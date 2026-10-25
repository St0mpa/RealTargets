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

        private static readonly List<WoundPath> Paths = new List<WoundPath>(2);

        private void OnHit(HitEvent hit)
        {
            if (!Plugin.VisualsEnabled.Value || hit.Target == null || hit.IsLocalPlayer || !hit.IsAI)
            {
                return;
            }

            try
            {
                // A site per stretch through flesh (a round through a forearm into the chest bleeds from both);
                // a stretch that goes on from inside adds none (its body part already bleeds from the earlier hit).
                WoundPath.ResolveAll(hit, Paths);
                if (Paths.Count == 0) return;
                var bleeder = GetOrCreate(hit.Target, hit.Status == HitStatus.Corpse);
                bleeder.Hits++;
                if (hit.Status == HitStatus.Killed) bleeder.KillingHit = hit;
                foreach (var path in Paths)
                {
                    if (!path.FromInside) AddSite(bleeder, hit, path);
                }
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

            // blood-5 (blood-4 test: buckshot at 1.4 m made 8 oozing sites of 42 ml/min, each clotting alone, and no run after
            // 1.9 L lost): pellets of one shell that land within 6 cm of an earlier one (a tight close-range pattern) tear one
            // wound together, so they join its site: the flows add up (a little less: overlapping tracks).
            if (hit.ProjectileCount > 1 && !string.IsNullOrEmpty(hit.SourceId))
            {
                foreach (var other in bleeder.Sites)
                {
                    if (other.SourceId != hit.SourceId || other.Bone == null || Time.time - other.CreatedAt > 0.5f) continue;
                    if ((other.Entry.position - path.Start).sqrMagnitude > 0.06f * 0.06f) continue;
                    other.Flow += flow * 0.8f;
                    other.Energy += e;
                    if (!other.HasExit && path.Exited)
                    {
                        other.HasExit = true;
                        other.LocalExit = other.Bone.InverseTransformPoint(path.End);
                        other.LocalExitOutward = other.Bone.InverseTransformDirection(path.Direction);
                    }
                    if (other.Kind != BleedKind.Arterial)
                    {
                        other.Kind = other.Flow < 60f ? BleedKind.Ooze : other.Flow < 200f ? BleedKind.Flow : BleedKind.Heavy;
                        other.ClotHalfLife = other.Kind == BleedKind.Heavy ? 200f : other.Kind == BleedKind.Flow ? 110f : 50f;
                    }
                    if (bleeder.IsDead) other.DrainRate = other.Flow * 0.6f;
                    bleeder.Log($"pellet joins {other.Collider}: {other.Kind} {other.Flow:F0} ml/min");
                    return;
                }
            }

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
                SourceId = hit.SourceId,
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
            // A dead body neither breathes (no froth) nor keeps its arterial blood scarlet.
            site.Tone = bleeder.IsDead ? BloodTone.Venous : BloodPalette.ToneFor(c, site.Kind == BleedKind.Arterial);
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
                bleeder.MouthTone = WoundPath.IsChest(c) ? BloodTone.Pulmonary : BloodTone.Venous;
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
                Tone = arterial && !(bleeder.IsDead && !bleeder.Agonal) ? BloodTone.Arterial : BloodTone.Venous,
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
            using var part = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.BleedTick);

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

                    TrackPlace(b, dt);
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
            // blood-5: a bot standing still pools too (most drops fall to its feet; some soak and run on the clothes).
            float pool = !b.Still ? 0f : b.Player.IsInPronePose ? 1f : 0.7f;
            foreach (var s in b.Sites) Drain(b, s, s.Rate, dt, pool);
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
                foreach (var s in b.Sites) Drain(b, s, s.Rate, dt, b.Still ? 1f : 0f);
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
                Drain(b, s, s.Rate, dt, b.Still ? 1f : 0f);
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

        /// <summary><paramref name="pool"/>: the share of it that collects in this place's pool (0 while the body moves).</summary>
        private static void Drain(Bleeder b, BleedSite s, float rate, float dt, float pool)
        {
            float ml = rate * dt / 60f;
            s.Drained += ml;
            b.BloodLost += ml;
            b.PoolVolume += ml * pool;
        }

        /// <summary>
        /// blood-5: where the body's blood leaves it now (the mean of the sites' lowest openings: these move with a ragdoll, the
        /// player root does not) and whether it has been still. Faster than 15 cm/s = moving (walking, dragged, falling);
        /// 0.5 m from the place's anchor = a new place, whose pool starts from nothing (GroundBlood starts a new pool there too).
        /// Small nudges (a shot into a corpse, a twitch) keep the place and its volume.
        /// </summary>
        private static void TrackPlace(Bleeder b, float dt)
        {
            var sum = Vector3.zero;
            int n = 0;
            foreach (var s in b.Sites)
            {
                if (s.Bone == null) continue;
                sum += s.Lowest.position;
                n++;
            }
            if (n == 0) return;
            var at = sum / n;
            float now = Time.time;
            if (!b.HasPlace)
            {
                b.HasPlace = true;
                b.PlaceAnchor = b.PlaceLast = at;
                b.MovedAt = now;
                return;
            }
            var step = at - b.PlaceLast;
            step.y = 0f;
            if (step.magnitude > 0.15f * Mathf.Max(dt, 0.05f)) b.MovedAt = now;
            b.PlaceLast = at;
            var off = at - b.PlaceAnchor;
            off.y = 0f;
            if (off.magnitude > 0.5f)
            {
                b.PlaceAnchor = at;
                b.PoolVolume = 0f;
                b.PoolRadius = 0f;
            }
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
