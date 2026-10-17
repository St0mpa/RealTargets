using System.Collections.Generic;
using EFT;
using EFT.HealthSystem;
using RealTargets.Hits;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// The visual bleed model (0.10.0, ADR 0008). Every projectile that enters a bot (alive or dead)
    /// becomes a BleedSite whose look comes from where it hit and how hard, not from organs:
    /// - flow: ~60 ml/min soft tissue x sqrt(energy / 1700 J), doubled if it went through; head
    ///   (scalp/face) x2.5 and neck x2 bleed more (0.10.1: x3, 0.10.0 corpses drained only 5-60 ml);
    /// - chest, belly and head wounds also fill a cavity (150-900 ml by energy) that runs out of that
    ///   part's holes: slowly while alive, 30 %/min x posture after death (the big corpse pools);
    /// - kind: ooze / flow / heavy by that flow; an artery is cut by chance where big ones run
    ///   (neck, groin, thigh, upper arm, smaller ones in forearm/calf/face), likelier with energy;
    /// - alive: flows clot down (half-life by kind, arteries don't); pressure and heart rate rise
    ///   with blood lost; when the bot treats a part (EFT bleed effect gone) its sites trickle; an
    ///   EFT bleed effect still on a part keeps its biggest site from clotting below heavy/light;
    /// - dead (EFT decides): a few agonal beats with falling pressure, then gravity drainage from
    ///   whichever opening faces down, tapering with `Post-mortem drain half-life`;
    /// - chest and head wounds put blood in the airway/nose: coughed out while alive, running from
    ///   the mouth when lying or dead.
    /// Nothing here touches EFT's health or AI.
    /// </summary>
    internal sealed class BleedSystem : MonoBehaviour
    {
        internal static BleedSystem Instance { get; private set; }

        private const float TickSeconds = 0.2f;
        private const float KeepDrainedSeconds = 120f;
        /// <summary>A body can't lose more than this share of its blood outside.</summary>
        private const float MaxLossFraction = 0.65f;

        private readonly Dictionary<int, Bleeder> _bleeders = new Dictionary<int, Bleeder>();
        private readonly List<int> _remove = new List<int>();
        private float _nextTick;

        internal IEnumerable<Bleeder> Bleeders => _bleeders.Values;
        internal int Count => _bleeders.Count;

        internal static Bleeder Find(Player player)
        {
            if (Instance == null || player == null) return null;
            Instance._bleeders.TryGetValue(player.Id, out var b);
            return b;
        }

        private void Awake()
        {
            Instance = this;
            HitCollector.Published += OnHit;
        }

        private void OnDestroy()
        {
            HitCollector.Published -= OnHit;
            if (Instance == this) Instance = null;
        }

        internal void ResetRaid() => _bleeders.Clear();

        /// <param name="corpse">The body was already dead before this hit (not killed by it).</param>
        internal Bleeder GetOrCreate(Player player, bool corpse)
        {
            if (!_bleeders.TryGetValue(player.Id, out var b))
            {
                b = new Bleeder(player);
                // Hits are published at end of frame, when the killing shot's target already reads
                // dead: only a hit on an existing corpse starts without a heartbeat. The killing shot
                // starts alive; the tick sees the death and plays the agonal beats.
                if (corpse)
                {
                    // First hit on a body that was already dead: no heartbeat at all.
                    b.DiedAt = Time.time - Plugin.AgonalSeconds.Value - 1f;
                    b.PressureAtDeath = 0f;
                }
                _bleeders[player.Id] = b;
                _nextTick = Mathf.Min(_nextTick, Time.time);
            }
            return b;
        }

        // ---- Hits -> sites -----------------------------------------------------------------------

        private void OnHit(HitEvent hit)
        {
            if (!Plugin.VisualsEnabled.Value || hit.Target == null || hit.IsLocalPlayer || !hit.IsAI)
            {
                return;
            }

            try
            {
                if (!WoundPath.Resolve(hit, out var path)) return;
                var bleeder = GetOrCreate(hit.Target, hit.Status == HitStatus.Corpse);
                bleeder.Hits++;
                if (hit.Status == HitStatus.Killed) bleeder.KillingHit = hit;
                AddSite(bleeder, hit, path);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Bleeding: {ex.Message}");
            }
        }

        private static void AddSite(Bleeder bleeder, HitEvent hit, WoundPath path)
        {
            var player = bleeder.Player;
            var colliders = player.PlayerBones != null ? player.PlayerBones.BodyPartCollidersDictionary : null;
            BodyPartCollider bpc = null;
            colliders?.TryGetValue(path.Entry.Collider, out bpc);
            var bone = bpc != null ? bpc.TransformCached : player.Transform.Original;

            var c = path.Entry.Collider;
            float e = path.Energy;
            float energyFactor = Mathf.Clamp(Mathf.Sqrt(e / 1700f), 0.35f, 2.5f);
            float flow = 60f * energyFactor * (path.Exited ? 2f : 1f);
            if (WoundPath.IsHead(c)) flow *= 2.5f;
            else if (WoundPath.IsNeck(c)) flow *= 2f;
            flow *= Plugin.BleedMultiplier.Value;

            var site = new BleedSite
            {
                BodyPart = path.Entry.BodyPart,
                Collider = c,
                Weapon = hit.WeaponName,
                Energy = e,
                CreatedAt = Time.time,
                Bone = bone,
                LocalEntry = bone.InverseTransformPoint(path.Start),
                LocalOutward = bone.InverseTransformDirection(-path.Direction),
                HasExit = path.Exited,
                LocalExit = bone.InverseTransformPoint(path.End),
                LocalExitOutward = bone.InverseTransformDirection(path.Direction),
                Flow = flow,
                Kind = flow < 60f ? BleedKind.Ooze : flow < 200f ? BleedKind.Flow : BleedKind.Heavy,
            };

            // Arteries run where they run: a hit there cuts one by chance, more likely the bigger
            // the temporary cavity (energy). Torso arteries bleed inside: no external spurt.
            var (chance, arterialFlow) = Artery(c);
            chance *= Mathf.Clamp01(0.35f + e / 2500f) * Plugin.ArterialChance.Value;
            if (chance > 0f && Random.value < chance)
            {
                site.Kind = BleedKind.Arterial;
                site.Flow = Mathf.Max(site.Flow, arterialFlow * Random.Range(0.7f, 1.2f) * Plugin.BleedMultiplier.Value);
            }
            site.ClotHalfLife = site.Kind == BleedKind.Arterial ? 0f :
                site.Kind == BleedKind.Heavy ? 200f : site.Kind == BleedKind.Flow ? 110f : 50f;

            // Chest wounds bleed into the lungs/airway; head and face wounds into the nose and mouth.
            float airway = 0f;
            if (WoundPath.IsChest(c) && Random.value < Mathf.Clamp(0.5f + e / 2000f, 0.5f, 0.95f))
            {
                airway = Mathf.Clamp(0.5f + e / 3000f, 0.5f, 1f);
            }
            else if (WoundPath.IsHead(c))
            {
                airway = 0.7f;
            }
            if (airway > bleeder.MouthBlood)
            {
                bleeder.MouthBlood = airway;
                bleeder.Log($"blood in the airway {airway:P0}");
            }

            // Blood collecting inside the chest, belly or skull: it comes out of the holes over minutes.
            var part = path.Entry.BodyPart;
            if (part == EBodyPart.Chest || part == EBodyPart.Stomach || part == EBodyPart.Head)
            {
                // 0.10.2: raised (0.10.1 test: corpses still lost only ~170-330 ml in all).
                float add = part == EBodyPart.Head ? Mathf.Clamp(250f + e * 0.15f, 250f, 800f) : Mathf.Clamp(300f + e * 0.3f, 300f, 1500f);
                bleeder.Cavity.TryGetValue(part, out float have);
                bleeder.Cavity[part] = have + add * Plugin.BleedMultiplier.Value;
            }

            if (bleeder.IsDead)
            {
                // A wound made after death only drains (and what the cavity squeezed out, ImpactBlood).
                site.DrainRate = site.Flow * (site.Kind == BleedKind.Arterial ? 0.4f : 0.6f);
                if (site.Kind == BleedKind.Arterial) site.Kind = BleedKind.Heavy;
                bleeder.DrainDone = false;
            }

            bleeder.Sites.Add(site);
            bleeder.Log($"{site.Kind} {c}{(site.HasExit ? " (through)" : "")} {site.Flow:F0} ml/min, {hit.WeaponName} {e:F0} J" +
                        (bleeder.IsDead ? " [corpse]" : hit.Status == HitStatus.Killed ? " [killing shot]" : ""));
        }

        /// <summary>
        /// A bleed site on a stump (dismemberment): open vessels at a cut, on <paramref name="bone"/>
        /// at <paramref name="at"/>, pointing <paramref name="outward"/>. Arterial ones spurt while the
        /// heart (or its agonal beats) still pumps; on a long-dead body they only drain. Adds
        /// <paramref name="cavityMl"/> of blood that runs out of the stump over the following minutes.
        /// </summary>
        internal void AddStump(Player player, bool corpse, Transform bone, EBodyPart part, EBodyPartColliderType collider,
            Vector3 at, Vector3 outward, float flow, bool arterial, float cavityMl, string label)
        {
            if (player == null || bone == null) return;
            var bleeder = GetOrCreate(player, corpse);
            var site = new BleedSite
            {
                BodyPart = part,
                Collider = collider,
                Weapon = label,
                CreatedAt = Time.time,
                Bone = bone,
                LocalEntry = bone.InverseTransformPoint(at),
                LocalOutward = bone.InverseTransformDirection(outward.normalized),
                Flow = flow * Plugin.BleedMultiplier.Value,
                Kind = arterial ? BleedKind.Arterial : BleedKind.Heavy,
                ClotHalfLife = arterial ? 0f : 300f,
            };
            if (bleeder.IsDead && !bleeder.Agonal)
            {
                site.DrainRate = site.Flow * (arterial ? 0.4f : 0.6f);
                site.Kind = BleedKind.Heavy;
                bleeder.DrainDone = false;
            }
            bleeder.Sites.Add(site);
            if (cavityMl > 0f)
            {
                bleeder.Cavity.TryGetValue(part, out float have);
                bleeder.Cavity[part] = have + cavityMl * Plugin.BleedMultiplier.Value;
            }
            bleeder.Log($"stump {label}: {site.Kind} {site.Flow:F0} ml/min" + (cavityMl > 0f ? $", +{cavityMl:F0} ml to drain" : ""));
        }

        /// <summary>Chance a hit here cuts an artery, and that artery's flow (ml/min at full pressure).</summary>
        private static (float chance, float flow) Artery(EBodyPartColliderType c)
        {
            switch (c)
            {
                case EBodyPartColliderType.NeckFront: return (0.55f, 900f);   // carotid / jugular
                case EBodyPartColliderType.NeckBack: return (0.15f, 600f);    // vertebral
                case EBodyPartColliderType.Pelvis: return (0.2f, 1000f);      // femoral at the groin, iliac branches
                case EBodyPartColliderType.LeftThigh:
                case EBodyPartColliderType.RightThigh: return (0.22f, 1000f); // femoral / profunda
                case EBodyPartColliderType.LeftUpperArm:
                case EBodyPartColliderType.RightUpperArm: return (0.15f, 500f); // brachial
                case EBodyPartColliderType.LeftForearm:
                case EBodyPartColliderType.RightForearm:
                case EBodyPartColliderType.LeftCalf:
                case EBodyPartColliderType.RightCalf: return (0.08f, 250f);   // radial/ulnar, tibial
                case EBodyPartColliderType.Jaw: return (0.12f, 250f);         // facial
                default: return (0f, 0f);
            }
        }

        // ---- Tick --------------------------------------------------------------------------------

        private void Update()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Bleed);
            if (_bleeders.Count == 0 || Time.time < _nextTick) return;

            float dt = Mathf.Min(TickSeconds + Mathf.Max(0f, Time.time - _nextTick), 1f);
            _nextTick = Time.time + TickSeconds;

            _remove.Clear();
            foreach (var pair in _bleeders)
            {
                var b = pair.Value;
                var player = b.Player;
                if (player == null || player.gameObject == null)
                {
                    _remove.Add(pair.Key);
                    continue;
                }

                try
                {
                    var health = player.ActiveHealthController;
                    if (!b.IsDead && (health == null || !health.IsAlive))
                    {
                        b.PressureAtDeath = b.Pressure;
                        b.HeartRateAtDeath = b.HeartRate;
                        b.DiedAt = Time.time;
                        b.Log("died (EFT)");
                    }

                    if (b.IsDead) TickDead(b, dt);
                    else TickAlive(b, health, dt);

                    if (b.DrainDone && Time.time - b.DiedAt > KeepDrainedSeconds) _remove.Add(pair.Key);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"Bleeding tick {b.Name}: {ex}");
                    b.DrainDone = true;
                }
            }

            foreach (var id in _remove) _bleeders.Remove(id);
        }

        private static void TickAlive(Bleeder b, ActiveHealthController health, float dt)
        {
            SyncTreatment(b, health);

            float pressureFactor = 0.25f + 0.75f * b.Pressure;
            bool empty = b.LossFraction >= MaxLossFraction;
            foreach (var s in b.Sites)
            {
                if (s.ClotHalfLife > 0f && !s.Treated)
                {
                    s.Flow *= Mathf.Pow(0.5f, dt / s.ClotHalfLife);
                }
                s.Rate = empty || s.Bone == null ? 0f : s.Flow * pressureFactor;
            }
            if (!empty) LeakCavities(b, 0.06f, dt);
            bool prone = b.Player.IsInPronePose;
            foreach (var s in b.Sites) Drain(b, s, s.Rate, dt, prone);
            UpdatePool(b, dt);

            // Airway blood is slowly cleared by coughing.
            b.MouthBlood = Mathf.Max(0f, b.MouthBlood - dt / 240f);
        }

        /// <summary>
        /// EFT's bleed effects stay the gameplay truth: a part the bot treated (effect gone) only
        /// trickles; an effect still running keeps the part's biggest site from clotting below
        /// heavy (150 ml/min) or light (35 ml/min) bleeding.
        /// </summary>
        private static void SyncTreatment(Bleeder b, ActiveHealthController health)
        {
            if (health == null) return;
            foreach (var part in Parts)
            {
                bool heavy = health.FindExistingEffect<ActiveHealthController.HeavyBleeding>(part) != null;
                bool light = !heavy && health.FindExistingEffect<ActiveHealthController.LightBleeding>(part) != null;

                if (heavy || light)
                {
                    b.EftBleeding.Add(part);
                    BleedSite biggest = null;
                    foreach (var s in b.Sites)
                    {
                        if (s.BodyPart == part && !s.Treated && (biggest == null || s.Flow > biggest.Flow)) biggest = s;
                    }
                    if (biggest != null) biggest.Flow = Mathf.Max(biggest.Flow, heavy ? 150f : 35f);
                }
                else if (b.EftBleeding.Remove(part))
                {
                    foreach (var s in b.Sites)
                    {
                        if (s.BodyPart != part || s.Treated) continue;
                        s.Treated = true;
                        s.Flow *= 0.1f;
                    }
                    b.Log($"treated {part}");
                }
            }
        }

        private static void TickDead(Bleeder b, float dt)
        {
            if (b.DrainDone) return;

            bool empty = b.LossFraction >= MaxLossFraction;
            if (b.Agonal)
            {
                // The last beats: everything still flows, weaker each beat.
                float pressureFactor = 0.25f + 0.75f * b.Pressure;
                foreach (var s in b.Sites)
                {
                    s.Rate = empty || s.Bone == null ? 0f : (s.DrainRate > 0f ? s.DrainRate * s.Dependent : s.Flow * pressureFactor);
                }
                if (!empty) LeakCavities(b, 0.3f, dt);
                foreach (var s in b.Sites) Drain(b, s, s.Rate, dt, true);
                UpdatePool(b, dt);
                return;
            }

            float halfLife = Plugin.PostMortemHalfLife.Value;
            if (halfLife <= 0f)
            {
                Stop(b);
                return;
            }

            if (!b.DrainStarted)
            {
                b.DrainStarted = true;
                foreach (var s in b.Sites)
                {
                    // Gravity only: a fraction of the live flow (the arterial head is gone).
                    if (s.DrainRate <= 0f) s.DrainRate = s.Flow * (s.Kind == BleedKind.Arterial ? 0.4f : 0.6f);
                }
            }

            float decay = Mathf.Pow(0.5f, dt / halfLife);
            float total = 0f;
            foreach (var s in b.Sites)
            {
                s.Rate = empty || s.Bone == null ? 0f : s.DrainRate * s.Dependent;
                s.DrainRate *= decay;
            }
            if (!empty) LeakCavities(b, 0.3f, dt);
            foreach (var s in b.Sites)
            {
                Drain(b, s, s.Rate, dt, true);
                total += s.Rate;
            }
            UpdatePool(b, dt);
            b.MouthBlood *= Mathf.Pow(0.5f, dt / Mathf.Max(halfLife, 60f));

            if ((total < 1f && b.SinceDeath > Plugin.AgonalSeconds.Value + 5f) || b.SinceDeath > 900f) Stop(b);
        }

        private static void Stop(Bleeder b)
        {
            b.DrainDone = true;
            foreach (var s in b.Sites) s.Rate = 0f;
            b.Log("stopped draining");
        }

        /// <summary>
        /// Cavity blood runs out of the holes of its body part: <paramref name="perMinute"/> of what is
        /// left, split over the openings, x posture (a hole facing down pours, one facing up barely seeps).
        /// Added to the sites' rates, so drips, stains and pools show it.
        /// </summary>
        private static void LeakCavities(Bleeder b, float perMinute, float dt)
        {
            if (b.Cavity.Count == 0) return;
            foreach (var part in Parts)
            {
                if (!b.Cavity.TryGetValue(part, out float left) || left < 1f) continue;
                int openings = 0;
                foreach (var s in b.Sites)
                {
                    if (s.BodyPart == part && s.Bone != null) openings++;
                }
                if (openings == 0) continue;

                foreach (var s in b.Sites)
                {
                    if (s.BodyPart != part || s.Bone == null) continue;
                    float leak = left * perMinute / openings * s.Dependent;
                    float ml = Mathf.Min(leak * dt / 60f, b.Cavity[part]);
                    b.Cavity[part] -= ml;
                    s.Rate += leak;
                }
            }
        }

        private static void Drain(Bleeder b, BleedSite s, float rate, float dt, bool lying)
        {
            float ml = rate * dt / 60f;
            s.Drained += ml;
            b.BloodLost += ml;
            if (lying) b.PoolVolume += ml;
        }

        /// <summary>The body's pool spreads toward the size of its volume as a ~2.5 mm film (0.10.3; 0.10.2 1.5 mm made a 1.2-1.4 m pool within a minute of a decapitation), max 1.1 m radius, with a lag.</summary>
        private static void UpdatePool(Bleeder b, float dt)
        {
            float target = Mathf.Clamp(Mathf.Sqrt(b.PoolVolume * 1e-6f / (Mathf.PI * 0.0025f)), 0f, 1.1f);
            b.PoolRadius += (target - b.PoolRadius) * (1f - Mathf.Exp(-dt / 8f));
        }

        private static readonly EBodyPart[] Parts =
        {
            EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach,
            EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg,
        };
    }
}
