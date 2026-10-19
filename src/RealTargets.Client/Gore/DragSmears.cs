using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Blood dragged along the floor behind a moved corpse (Stompa, 2026-10-18: TraumaCore's body dragging). Not tied to
    /// TraumaCore: any corpse that slides over the floor smears, whether TraumaCore's drag, a blast shove or another mod
    /// moves it. gore-5 (gore-4 test: a body dragged by its lifted arm only dripped; a stump on the floor left a painted
    /// strip with a ladder edge): every bleeding corpse is tracked with its contact points, the bottom of each body-part
    /// collider (head, chest, lower back, pelvis, upper arms, forearms, thighs, calves) plus each wound opening and stump.
    /// A point picks up blood (Soak 0-1) from a wet pool it lies or slides in and from a wound within reach of it; it loses
    /// ~25 % per metre of streak and dries with a 3 min half-life. Sampled every 0.1 s from 2 s after death; a point on the floor
    /// (15 cm) at two samples in a row that slid 4 cm-1 m lays overlapping streak steps (GroundBlood.DragStreak: even
    /// width, stripes along the drag, wet then drying cells) every half streak length, so they join into one trail that
    /// thins out along the drag. Never from a removed part (bone scale &lt; 0.3).
    /// </summary>
    internal sealed class DragSmears : MonoBehaviour
    {
        private const float Tick = 0.1f;
        private const float Contact = 0.15f;
        private const int MaxCorpses = 40;
        private const float ForgetAfter = 1200f;
        private const float SoakHalfLife = 180f;
        /// <summary>Soak lost per metre of streak: e^(-d/3.5 m), ~25 % per metre (a torso trail thins out over 4-6 m).</summary>
        private const float SoakMetres = 3.5f;
        private const float MinSoak = 0.05f;

        private static readonly (EBodyPartColliderType type, float width)[] Parts =
        {
            (EBodyPartColliderType.HeadCommon, 0.12f),
            (EBodyPartColliderType.RibcageUp, 0.24f),
            (EBodyPartColliderType.RibcageLow, 0.22f),
            (EBodyPartColliderType.Pelvis, 0.22f),
            (EBodyPartColliderType.LeftUpperArm, 0.1f),
            (EBodyPartColliderType.RightUpperArm, 0.1f),
            (EBodyPartColliderType.LeftForearm, 0.08f),
            (EBodyPartColliderType.RightForearm, 0.08f),
            (EBodyPartColliderType.LeftThigh, 0.13f),
            (EBodyPartColliderType.RightThigh, 0.13f),
            (EBodyPartColliderType.LeftCalf, 0.1f),
            (EBodyPartColliderType.RightCalf, 0.1f),
        };

        private sealed class Point
        {
            public string Name;
            public Collider Collider;
            public BleedSite Site;
            public float Width;
            public float Soak;
            public Vector3 Last;
            public bool HasLast, LastOnFloor;
            public float Travel;
            public int Steps;
            public float LoggedAt = -100f;
        }

        private sealed class Corpse
        {
            public Player Player;
            public Bleeder Bleeder;
            public float AddedAt;
            public readonly List<Point> Points = new List<Point>();
            public readonly HashSet<BleedSite> Sites = new HashSet<BleedSite>();
        }

        private readonly Dictionary<int, Corpse> _corpses = new Dictionary<int, Corpse>();
        private readonly List<int> _gone = new List<int>();
        private float _next;

        internal void ResetRaid() => _corpses.Clear();

        private void Update()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Ground);
            if (!Plugin.ModEnabled.Value || !Plugin.VisualsEnabled.Value || !Plugin.VisualsDragSmears.Value) return;
            float now = Time.time;
            if (now < _next) return;
            float dt = Tick + Mathf.Max(0f, now - _next);
            _next = now + Tick;
            var ground = GroundBlood.Instance;
            var system = BleedSystem.Instance;
            if (ground == null || system == null) return;

            // Dead bleeding bodies (the bleeder is dropped 2 min after it stops draining; the corpse and its blood stay).
            foreach (var b in system.Bleeders)
            {
                if (!b.IsDead || b.Player == null) continue;
                if (!_corpses.TryGetValue(b.Player.Id, out var corpse))
                {
                    if (_corpses.Count >= MaxCorpses) continue;
                    corpse = Track(b, now);
                    if (corpse == null) continue;
                    _corpses[b.Player.Id] = corpse;
                }
                foreach (var s in b.Sites)
                {
                    if (s.Bone == null || !corpse.Sites.Add(s)) continue;
                    // A stump is wider than a bullet hole.
                    corpse.Points.Add(new Point { Name = s.Weapon != null && s.Weapon.Contains("stump") ? $"{s.Collider} stump" : $"{s.Collider} wound", Site = s, Width = s.Weapon != null && s.Weapon.Contains("stump") ? 0.12f : 0.08f });
                }
            }
            if (_corpses.Count == 0) return;

            var cam = Camera.main;
            float maxDistance = Plugin.VisualsDistance.Value;
            float dry = Mathf.Pow(0.5f, dt / SoakHalfLife);
            _gone.Clear();
            foreach (var pair in _corpses)
            {
                var corpse = pair.Value;
                if (corpse.Player == null || corpse.Player.gameObject == null || !corpse.Player.gameObject.activeInHierarchy ||
                    now - corpse.AddedAt > ForgetAfter || (corpse.Player.HealthController != null && corpse.Player.HealthController.IsAlive))
                {
                    _gone.Add(pair.Key);
                    continue;
                }
                // Not while the body is still falling (gore-3 test: smears logged at the kill).
                if (corpse.Bleeder.DiedAt < 0f || now - corpse.Bleeder.DiedAt < 2f) continue;
                if (cam != null && (corpse.Player.Position - cam.transform.position).sqrMagnitude > maxDistance * maxDistance)
                {
                    foreach (var p in corpse.Points) p.HasLast = false;
                    continue;
                }
                try
                {
                    foreach (var p in corpse.Points)
                    {
                        p.Soak *= dry;
                        Sample(corpse, p, ground, now);
                    }
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"Drag smears: {ex.Message}");
                    _gone.Add(pair.Key);
                }
            }
            foreach (var id in _gone) _corpses.Remove(id);
        }

        private static Corpse Track(Bleeder b, float now)
        {
            var colliders = b.Player.PlayerBones?.BodyPartCollidersDictionary;
            if (colliders == null) return null;
            var corpse = new Corpse { Player = b.Player, Bleeder = b, AddedAt = now };
            foreach (var (type, width) in Parts)
            {
                if (colliders.TryGetValue(type, out var bpc) && bpc?.Collider != null)
                {
                    corpse.Points.Add(new Point { Name = type.ToString(), Collider = bpc.Collider, Width = width });
                }
            }
            return corpse;
        }

        /// <summary>Where the point touches the floor now: the bottom of its collider, or the wound's lowest opening.</summary>
        private static bool Where(Point p, out Vector3 at)
        {
            at = default;
            if (p.Site != null)
            {
                if (p.Site.Bone == null || p.Site.Bone.lossyScale.x < 0.3f) return false;
                at = p.Site.Lowest.position;
                return true;
            }
            if (p.Collider == null || !p.Collider.enabled || p.Collider.transform.lossyScale.x < 0.3f) return false;
            var bounds = p.Collider.bounds;
            // ClosestPoint needs a primitive or convex collider; otherwise the bottom of the bounds.
            at = p.Collider is MeshCollider mesh && !mesh.convex
                ? bounds.center - Vector3.up * bounds.extents.y
                : p.Collider.ClosestPoint(bounds.center + Vector3.down * 2f);
            return true;
        }

        private static void Sample(Corpse corpse, Point p, GroundBlood ground, float now)
        {
            if (!Where(p, out var at))
            {
                p.HasLast = false;
                return;
            }
            if (!p.HasLast)
            {
                p.Last = at;
                p.HasLast = true;
                p.LastOnFloor = OnFloor(at, out _);
                return;
            }

            var moved = at - p.Last;
            var flat = Vector3.ProjectOnPlane(moved, Vector3.up);
            if (flat.sqrMagnitude < 0.04f * 0.04f) return; // lying still (or only lifted): keep the old sample
            var from = p.Last;
            p.Last = at;
            bool wasOnFloor = p.LastOnFloor;
            p.LastOnFloor = OnFloor(at, out var hit);
            // A slide keeps the point on the floor at both samples (landing or tumbling: it came from the air);
            // more than 1 m in 0.1 s is a throw or a teleport: no streak across the gap.
            if (!p.LastOnFloor || !wasOnFloor || flat.sqrMagnitude > 1f) return;

            // Picks up blood: a wet pool under it, a wound of this body next to it.
            float pool = ground.PoolWetness(hit.point);
            if (pool > p.Soak) p.Soak = pool;
            foreach (var s in corpse.Bleeder.Sites)
            {
                if (s.Bone == null || s.Drained < 1f) continue;
                float reach = 0.1f + p.Width * 0.5f;
                if ((s.Lowest.position - at).sqrMagnitude > reach * reach) continue;
                float wet = Mathf.Clamp01(s.Drained / 150f);
                if (wet > p.Soak) p.Soak = wet;
            }
            if (p.Soak < MinSoak) return;

            // Steps every half streak length (a streak is 2 widths long): each overlaps the last, one trail.
            float spacing = p.Width;
            float length = flat.magnitude;
            var dir = flat / length;
            var floorFrom = from;
            floorFrom.y = hit.point.y;
            int laid = 0;
            p.Travel += length;
            while (p.Travel >= spacing && laid < 10 && p.Soak >= MinSoak)
            {
                p.Travel -= spacing;
                float back = p.Travel; // how far behind the current point this step sits
                var stamp = hit.point - dir * back;
                float width = p.Width * Mathf.Lerp(0.6f, 1f, p.Soak) * Random.Range(0.9f, 1.1f);
                ground.DragStreak(stamp + hit.normal * 0.002f, hit.normal, dir, width, p.Soak);
                p.Soak *= Mathf.Exp(-spacing / SoakMetres);
                p.Steps++;
                laid++;
            }

            if (laid > 0 && Plugin.LogBlood.Value && now - p.LoggedAt > 2f)
            {
                p.LoggedAt = now;
                Plugin.Log.LogInfo($"[Drag] {corpse.Player.Profile?.Nickname} {p.Name}: streak {length * 100f:F0} cm ({laid} step(s), " +
                                   $"soak {p.Soak:F2}, {p.Steps} so far{(pool > 0f ? ", through a pool" : "")})");
            }
        }

        private static bool OnFloor(Vector3 at, out RaycastHit hit) =>
            Physics.Raycast(at + Vector3.up * 0.1f, Vector3.down, out hit, 0.1f + Contact, LayersMaskController.HighPolyWithTerrainMask) &&
            !GroundBlood.Movable(hit.collider) && hit.normal.y >= 0.5f;
    }
}
