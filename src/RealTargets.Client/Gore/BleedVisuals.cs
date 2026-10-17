using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Draws the bleed model (BleedSystem) every frame, visual only:
    /// - arterial spurts: per heartbeat a burst of droplets out of the wound, speed by pressure;
    ///   after death the agonal beats weaken them to nothing;
    /// - drips and pouring: droplets welling from every open site (entry and exit), cadence by flow;
    ///   a dead body drains from whichever opening faces down;
    /// - blood running down the body: stain patches laid on the clothing below the wound, one step
    ///   further down each time enough blood has run (WoundDecals.Stain);
    /// - pools under the lowest opening of a lying body, spreading with a lag and drying darker;
    /// - mouth: chest wounds make a standing bot cough blood and a lying/dead one dribble it.
    /// </summary>
    internal sealed class BleedVisuals : MonoBehaviour
    {
        // 0.10.1 (Stompa: "3x the amount"): more, bigger, closer steps.
        private const int MaxStainsPerSite = 16;
        private const int MaxMouthStains = 10;
        private const float StainsPerSecond = 10f;
        private const float StainDistance = 35f;

        private readonly Dictionary<BleedSite, float> _nextDrip = new Dictionary<BleedSite, float>();
        private readonly Dictionary<BleedSite, float> _nextPulse = new Dictionary<BleedSite, float>();
        private readonly Dictionary<BleedSite, float> _nextStain = new Dictionary<BleedSite, float>();
        private readonly Dictionary<Bleeder, float> _nextPool = new Dictionary<Bleeder, float>();
        private readonly Dictionary<Bleeder, float> _nextMouth = new Dictionary<Bleeder, float>();
        private readonly Dictionary<Bleeder, int> _mouthStains = new Dictionary<Bleeder, int>();
        private float _stainBudget = StainsPerSecond;

        private void Update()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Bleed);
            var system = BleedSystem.Instance;
            var blood = BloodParticles.Instance;
            if (system == null || blood == null || system.Count == 0 || !Plugin.VisualsEnabled.Value)
            {
                return;
            }

            _stainBudget = Mathf.Min(StainsPerSecond, _stainBudget + StainsPerSecond * Time.deltaTime);
            var camera = Camera.main;
            float maxDistSqr = Plugin.VisualsDistance.Value * Plugin.VisualsDistance.Value;
            float now = Time.time;

            foreach (var b in system.Bleeders)
            {
                var player = b.Player;
                if (player == null || b.DrainDone) continue;

                float distSqr = camera != null ? (player.Position - camera.transform.position).sqrMagnitude : 0f;
                if (distSqr > maxDistSqr) continue;
                bool near = distSqr < StainDistance * StainDistance;

                float pressure = b.Pressure;
                float heartRate = b.HeartRate;
                bool beating = heartRate > 0f && (!b.IsDead || b.Agonal);
                bool lying = b.IsDead || player.IsInPronePose;

                foreach (var site in b.Sites)
                {
                    if (site.Bone == null || site.Rate <= 0.3f) continue;

                    if (Plugin.VisualsSpurts.Value && site.Spurts && beating && site.Rate > 60f)
                    {
                        Spurt(blood, site, pressure, heartRate, now);
                    }
                    else if (Plugin.VisualsDrips.Value)
                    {
                        Drip(blood, site, now, b.IsDead && !b.Agonal);
                    }

                    if (Plugin.VisualsBodyStains.Value && near && site.Stains < MaxStainsPerSite)
                    {
                        Stain(player, site, now);
                    }

                }

                if (Plugin.VisualsPools.Value && lying)
                {
                    Pool(b, now);
                }

                if (Plugin.VisualsMouth.Value && b.MouthBlood > 0.05f)
                {
                    Mouth(blood, b, lying, beating, near, now);
                }
            }
        }

        internal void ResetRaid()
        {
            _nextDrip.Clear();
            _nextPulse.Clear();
            _nextStain.Clear();
            _nextPool.Clear();
            _nextMouth.Clear();
            _mouthStains.Clear();
        }

        /// <summary>One heartbeat: a burst of droplets, faster and denser with higher pressure.</summary>
        private void Spurt(BloodParticles blood, BleedSite site, float pressure, float heartRate, float now)
        {
            if (_nextPulse.TryGetValue(site, out float next) && now < next) return;
            _nextPulse[site] = now + 60f / Mathf.Max(heartRate, 20f);

            var (position, outward) = site.HasExit && Random.value < 0.4f ? site.Exit : site.Entry;
            // Arterial pressure alone gives ~5 m/s at most (Bernoulli, 100 mmHg); clothing and the
            // wound track slow and scatter it: a femoral jet carries ~1-1.5 m from thigh height.
            float strength = Mathf.Clamp01(site.Rate / 1000f);
            float speed = Mathf.Lerp(0.4f, 3.4f, Mathf.Clamp01(pressure)) * Mathf.Lerp(0.8f, 1.1f, strength);
            int count = Mathf.RoundToInt(Mathf.Lerp(5f, 22f, strength) * Mathf.Lerp(0.5f, 1f, pressure));

            var jet = (outward + Vector3.up * 0.1f).normalized;
            blood.Emit(position, jet * speed, 10f, count, 0.012f);
            blood.Emit(position, jet * speed * 0.35f, 25f, Mathf.Max(2, count / 3), 0.009f);
        }

        /// <summary>Welling drops; ~0.25 ml per drop, so a pouring wound becomes a near-stream.</summary>
        private void Drip(BloodParticles blood, BleedSite site, float now, bool gravityOnly)
        {
            if (_nextDrip.TryGetValue(site, out float next) && now < next) return;
            _nextDrip[site] = now + Mathf.Clamp(15f / Mathf.Max(site.Rate, 1f), 0.035f, 4f) * Random.Range(0.7f, 1.3f);

            var (position, outward) = gravityOnly ? site.Lowest : site.HasExit && Random.value < 0.6f ? site.Exit : site.Entry;
            int count = site.Rate > 250f ? 2 : 1;
            blood.Emit(position + outward * 0.01f, outward * 0.15f + Random.insideUnitSphere * 0.05f, 0f, count,
                site.Rate > 120f ? 0.012f : 0.009f);
        }

        /// <summary>
        /// Blood runs down the outside of the body from the wound: every so often (faster with more
        /// flow) a stain patch is laid one step further down the surface, along gravity projected on
        /// the wound's surface. A wound facing straight up or down soaks around itself instead.
        /// </summary>
        private void Stain(Player player, BleedSite site, float now)
        {
            if (_nextStain.TryGetValue(site, out float next) && now < next) return;
            // 0.10.15: not in a frame that already baked body meshes for 3 ms (retried next frame).
            if (!WoundDecals.StainAffordable) return;
            if (_stainBudget < 1f) return;
            var decals = WoundDecals.Instance;
            if (decals == null) return;

            // First stain right away, then one per ~3 ml run out (bounded 0.3-4 s).
            _nextStain[site] = now + Mathf.Clamp(160f / Mathf.Max(site.Rate, 1f), 0.3f, 4f) * Random.Range(0.8f, 1.2f);
            _stainBudget -= 1f;

            // The stream comes from the exit when the round went through and the exit is lower.
            var (origin, outward) = site.HasExit && site.Exit.position.y < site.Entry.position.y ? site.Exit : site.Entry;
            var run = Vector3.ProjectOnPlane(Vector3.down, outward);
            int step = site.Stains;
            Vector3 at;
            if (run.sqrMagnitude < 0.09f)
            {
                // Facing up or down: the soak spreads around the hole.
                var t = Vector3.Cross(outward, Vector3.right);
                if (t.sqrMagnitude < 0.01f) t = Vector3.Cross(outward, Vector3.forward);
                t = Quaternion.AngleAxis(Random.Range(0f, 360f), outward) * t.normalized;
                at = origin + t * (0.01f * step);
            }
            else
            {
                // Overlapping steps make a continuous run; it widens and wanders a little as it goes.
                at = origin + run.normalized * (0.015f + 0.03f * step) +
                     Vector3.Cross(outward, run.normalized) * Random.Range(-0.006f, 0.006f) * step;
            }

            float size = Mathf.Lerp(0.05f, 0.1f, Mathf.Clamp01(site.Rate / 300f)) * Random.Range(0.85f, 1.15f);
            if (decals.Stain(player, at, outward, size, step == 0))
            {
                site.Stains++;
            }
            else
            {
                // Ran off the body (edge, gap): stop extending this streak.
                site.Stains = MaxStainsPerSite;
            }
        }

        /// <summary>
        /// Grows the body's pool (GroundBlood) under where its blood ran out: the lowest openings
        /// weighted by how much left through each (stable as the rates change).
        /// </summary>
        private void Pool(Bleeder b, float now)
        {
            if (_nextPool.TryGetValue(b, out float next) && now < next) return;
            _nextPool[b] = now + 0.4f;

            var ground = GroundBlood.Instance;
            if (ground == null || b.PoolRadius < 0.03f) return;

            var sum = Vector3.zero;
            float weight = 0f;
            foreach (var s in b.Sites)
            {
                if (s.Bone == null || s.Drained <= 0f) continue;
                sum += s.Lowest.position * s.Drained;
                weight += s.Drained;
            }
            if (weight <= 0f) return;
            ground.Pool(b, b.Name, sum / weight, b.PoolRadius);
        }

        /// <summary>
        /// Blood from the airway: a standing bot with a beating heart coughs a spray every few
        /// seconds; lying or dead, it dribbles from the mouth.
        /// </summary>
        private void Mouth(BloodParticles blood, Bleeder b, bool lying, bool beating, bool near, float now)
        {
            if (_nextMouth.TryGetValue(b, out float next) && now < next) return;

            bool cough = beating && !lying && !b.IsDead;
            float severity = b.MouthBlood;
            _nextMouth[b] = now + (cough ? Random.Range(2f, 5f) / severity : Random.Range(0.12f, 0.45f) / severity);

            var frame = BodyFrame.Build(b.Player);
            if (frame == null || !frame.Anchors.TryGetValue("col:HeadCommon", out var head))
            {
                return;
            }

            var mouth = head + frame.Forward * 0.085f - frame.Up * 0.055f;
            if (cough)
            {
                int count = Mathf.RoundToInt(Mathf.Lerp(15f, 35f, severity));
                blood.Emit(mouth, (frame.Forward * 1.6f + Vector3.up * 0.2f) * Random.Range(0.8f, 1.2f), 25f, count, 0.009f);
                BloodMist.Instance?.Puff(mouth, frame.Forward, 0.08f, 0.7f * severity, 5);
            }
            else
            {
                // A thin stream out of the mouth corner (and nose).
                blood.Emit(mouth, frame.Forward * 0.08f + Random.insideUnitSphere * 0.04f, 0f, beating ? 3 : 2, 0.011f);
            }

            // Blood around the mouth and running down the chin/cheek, a step at a time.
            _mouthStains.TryGetValue(b, out int stains);
            if (near && stains < MaxMouthStains && _stainBudget >= 1f && WoundDecals.StainAffordable && Random.value < (cough ? 0.6f : 0.15f))
            {
                var decals = WoundDecals.Instance;
                var run = Vector3.ProjectOnPlane(Vector3.down, frame.Forward);
                var at = mouth + (run.sqrMagnitude > 0.04f ? run.normalized * (0.012f * stains) : Vector3.zero);
                if (decals != null && decals.Stain(b.Player, at, frame.Forward, Random.Range(0.035f, 0.055f), true))
                {
                    _stainBudget -= 1f;
                    _mouthStains[b] = stains + 1;
                }
            }
        }
    }
}
