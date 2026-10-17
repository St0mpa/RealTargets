using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using RealTargets.Hits;
using UnityEngine;

namespace RealTargets.Features
{
    /// <summary>
    /// Physical side of a hit (0.6.0; living knockdown removed with the living ragdoll in 0.10.0):
    /// - dead bodies are pushed at the hit bone by the bullet's momentum (x Impacts / Push strength;
    ///   a round that exits passes on ~35 %). The killing shot is left to EFT's own death impulse
    ///   (shaped by Ragdoll Kinetics);
    /// - frozen corpses (EFT made their bodies kinematic once still) are woken for a few seconds so
    ///   later shots move them, then frozen again.
    /// </summary>
    internal sealed class Impacts : MonoBehaviour
    {
        private sealed class Woken
        {
            public Player Player;
            public readonly List<Rigidbody> Bodies = new List<Rigidbody>();
            public float Since;
            public float StillSince = -1f;
        }

        private const float MaxImpulse = 60f;
        private const float WokenMaxSeconds = 8f;

        private readonly Dictionary<int, Woken> _woken = new Dictionary<int, Woken>();
        private const float MaxDeltaV = 4f;

        internal static Impacts Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            HitCollector.PublishedVolley += OnVolley;
        }

        private void OnDestroy()
        {
            HitCollector.PublishedVolley -= OnVolley;
            RefreezeAll();
        }

        internal void ResetRaid()
        {
            _woken.Clear();
        }

        private void OnVolley(HitVolley volley)
        {
            var target = volley.Target;
            if (!Plugin.ImpactsEnabled.Value || target == null || target.IsYourPlayer)
            {
                return;
            }

            try
            {
                bool alive = target.ActiveHealthController != null && target.ActiveHealthController.IsAlive;
                if (alive || !Plugin.PushCorpses.Value)
                {
                    return;
                }

                foreach (var p in volley.Projectiles)
                {
                    if (!p.HasBallistics || p.Segments.Count == 0 || p.Status == HitStatus.Killed)
                    {
                        continue;
                    }

                    Push(target, p, wakeFrozen: true);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Impacts: {ex.Message}");
            }
        }

        private static float Impulse(HitEvent p)
        {
            bool exited = false;
            foreach (var e in p.Exits)
            {
                if (!e.plate) exited = true;
            }
            return Mathf.Min(p.ImpactMomentum * (exited ? 0.35f : 1f) * Plugin.PushStrength.Value, MaxImpulse);
        }

        private static Vector3 Direction(HitEvent p) =>
            p.Entry.Direction.sqrMagnitude > 0.5f ? p.Entry.Direction : p.Direction.normalized;

        private void Push(Player target, HitEvent p, bool wakeFrozen)
        {
            var rb = BodyAt(target, p.Entry.Collider, p.Entry.Point);
            if (rb == null)
            {
                return;
            }

            if (rb.isKinematic)
            {
                if (!wakeFrozen || !WakeCorpse(target))
                {
                    return;
                }
            }

            // A light body (hand, foot) must not be flung: cap the velocity change (0.7.1 test:
            // limbs stretched on corpses hit several times).
            float impulse = Mathf.Min(Impulse(p), rb.mass * MaxDeltaV);
            rb.AddForceAtPosition(Direction(p) * impulse, p.Entry.Point, ForceMode.Impulse);
            if (Plugin.LogBlood.Value)
            {
                Plugin.Log.LogInfo($"[Impact] {target.Profile?.Nickname}: push {impulse:F1} N s at {p.Entry.Collider} " +
                                   $"({(wakeFrozen ? "corpse" : "limp")}{(_woken.ContainsKey(target.Id) ? ", woken" : "")}, {p.WeaponName})");
            }
        }

        /// <summary>The ragdoll body carrying a hit collider (or the nearest one to the point).</summary>
        internal static Rigidbody BodyAt(Player player, EBodyPartColliderType type, Vector3 point)
        {
            var colliders = player.PlayerBones != null ? player.PlayerBones.BodyPartCollidersDictionary : null;
            if (colliders != null && colliders.TryGetValue(type, out var bpc) && bpc != null && bpc.Collider != null)
            {
                var rb = bpc.Collider.attachedRigidbody;
                if (rb == null) rb = bpc.Collider.GetComponentInParent<Rigidbody>();
                if (rb != null) return rb;
            }

            var pool = player.GetComponent<PlayerPoolObject>() ?? player.GetComponentInChildren<PlayerPoolObject>();
            Rigidbody best = null;
            float bestDist = float.MaxValue;
            if (pool?.RigidbodySpawners != null)
            {
                foreach (var spawner in pool.RigidbodySpawners)
                {
                    var body = spawner != null ? spawner.Rigidbody : null;
                    if (body == null) continue;
                    float d = (body.worldCenterOfMass - point).sqrMagnitude;
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = body;
                    }
                }
            }
            return best;
        }

        /// <summary>Push at the body nearest a point (blasts): wakes a still corpse if needed.</summary>
        internal void Push(Player target, Vector3 point, Vector3 impulse, bool corpse, string source)
        {
            try
            {
                var rb = BodyAt(target, (EBodyPartColliderType)(-1), point);
                if (rb == null) return;
                if (rb.isKinematic && (!corpse || !Plugin.PushCorpses.Value || !WakeCorpse(target))) return;
                var capped = Vector3.ClampMagnitude(impulse, rb.mass * MaxDeltaV * 1.5f);
                rb.AddForceAtPosition(capped, point, ForceMode.Impulse);
                if (Plugin.LogBlood.Value)
                {
                    Plugin.Log.LogInfo($"[Impact] {target.Profile?.Nickname}: push {capped.magnitude:F1} N s ({(corpse ? "corpse" : "limp")}, {source})");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Impacts push: {ex.Message}");
            }
        }

        /// <summary>
        /// Makes a still (kinematic) corpse physical again until it settles. Only if its joints are
        /// all still there: EFT removes them once a settled body is out of sight, and waking bodies
        /// without joints lets the parts fly apart (stretched limbs, 0.7.1 test).
        /// </summary>
        private bool WakeCorpse(Player target)
        {
            if (_woken.TryGetValue(target.Id, out var existing))
            {
                existing.Since = Time.time;
                existing.StillSince = -1f;
                return true;
            }

            var pool = target.GetComponent<PlayerPoolObject>() ?? target.GetComponentInChildren<PlayerPoolObject>();
            if (pool?.RigidbodySpawners == null)
            {
                return false;
            }

            if (pool.JointSpawners != null)
            {
                foreach (var joint in pool.JointSpawners)
                {
                    if (joint != null && joint.GetComponent<Joint>() == null)
                    {
                        return false;
                    }
                }
            }

            var woken = new Woken { Player = target, Since = Time.time };
            foreach (var spawner in pool.RigidbodySpawners)
            {
                var body = spawner != null ? spawner.Rigidbody : null;
                if (body == null || !body.isKinematic) continue;
                body.isKinematic = false;
                body.WakeUp();
                PhysicsExtensions.UpdateController.SupportRigidbody(body, 0f);
                woken.Bodies.Add(body);
            }

            if (woken.Bodies.Count == 0)
            {
                return false;
            }
            _woken[target.Id] = woken;
            return true;
        }

        private void Update()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Impacts);
            if (_woken.Count == 0)
            {
                return;
            }

            List<int> done = null;
            float now = Time.time;
            foreach (var pair in _woken)
            {
                var w = pair.Value;
                bool still = true, any = false;
                foreach (var body in w.Bodies)
                {
                    if (body == null) continue;
                    any = true;
                    if (body.velocity.sqrMagnitude > 0.02f || body.angularVelocity.sqrMagnitude > 0.1f) still = false;
                }

                if (still && w.StillSince < 0f) w.StillSince = now;
                if (!still) w.StillSince = -1f;

                if (!any || now - w.Since > WokenMaxSeconds || (w.StillSince >= 0f && now - w.StillSince > 1f && now - w.Since > 1f))
                {
                    Freeze(w);
                    (done ??= new List<int>()).Add(pair.Key);
                }
            }

            if (done != null)
            {
                foreach (var id in done) _woken.Remove(id);
            }
        }

        private void RefreezeAll()
        {
            foreach (var w in _woken.Values) Freeze(w);
            _woken.Clear();
        }

        private static void Freeze(Woken w)
        {
            foreach (var body in w.Bodies)
            {
                if (body == null) continue;
                PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
                body.isKinematic = true;
            }
            PhysicsExtensions.UpdateController.CheckRigidbodiesDone();
        }
    }
}
