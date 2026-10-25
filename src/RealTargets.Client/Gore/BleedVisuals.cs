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
        // blood-5 (blood-4 test, Stompa: the running mouth trickle "could be 2x"): twice as long, a little wider.
        private const int MaxMouthStains = 20;
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

                    if (Plugin.VisualsBodyStains.Value && near && !RunsFinished(site))
                    {
                        Stain(player, site, now, b.IsDead && !b.Agonal ? BloodTone.Venous : site.Tone, b.Still);
                    }

                }

                // blood-5: wherever the body is still (lying, or standing still while it bleeds); never while it moves or is
                // dragged (that blood drips and smears; the blood-4 drag left a new round pool at every pause).
                if (Plugin.VisualsPools.Value && b.Still)
                {
                    Pool(b, now);
                }

                if (Plugin.VisualsBodyStains.Value && near && b.IsDead)
                {
                    ContactSoak(b, now);
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
            _nextSoak.Clear();
            _soaks.Clear();
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
            blood.Emit(position, jet * speed, 10f, count, 0.012f, site.Tone);
            blood.Emit(position, jet * speed * 0.35f, 25f, Mathf.Max(2, count / 3), 0.009f, site.Tone);
        }

        /// <summary>Welling drops; ~0.25 ml per drop, so a pouring wound becomes a near-stream.</summary>
        private void Drip(BloodParticles blood, BleedSite site, float now, bool gravityOnly)
        {
            if (_nextDrip.TryGetValue(site, out float next) && now < next) return;
            _nextDrip[site] = now + Mathf.Clamp(15f / Mathf.Max(site.Rate, 1f), 0.035f, 4f) * Random.Range(0.7f, 1.3f);

            var (position, outward) = gravityOnly ? site.Lowest : site.HasExit && Random.value < 0.6f ? site.Exit : site.Entry;
            // blood-5: a run that ran off the clothing's edge (a jacket hem) is where most of its blood falls from.
            var edge = site.EntryRun.OffEdge ? site.EntryRun : site.ExitRun.OffEdge ? site.ExitRun : null;
            if (edge != null && edge.HasEnd && site.Bone != null && Random.value < 0.6f)
            {
                position = site.Bone.TransformPoint(edge.LocalEnd);
                outward = Vector3.down;
            }
            int count = site.Rate > 250f ? 2 : 1;
            blood.Emit(position + outward * 0.01f, outward * 0.15f + Random.insideUnitSphere * 0.05f, 0f, count,
                site.Rate > 120f ? 0.012f : 0.009f, DripTone(site, gravityOnly));
        }

        /// <summary>
        /// Drops from a site: its own tone while the heart beats; a corpse draining by gravity drips venous-dark blood (an
        /// artery or lung no longer refreshes it).
        /// </summary>
        private static BloodTone DripTone(BleedSite site, bool gravityOnly) => gravityOnly ? BloodTone.Venous : site.Tone;

        /// <summary>
        /// Blood runs down the outside of the body from the wound: every so often (faster with more
        /// flow) a stain patch is laid one step further down the surface, along gravity projected on
        /// the wound's surface. A wound facing straight up or down soaks around itself instead.
        /// </summary>
        private void Stain(Player player, BleedSite site, float now, BloodTone tone, bool still)
        {
            if (_nextStain.TryGetValue(site, out float next) && now < next) return;
            // 0.10.15: not in a frame that already baked body meshes for 3 ms (retried next frame).
            if (!WoundDecals.StainAffordable) return;
            if (_stainBudget < 1f) return;
            var decals = WoundDecals.Instance;
            if (decals == null) return;

            // First stain right away, then one per ~3 ml run out (bounded 0.3-4 s).
            _nextStain[site] = now + Mathf.Clamp(160f / Mathf.Max(site.Rate, 1f), 0.3f, 4f) * Random.Range(0.8f, 1.2f);

            // blood-5: both openings run (each its own StainRun); the exit, usually the bigger hole, a little more often. Before,
            // only the lower opening ran and a level through-shot left its exit dry.
            bool exitRun = site.HasExit && !Finished(site.ExitRun) && (Finished(site.EntryRun) || Random.value < 0.6f);
            var stream = exitRun ? site.ExitRun : site.EntryRun;
            if (Finished(stream)) return;
            var (origin, outward) = exitRun ? site.Exit : site.Entry;
            var run = Vector3.ProjectOnPlane(Vector3.down, outward);
            float size = Mathf.Lerp(0.05f, 0.1f, Mathf.Clamp01(site.Rate / 300f)) * Random.Range(0.9f, 1.1f);
            // blood-3 (buckshot: every pellet its own identical run, "tally marks"): an oozing hole (< 40 ml/min) leaves a small
            // spot and at most one or two short steps; only real bleeders run.
            bool ooze = site.Rate < 40f && !site.Spurts;
            if (ooze) size *= 0.7f;

            if (stream.Done)
            {
                // The run is as long as it gets: blood still flowing widens and darkens it and spreads the soak round the hole
                // (blood-4 test: a run stayed exactly as it was from 34 ml to 246 ml lost).
                // blood-5b (raid [Perf]: Bleed 0.35-0.67 ms/frame with ~20 bleeding bodies, 10-21 ms frames): a moving body's
                // bake is stale at every stain, so feeding it re-baked the whole body each time; feed only while it is still.
                if (ooze || site.Rate < 15f || !still) return;
                Feed(player, site, stream, origin, outward, run, size, tone);
                return;
            }
            _stainBudget -= 1f;
            int step = stream.Steps;
            if (ooze && step >= 1 + (site.GetHashCode() & 1))
            {
                End(player, site, stream, exitRun, false, "an oozing hole");
                stream.Fed = MaxFeed;
                return;
            }
            Vector3 at;
            int cell = -1;
            if (run.sqrMagnitude < 0.09f)
            {
                // Facing up or down: one soak spreading around the hole (blood-2: steps scattered in random directions made a
                // red cartoon blob): the soak blot, a little off centre, bigger each step.
                var t = Vector3.Cross(outward, Vector3.right);
                if (t.sqrMagnitude < 0.01f) t = Vector3.Cross(outward, Vector3.forward);
                t = Quaternion.AngleAxis(Random.Range(0f, 360f), outward) * t.normalized;
                at = origin + t * (0.004f * step);
                size *= Mathf.Min(1f + 0.22f * step, 3f);
                cell = WoundTextures.SoakCell;
            }
            else
            {
                // Overlapping steps make a continuous run (blood-2: 3 cm steps left gaps between the narrower stains): each
                // step ~45 % of a stain's length further, wandering a little.
                at = origin + run.normalized * (size * (0.2f + 0.3f * step)) +
                     Vector3.Cross(outward, run.normalized) * Random.Range(-0.004f, 0.004f) * step;
                // blood-3 ("a red sausage"): a run thins as it goes, and now and then splits off a narrower rivulet.
                float full = size;
                size *= Mathf.Max(0.5f, Mathf.Pow(0.9f, step));
                if (step >= 3 && step <= 8 && Random.value < 0.18f && WoundDecals.StainAffordable && _stainBudget >= 2f)
                {
                    var side = Vector3.Cross(outward, run.normalized) * (Random.value < 0.5f ? -1f : 1f);
                    if (decals.Stain(player, at + side * (full * 0.3f) + run.normalized * (full * 0.25f), outward, size * 0.65f, false, tone, step, 0))
                    {
                        _stainBudget -= 1f;
                    }
                }
            }

            stream.Steps++;
            if (decals.Stain(player, at, outward, size, step == 0, tone, step, cell))
            {
                site.Stains++;
                stream.Misses = 0;
                if (site.Bone != null)
                {
                    stream.LocalEnd = site.Bone.InverseTransformPoint(at);
                    stream.HasEnd = true;
                }
                if (stream.Steps >= MaxStainsPerSite) End(player, site, stream, exitRun, false, "full length");
            }
            else if (step == 0 || ++stream.Misses >= 2)
            {
                // Ran off the body (edge, gap): stop extending this streak. blood-5: one bad spot (a pocket edge, a seam, a
                // strap) is stepped over; two in a row end it.
                End(player, site, stream, exitRun, step > 0, step == 0 ? "no surface at the wound" : "ran off the clothing");
            }
        }

        /// <summary>Feeds kept per run once it has its full length (wider steps over it, a growing soak round the hole).</summary>
        private const int MaxFeed = 10;

        private static bool Finished(StainRun run) => run.Done && run.Fed >= MaxFeed;

        private static bool RunsFinished(BleedSite site) => Finished(site.EntryRun) && (!site.HasExit || Finished(site.ExitRun));

        private static void End(Player player, BleedSite site, StainRun stream, bool exit, bool offEdge, string why)
        {
            stream.Done = true;
            stream.OffEdge = offEdge && stream.HasEnd;
            if (Plugin.LogBlood.Value && site.Rate >= 40f)
            {
                Plugin.Log.LogInfo($"[Wound] {player.Profile?.Nickname} {site.Collider} {(exit ? "exit" : "entry")} run: {stream.Steps} step(s), " +
                                   $"ended ({why}){(stream.OffEdge ? ", drips from its end" : "")}, {site.Rate:F0} ml/min");
            }
        }

        /// <summary>
        /// A finished run still fed by the wound: a wider patch over one of its steps (the stream thickens and darkens where it
        /// overlaps itself), and every third feed a bigger soak blot round the hole.
        /// </summary>
        private void Feed(Player player, BleedSite site, StainRun stream, Vector3 origin, Vector3 outward, Vector3 run, float size, BloodTone tone)
        {
            if (_stainBudget < 1f) return;
            var decals = WoundDecals.Instance;
            if (decals == null) return;
            _stainBudget -= 1f;
            stream.Fed++;
            if (stream.Fed % 4 == 0 || run.sqrMagnitude < 0.09f || stream.Steps == 0)
            {
                // The soak round the opening: fabric wicks it outward as more comes through. blood-5b (an M4 exit read as a
                // round orange disc: growing blots stacked on the hole): each blot off centre, mostly toward where it runs,
                // and growing slowly (max 1.5x), so they overlap into an uneven patch.
                var t = Vector3.Cross(outward, Mathf.Abs(outward.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                t = Quaternion.AngleAxis(Random.Range(0f, 360f), outward) * t;
                var down = run.sqrMagnitude > 0.01f ? run.normalized : t;
                var soakAt = origin + t * Random.Range(0.006f, 0.018f) + down * Random.Range(0.005f, 0.02f);
                if (decals.Stain(player, soakAt, outward, size * Mathf.Min(0.9f + 0.06f * stream.Fed, 1.5f), true, BloodTone.Venous, 0,
                        WoundTextures.SoakCell))
                {
                    site.Stains++;
                }
                return;
            }
            int k = Random.Range(0, Mathf.Max(1, Mathf.Min(stream.Steps, MaxStainsPerSite)));
            float stepSize = size * Mathf.Max(0.5f, Mathf.Pow(0.9f, k)) * Random.Range(1.1f, 1.3f);
            var at = origin + run.normalized * (size * (0.2f + 0.3f * k)) + Vector3.Cross(outward, run.normalized) * Random.Range(-0.006f, 0.006f);
            if (decals.Stain(player, at, outward, stepSize, false, tone, k, -1)) site.Stains++;
        }

        /// <summary>
        /// Grows the body's pool (GroundBlood/BloodPool) under where its blood ran out at this place (blood-5: PoolRadius
        /// counts only the blood since the body came to rest here, see BleedSystem.TrackPlace): the lowest openings
        /// weighted by how much left through each (stable as the rates change). 1.2.0: every opening that let
        /// out more than 5 % of the blood is a source the pool spreads from; how scarlet it starts is the
        /// drained-volume mix of the sites' tones. The pool moves its own edge every frame.
        /// </summary>
        private void Pool(Bleeder b, float now)
        {
            if (_nextPool.TryGetValue(b, out float next) && now < next) return;
            _nextPool[b] = now + 0.4f;

            var ground = GroundBlood.Instance;
            if (ground == null || b.PoolRadius < 0.03f) return;

            var sum = Vector3.zero;
            float weight = 0f, oxygen = 0f;
            foreach (var s in b.Sites)
            {
                if (s.Bone == null || s.Drained <= 0f) continue;
                sum += s.Lowest.position * s.Drained;
                weight += s.Drained;
                oxygen += BloodPalette.Oxygen(s.Tone) * s.Drained;
            }
            if (weight <= 0f) return;
            PoolSources.Clear();
            foreach (var s in b.Sites)
            {
                if (s.Bone == null || s.Drained < weight * 0.05f) continue;
                PoolSources.Add(s.Lowest.position);
            }
            ground.Pool(b, b.Name, sum / weight, b.PoolRadius, oxygen / weight, PoolSources);
        }

        private static readonly List<Vector3> PoolSources = new List<Vector3>(8);

        private readonly Dictionary<Bleeder, float> _nextSoak = new Dictionary<Bleeder, float>();
        private readonly Dictionary<Bleeder, int> _soaks = new Dictionary<Bleeder, int>();
        private const int MaxContactSoaks = 8;

        /// <summary>
        /// A body lying in its own blood soaks it up (blood-3, Stompa's turned-over corpse: the back showed a red cartoon blob,
        /// a real one is dark and soaked where it lay): while the floor under an opening is wet, every ~2.5 s a big dark soak
        /// blot on the down-facing surface around it, up to 8 per body.
        /// </summary>
        private void ContactSoak(Bleeder b, float now)
        {
            if (_nextSoak.TryGetValue(b, out float next) && now < next) return;
            _nextSoak[b] = now + Random.Range(2f, 3f);
            _soaks.TryGetValue(b, out int done);
            var ground = GroundBlood.Instance;
            var decals = WoundDecals.Instance;
            if (done >= MaxContactSoaks || ground == null || decals == null || !WoundDecals.StainAffordable || _stainBudget < 1f) return;
            BleedSite lowest = null;
            float y = float.MaxValue;
            foreach (var s in b.Sites)
            {
                if (s.Bone == null || s.Drained < 20f) continue;
                var p = s.Lowest.position;
                if (p.y < y) { y = p.y; lowest = s; }
            }
            if (lowest == null) return;
            var at = lowest.Lowest.position;
            if (ground.PoolWetness(at + Vector3.down * 0.15f) < 0.3f) return;
            var spread = Random.insideUnitCircle * (0.04f + 0.03f * done);
            var point = at + new Vector3(spread.x, 0f, spread.y);
            // Looking up at the body from below: the patch lands on the surface that touches the floor.
            if (decals.Stain(b.Player, point, Vector3.down, Random.Range(0.12f, 0.2f), true, BloodTone.Venous, 0, WoundTextures.SoakCell))
            {
                _stainBudget -= 1f;
                _soaks[b] = done + 1;
            }
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
                // blood-3: coughed blood landed as a confetti of 2.5-5.5 cm splats: a fine spray (mm speckles where it lands).
                blood.Emit(mouth, (frame.Forward * 1.6f + Vector3.up * 0.2f) * Random.Range(0.8f, 1.2f), 25f, count, 0.006f, b.MouthTone, true);
                BloodMist.Instance?.Puff(mouth, frame.Forward, 0.08f, 0.7f * severity, 5, 1f, b.MouthTone);
            }
            else
            {
                // A thin stream out of the mouth corner (and nose). blood-5: twice the drops.
                blood.Emit(mouth, frame.Forward * 0.08f + Random.insideUnitSphere * 0.04f, 0f, beating ? 6 : 4, 0.011f, b.IsDead ? BloodTone.Venous : b.MouthTone);
            }

            // Blood around the mouth and running down the chin/cheek, a step at a time.
            _mouthStains.TryGetValue(b, out int stains);
            if (near && stains < MaxMouthStains && _stainBudget >= 1f && WoundDecals.StainAffordable && Random.value < (cough ? 0.75f : 0.3f))
            {
                var decals = WoundDecals.Instance;
                var run = Vector3.ProjectOnPlane(Vector3.down, frame.Forward);
                // blood-2: 3.5-5.5 cm patches on the curved jaw were cut into blocks by the 5x5 grid: a thin trickle of small ones.
                // blood-5: twice as long (20 steps of 0.9 cm, down the chin onto the neck), ~1.25x wider.
                var at = mouth + (run.sqrMagnitude > 0.04f ? run.normalized * (0.009f * stains) : Vector3.zero);
                if (decals != null && decals.Stain(b.Player, at, frame.Forward, Random.Range(0.022f, 0.035f), true, b.MouthTone, stains, 0))
                {
                    _stainBudget -= 1f;
                    _mouthStains[b] = stains + 1;
                }
            }
        }
    }
}
