using EFT;
using RealTargets.Compat;
using RealTargets.Hits;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// What a projectile throws out of a body the moment it hits (0.10.0), on living bots and corpses:
    /// - exit spray: a cone of droplets out of the exit wound along the shot, count and speed by
    ///   energy (a 9x19 throws a few, a 7.62x51 a few dozen); they fly, fall and leave splats where
    ///   they land (BloodParticles -> GroundBlood);
    /// - mist: a short-lived cloud at the exit, bigger with energy (high-speed footage of rifle
    ///   through-shots); a killing high-energy headshot gets a large one;
    /// - wall spatter: a raycast along the shot from the exit; a wall or floor within ~2.5 m gets a
    ///   splash at once (dense near the centre, thinning out, smaller and fewer with distance),
    ///   so it shows even when the droplet budget is spent;
    /// - corpse squeeze: a hit on a dead body pushes a little blood out of the hole (cavity), no jet.
    /// EFT's own entry puff stays; we don't add one at the entry.
    /// With HollywoodFX and [HollywoodFX] Prefer its blood (1.1.0), some of these give way to its blood (Compat/HollywoodFx).
    /// </summary>
    internal sealed class ImpactBlood : MonoBehaviour
    {
        private const float SpatterRange = 2.5f;

        private void Awake() => HitCollector.Published += OnHit;
        private void OnDestroy() => HitCollector.Published -= OnHit;

        private void OnHit(HitEvent hit)
        {
            if (!Plugin.VisualsEnabled.Value || hit.Target == null || hit.IsLocalPlayer || !hit.IsAI)
            {
                return;
            }

            var camera = Camera.main;
            if (camera != null && (hit.Target.Position - camera.transform.position).sqrMagnitude >
                Plugin.VisualsDistance.Value * Plugin.VisualsDistance.Value)
            {
                return;
            }

            try
            {
                // Every stretch through flesh (forearm, then chest): its own exit blood; the wall only behind the last.
                WoundPath.ResolveAll(hit, Paths);
                for (int i = 0; i < Paths.Count; i++) Throw(hit, Paths[i], i == Paths.Count - 1);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Impact blood: {ex.Message}");
            }
        }

        private static readonly System.Collections.Generic.List<WoundPath> Paths = new System.Collections.Generic.List<WoundPath>(2);

        private static void Throw(HitEvent hit, WoundPath path, bool last)
        {
            var blood = BloodParticles.Instance;
            float e = path.Energy;
            float power = Mathf.Clamp01(e / 3000f);
            bool head = WoundPath.IsHead(path.Entry.Collider);
            bool corpse = hit.Status == HitStatus.Corpse;
            bool bigHead = head && hit.Status != HitStatus.Alive && e > 1200f;
            // [HollywoodFX] Prefer its blood: its puffs/sprays sit at the entry, EFT's splash on the wall behind.
            // A stretch that goes on from inside (the same round's earlier-frame hit) has no entry to mist from.
            bool skipEntry = HollywoodFx.SkipEntryMist || path.FromInside;
            bool skipExit = HollywoodFx.SkipExitBlood;

            if (path.Exited)
            {
                var dir = path.Direction;
                if (blood != null && Plugin.VisualsExitSpray.Value && !skipExit)
                {
                    int count = Mathf.RoundToInt(Mathf.Clamp(e / 70f, 3f, 45f) * (bigHead ? 1.6f : 1f));
                    float speed = Mathf.Lerp(2.5f, 9f, power);
                    blood.Emit(path.End + dir * 0.01f, dir * speed, 14f, count, 0.007f);
                    // Heavier, slower drops that fall close behind the body.
                    blood.Emit(path.End + dir * 0.01f, dir * speed * 0.3f, 30f, Mathf.Max(2, count / 4), 0.011f);
                }

                if (e > 250f && !skipExit)
                {
                    // 0.10.1: 0.10.0's 3-12 cm puffs were never seen (Stompa).
                    float size = Mathf.Lerp(0.08f, 0.22f, power) * (bigHead ? 1.6f : 1f);
                    int puffs = Mathf.RoundToInt(Mathf.Lerp(6f, 18f, power) * (bigHead ? 2f : 1f));
                    BloodMist.Instance?.Puff(path.End + dir * 0.04f, dir, size, Mathf.Lerp(0.55f, 0.9f, power), puffs);
                }

                if (last && Plugin.VisualsSpatter.Value && e > 400f && !HollywoodFx.SkipWallSpatter) Spatter(path, e, bigHead);
            }
            else if (head && e > 800f && !skipEntry)
            {
                // Stopped inside the skull: the pressure vents back out of the entry.
                BloodMist.Instance?.Puff(path.Start - path.Direction * 0.03f, -path.Direction, 0.08f, 0.6f, 6);
            }

            // Backspatter toward the shooter: the part of the cloud you actually see from the front.
            if (e > 300f && !skipEntry)
            {
                BloodMist.Instance?.Puff(path.Start - path.Direction * 0.03f, -path.Direction, Mathf.Lerp(0.05f, 0.12f, power), 0.5f,
                    Mathf.RoundToInt(Mathf.Lerp(3f, 8f, power)));
            }

            if (corpse && blood != null && Plugin.VisualsDrips.Value && !skipExit)
            {
                var at = path.Exited ? path.End : path.Start;
                var outward = path.Exited ? path.Direction : -path.Direction;
                int squeeze = Mathf.RoundToInt(Mathf.Clamp(e / 250f, 2f, 10f));
                blood.Emit(at + outward * 0.01f, outward * 0.6f, 35f, squeeze, 0.01f);
            }
        }

        /// <summary>Splash on the surface behind the body, along the shot from the exit.</summary>
        private static void Spatter(WoundPath path, float energy, bool bigHead)
        {
            var ground = GroundBlood.Instance;
            if (ground == null) return;

            var from = path.End + path.Direction * 0.03f;
            if (!Physics.Raycast(from, path.Direction, out var hit, SpatterRange, LayersMaskController.HighPolyWithTerrainMask) ||
                GroundBlood.Movable(hit.collider))
            {
                if (Plugin.LogBlood.Value) Plugin.Log.LogInfo($"[Spatter] nothing within {SpatterRange} m behind the exit");
                return;
            }

            float falloff = 1f - hit.distance / SpatterRange;
            int count = Mathf.RoundToInt(Mathf.Clamp(energy / 140f, 3f, 24f) * falloff * (bigHead ? 1.8f : 1f));
            if (count < 1) return;
            // The cone widens with distance; drops also fall, so the splash sits a bit lower.
            float radius = 0.04f + hit.distance * 0.13f;
            var centre = hit.point + Vector3.down * (hit.distance * hit.distance * 0.02f);
            ground.Spatter(centre, hit.normal, path.Direction * Mathf.Lerp(3f, 9f, Mathf.Clamp01(energy / 3000f)), radius, count);
            if (Plugin.LogBlood.Value) Plugin.Log.LogInfo($"[Spatter] {count} drops, {radius * 100f:F0} cm, {hit.distance:F2} m behind on {hit.collider.name}");
        }
    }
}
