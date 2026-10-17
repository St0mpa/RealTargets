using System;
using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using RealTargets.Patches;
using UnityEngine;

namespace RealTargets.Hits
{
    /// <summary>Pre-hit state captured by ApplyShotPatch's prefix.</summary>
    internal struct ShotSnapshot
    {
        public DamageInfo DamageInfo;
        public bool TargetAlive;
    }

    /// <summary>
    /// Groups the per-collider ApplyShot calls of one projectile into a HitEvent (published at
    /// end of frame), and the projectiles of one cartridge on one target into a HitVolley.
    /// A shell's pellets can land a frame apart at range, and a single bullet can cross a plate
    /// in one frame and the body in the next (both seen 2026-10-11), so a volley is published
    /// once all of a shell's pellets have arrived, or after [Hit model] Volley window seconds.
    /// </summary>
    internal static class HitCollector
    {
        /// <summary>Raised once per projectile per target, at the end of the frame it hit.</summary>
        public static event Action<HitEvent> Published;

        /// <summary>Raised once per cartridge per target, when the volley is complete.</summary>
        public static event Action<HitVolley> PublishedVolley;

        // ShotId keeps its parts private; read them without boxing. If a game update renames
        // them, volleys fall back to one projectile each instead of breaking hit capture.
        private static readonly AccessTools.StructFieldRef<ShotId, string> AmmoIdRef;
        private static readonly AccessTools.StructFieldRef<ShotId, int> FragmentRef;

        private static readonly Dictionary<(int targetId, ShotId shot), HitEvent> Pending =
            new Dictionary<(int, ShotId), HitEvent>();
        private static readonly List<HitEvent> Order = new List<HitEvent>();

        private sealed class OpenVolley
        {
            public HitVolley Volley;
            public float LastHitTime;
        }

        private static readonly Dictionary<(int targetId, string source), OpenVolley> Open =
            new Dictionary<(int, string), OpenVolley>();
        private static readonly List<(int, string)> ReadyKeys = new List<(int, string)>();

        static HitCollector()
        {
            try
            {
                AmmoIdRef = AccessTools.StructFieldRefAccess<ShotId, string>("_ammoId");
                FragmentRef = AccessTools.StructFieldRefAccess<ShotId, int>("_fragmentIndex");
            }
            catch (Exception ex)
            {
                AmmoIdRef = null;
                FragmentRef = null;
                Plugin.Log.LogWarning($"ShotId fields not found; hits won't group into volleys: {ex.Message}");
            }
        }

        internal static void Record(
            Player target,
            in ShotSnapshot before,
            in DamageInfo after,
            EBodyPart bodyPart,
            EBodyPartColliderType collider,
            EArmorPlateCollider plate,
            ShotId shotId,
            PlayerHitInfo result)
        {
            var info = before.DamageInfo;
            var health = target.HealthController;
            string sourceId = AmmoIdRef != null ? AmmoIdRef(ref shotId) : null;

            // The live Shot, if this hit is being resolved by GameWorld.ShotDelegate.
            var shot = ShotContext.Current;
            if (shot != null && (shot.Ammo == null || shot.Ammo.Id != sourceId))
            {
                shot = null;
            }

            var segment = new HitSegment(
                bodyPart,
                collider,
                plate,
                info.HitPoint,
                info.Damage,
                after.Damage,
                info.PenetrationPower,
                result != null && result.Penetrated,
                health != null && health.IsAlive,
                shot != null ? shot.VelocityMagnitude : 0f,
                shot != null && shot.Direction.sqrMagnitude > 0f ? shot.Direction.normalized : info.Direction.normalized);

            // Only bullets share a ShotId across colliders; anything else stands alone.
            bool groupable = info.DamageType == EDamageType.Bullet;
            var key = (target.Id, shotId);

            if (groupable && Pending.TryGetValue(key, out HitEvent existing))
            {
                existing.Segments.Add(segment);
                return;
            }

            var hit = new HitEvent
            {
                Target = target,
                TargetName = target.Profile?.Nickname ?? "?",
                IsAI = target.IsAI,
                IsLocalPlayer = target.IsYourPlayer,
                ShotId = shotId,
                SourceId = sourceId,
                FragmentIndex = FragmentRef != null ? FragmentRef(ref shotId) : -1,
                DamageType = info.DamageType,
                Weapon = info.Weapon,
                WeaponName = ItemName(info.Weapon),
                Origin = info.MasterOrigin,
                Direction = info.Direction,
                Distance = Vector3.Distance(info.MasterOrigin, info.HitPoint),
                WallTime = DateTime.Now,
                WasAlive = before.TargetAlive,
            };
            hit.Segments.Add(segment);

            if (shot != null)
            {
                hit.BulletMassGram = shot.BulletMassGram;
                hit.MuzzleSpeed = shot.InitialSpeed;
                if (shot.Ammo is Ammo ammo)
                {
                    hit.AmmoName = ItemName(ammo);
                    hit.Caliber = ammo.Caliber;
                    hit.ProjectileCount = Mathf.Max(1, ammo.ProjectileCount);
                }

                // EFT's pellet masses aren't physical (7 mm buckshot = 0.09 g, real ~2 g), so for
                // multi-projectile ammo use at least a lead sphere of the pellet's diameter.
                if (hit.ProjectileCount > 1 && shot.BulletDiameterMilimeters > 0f)
                {
                    hit.BulletMassGram = Mathf.Max(hit.BulletMassGram, LeadSphereGram(shot.BulletDiameterMilimeters));
                }
            }

            // Grenade shrapnel: physical fragment mass and speed (EFT's are ~0 J).
            if (GrenadeFragments.IsFragment(hit.AmmoName, info.DamageType))
            {
                var (mass, speed) = GrenadeFragments.At(hit.WeaponName, hit.Distance);
                hit.BulletMassGram = mass;
                hit.FragmentSpeed = speed;
            }

            if (groupable)
            {
                Pending[key] = hit;
            }
            Order.Add(hit);
        }

        /// <summary>
        /// A projectile leaving a body collider (EFT's backward hit). Attached to the projectile's
        /// HitEvent from this frame; an exit without a matching entry this frame is dropped.
        /// </summary>
        internal static void RecordExit(Player target, ShotId shotId, EBodyPartColliderType collider, bool plate, Vector3 point)
        {
            if (target != null && Pending.TryGetValue((target.Id, shotId), out HitEvent hit))
            {
                hit.Exits.Add((collider, plate, point));
            }
        }

        /// <summary>
        /// EFT doesn't always report the backward (exit) hit: a 12 ga slug through the head crossed
        /// ParietalHead and then BackHead at 465 m/s with no exit recorded, and counted as stopped
        /// (0.9.1 test). A projectile still fast when it enters a later body collider on its way
        /// through has gone out the far side: record an exit just past that collider's entry point.
        /// </summary>
        private static void InferExit(HitEvent hit)
        {
            foreach (var e in hit.Exits)
            {
                if (!e.plate) return;
            }
            int first = hit.Segments.FindIndex(s => !s.HasPlate);
            int last = hit.Segments.FindLastIndex(s => !s.HasPlate);
            if (first < 0) return;
            if (last > first)
            {
                var seg = hit.Segments[last];
                if (seg.Penetrated && seg.ImpactSpeed >= 150f)
                {
                    var dir = seg.Direction.sqrMagnitude > 0.5f ? seg.Direction.normalized : hit.Direction.normalized;
                    hit.Exits.Add((seg.Collider, false, seg.Point + dir * 0.03f));
                    return;
                }
            }
            InferExitThroughBody(hit, hit.Segments[first]);
        }

        /// <summary>
        /// 0.10.0 test: a 7.62x51 (2580 J) through a naked chest reported ONE collider and no exit, so it
        /// read "stopped". Here the body's far side along the path is found by raycasting its own
        /// colliders backwards from 1 m past the entry; if that thickness is within what the
        /// projectile can cross in tissue it went through: fragments ~8 cm, a buckshot pellet ~25 cm,
        /// bullets 20 cm + 1 cm per 25 J (9x19 ~40 cm, rifles up to 90 cm).
        /// </summary>
        private static void InferExitThroughBody(HitEvent hit, HitSegment entry)
        {
            var colliders = hit.Target != null && hit.Target.PlayerBones != null ? hit.Target.PlayerBones.BodyPartCollidersDictionary : null;
            if (colliders == null || !hit.HasBallistics) return;
            // Slow and blunt (0.10.5 test: an unarmed 40 mm grenade at 76 m/s read "through" the pelvis):
            // nothing under ~150 m/s crosses a body; flares, unarmed grenades and spent rounds stay in.
            if (hit.ImpactSpeed < 150f) return;

            float energy = hit.ImpactEnergy * Mathf.Max(hit.FleshFraction, 0.05f);
            // Pellets by energy (0.10.1: a 54 J #4 pellet "went through" a chest): 1 cm per 5 J, 4-30 cm.
            float reach = hit.IsFragment ? 0.08f : hit.ProjectileCount > 1 ? Mathf.Clamp(energy / 500f, 0.04f, 0.3f)
                : Mathf.Clamp(0.2f + energy / 2500f, 0.2f, 0.9f);

            var dir = entry.Direction.sqrMagnitude > 0.5f ? entry.Direction.normalized : hit.Direction.normalized;
            var ray = new Ray(entry.Point + dir * 1f, -dir);
            float best = float.MaxValue;
            var exitCollider = entry.Collider;
            Vector3 exit = default;
            foreach (var pair in colliders)
            {
                var bpc = pair.Value;
                if (bpc == null || bpc.Collider == null || bpc is ArmorPlateCollider) continue;
                if (bpc.Collider.Raycast(ray, out var info, 1f) && info.distance < best)
                {
                    best = info.distance;
                    exit = info.point;
                    exitCollider = pair.Key;
                }
            }

            if (best == float.MaxValue) return;
            float thickness = 1f - best;
            if (thickness < 0.03f || thickness > reach) return;
            hit.Exits.Add((exitCollider, false, exit + dir * 0.005f));
        }

        /// <summary>Drops everything in flight; called while no raid is running.</summary>
        internal static void Reset()
        {
            if (Order.Count > 0 || Open.Count > 0)
            {
                Order.Clear();
                Pending.Clear();
                Open.Clear();
            }
        }

        /// <summary>Called every frame in raid from Plugin.LateUpdate.</summary>
        internal static void Flush()
        {
            if (Order.Count > 0)
            {
                // Copy first: a subscriber may cause new hits while we publish.
                var finished = Order.ToArray();
                Order.Clear();
                Pending.Clear();

                try
                {
                    Finish(finished);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"HitEvent finish failed: {ex}");
                    return;
                }

                foreach (var hit in finished)
                {
                    Publish(Published, hit);
                    AddToVolley(hit);
                }
            }

            PublishReadyVolleys();
        }

        private static void AddToVolley(HitEvent hit)
        {
            // No id to group by (non-bullet damage): the projectile is its own volley.
            if (string.IsNullOrEmpty(hit.SourceId) || hit.Target == null)
            {
                var single = new HitVolley();
                single.Projectiles.Add(hit);
                Publish(PublishedVolley, single);
                return;
            }

            var key = (hit.Target.Id, hit.SourceId);
            if (!Open.TryGetValue(key, out OpenVolley open))
            {
                open = new OpenVolley { Volley = new HitVolley() };
                Open[key] = open;
            }

            open.Volley.Projectiles.Add(hit);
            open.LastHitTime = Time.time;
        }

        private static void PublishReadyVolleys()
        {
            if (Open.Count == 0)
            {
                return;
            }

            float now = Time.time;
            float window = Plugin.VolleyWindow.Value;
            ReadyKeys.Clear();

            foreach (var pair in Open)
            {
                var volley = pair.Value.Volley;
                // A shell is complete once every pellet has landed. A single bullet can't be known
                // complete: it may cross a plate in one frame and the body in the next (.50 BMG
                // slug, 2026-10-11), so it always waits for the window.
                int expected = volley.First.ProjectileCount;
                bool complete = expected > 1 && volley.Count >= expected;
                if (complete || now - pair.Value.LastHitTime >= window)
                {
                    ReadyKeys.Add(pair.Key);
                }
            }

            foreach (var key in ReadyKeys)
            {
                var volley = Open[key].Volley;
                Open.Remove(key);
                Publish(PublishedVolley, volley);
            }
        }

        /// <summary>End-of-frame state: exact status per projectile and body-part health.</summary>
        private static void Finish(HitEvent[] hits)
        {
            foreach (var hit in hits)
            {
                InferExit(hit);

                hit.Status = !hit.WasAlive ? HitStatus.Corpse
                    : hit.AliveAfter ? HitStatus.Alive
                    : HitStatus.Killed;

                var health = hit.Target != null ? hit.Target.HealthController : null;
                if (health == null)
                {
                    continue;
                }

                foreach (var segment in hit.Segments)
                {
                    if (!hit.HealthAfter.ContainsKey(segment.BodyPart))
                    {
                        var value = health.GetBodyPartHealth(segment.BodyPart, true);
                        hit.HealthAfter[segment.BodyPart] = (value.Current, value.Maximum);
                    }
                }
            }

            // If death is applied after ApplyShot returns (seen on the local player), no
            // projectile saw the target die; credit the last one that hit it alive.
            var credited = new HashSet<int>();
            for (int i = hits.Length - 1; i >= 0; i--)
            {
                var hit = hits[i];
                if (hit.Target == null || credited.Contains(hit.Target.Id))
                {
                    continue;
                }

                credited.Add(hit.Target.Id);
                var health = hit.Target.HealthController;
                bool deadNow = health != null && !health.IsAlive;
                if (!deadNow || HasKiller(hits, hit.Target.Id))
                {
                    continue;
                }

                for (int j = i; j >= 0; j--)
                {
                    if (hits[j].Target == hit.Target && hits[j].WasAlive)
                    {
                        hits[j].Status = HitStatus.Killed;
                        break;
                    }
                }
            }
        }

        private static bool HasKiller(HitEvent[] hits, int targetId)
        {
            foreach (var hit in hits)
            {
                if (hit.Target != null && hit.Target.Id == targetId && hit.Status == HitStatus.Killed)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Publish<T>(Action<T> handler, T item)
        {
            if (handler == null || !Plugin.ModEnabled.Value)
            {
                return;
            }

            // Isolate subscribers so one failing feature doesn't starve the others.
            foreach (Action<T> subscriber in handler.GetInvocationList())
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    subscriber(item);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"{typeof(T).Name} subscriber failed: {ex}");
                }
                // 0.10.14 (0.10.13 raid: a 68 ms `Hits` frame the logged bakes don't explain): name the slow feature.
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms >= 8.0 && Plugin.PerfLogging)
                {
                    Plugin.Log.LogInfo($"[Perf] slow hit handler {ms:F1} ms: {subscriber.Method.DeclaringType?.Name}.{subscriber.Method.Name}");
                }
            }
        }

        /// <summary>Mass of a lead sphere (11.34 g/cm3) of the given diameter, grams.</summary>
        private static float LeadSphereGram(float diameterMm)
        {
            float r = diameterMm / 20f; // mm diameter -> cm radius
            return 11.34f * 4f / 3f * Mathf.PI * r * r * r;
        }

        internal static string ItemName(Item item)
        {
            if (item == null)
            {
                return "none";
            }

            try
            {
                return LocalizationExtensions.LocalizedShortName(item);
            }
            catch (Exception)
            {
                return item.TemplateId.ToString();
            }
        }
    }
}
