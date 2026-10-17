using System.Diagnostics;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Rough light level (0..1) at a surface point, for decals drawn with an unlit colour pass (0.9.3):
    /// baked light probes in the normal's direction plus nearby point and spot lights (map lamps, a
    /// helmet torch). Directional lights are left out: without shadows the sun would light indoor
    /// wounds (the 0.9.0 red glow). Approximate by design: it only keeps the colour pass from glowing
    /// in the dark and lets it show where there is light.
    /// 0.10.10 (0.10.9 benchmark: a 5-6 ms frame every few seconds with nothing moving): the light list was
    /// FindObjectsOfType every 2 s, a sorted scan of the whole map, called by every gore piece and new ground
    /// mark. Now the unsorted FindObjectsByType, every 20 s, plus once while the raid loads (<see cref="Warm"/>).
    /// A torch switched on mid-raid is counted from the next scan (at most 20 s); one already found is
    /// followed on and off at once (isActiveAndEnabled per lookup).
    /// </summary>
    internal static class SceneLight
    {
        private const float ScanEvery = 20f;

        private static Light[] _lights = new Light[0];
        private static float _nextScan;
        private static readonly Vector3[] Dir = new Vector3[1];
        private static readonly Color[] Result = new Color[1];
        private static int _scans;
        private static double _scanMsMax;

        /// <summary>Scans the map's lights now (raid loading), so no scan lands in the first fight.</summary>
        internal static void Warm() => Scan();

        /// <summary>Forgets the old map's lights (raid end).</summary>
        internal static void ResetRaid()
        {
            _lights = new Light[0];
            _nextScan = 0f;
        }

        /// <summary>"2 (max 1.3 ms)" since the last call, for the [Perf] line.</summary>
        internal static string TakeScanStats()
        {
            string s = _scans == 0 ? "0" : $"{_scans} (max {_scanMsMax:F1} ms, {_lights.Length} lights)";
            _scans = 0;
            _scanMsMax = 0;
            return s;
        }

        private static void Scan()
        {
            var watch = Stopwatch.StartNew();
            _nextScan = Time.time + ScanEvery;
            _lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            double ms = watch.Elapsed.TotalMilliseconds;
            _scans++;
            if (ms > _scanMsMax) _scanMsMax = ms;
        }

        internal static float At(Vector3 point, Vector3 normal)
        {
            if (Time.time >= _nextScan) Scan();

            float level = 0f;
            try
            {
                LightProbes.GetInterpolatedProbe(point, null, out var sh);
                Dir[0] = normal;
                sh.Evaluate(Dir, Result);
                var c = Result[0];
                level = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            }
            catch
            {
                level = 0f;
            }
            if (level < 0.01f)
            {
                // No probes here: fall back to the scene's ambient colour.
                var a = RenderSettings.ambientLight;
                level = Mathf.Max(a.r, Mathf.Max(a.g, a.b)) * RenderSettings.ambientIntensity;
            }

            foreach (var light in _lights)
            {
                if (light == null || !light.isActiveAndEnabled || light.intensity <= 0f) continue;
                if (light.type != LightType.Point && light.type != LightType.Spot) continue;
                var to = light.transform.position - point;
                float d = to.magnitude;
                if (d > light.range || d < 1e-3f) continue;
                var dir = to / d;
                float lambert = Mathf.Max(0f, Vector3.Dot(normal, dir));
                if (lambert <= 0f) continue;
                float x = d / light.range;
                float atten = 1f / (1f + 25f * x * x) * Mathf.Clamp01((1f - x) * 4f);
                if (light.type == LightType.Spot)
                {
                    float cos = Vector3.Dot(light.transform.forward, -dir);
                    float outer = Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad);
                    float inner = Mathf.Cos(light.spotAngle * 0.35f * Mathf.Deg2Rad);
                    atten *= Mathf.Clamp01((cos - outer) / Mathf.Max(1e-3f, inner - outer));
                }
                var col = light.color;
                level += light.intensity * atten * lambert * Mathf.Max(col.r, Mathf.Max(col.g, col.b));
            }

            return Mathf.Clamp(level, 0.06f, 1.2f);
        }

        /// <summary>
        /// Brightness for an unlit colour pass (Hybrid mode fallback, mist) from a level of At().
        /// 0.10.2 used 0.3-1 and logged 1.0 ("light 255") nearly everywhere on Factory: lamps without
        /// shadows reach through walls and EFT's probes are HDR, so the level saturates, and EFT's auto
        /// exposure then brightens the fixed colour further (blood glowed). Halved, with a low floor.
        /// </summary>
        internal static float Unlit(float level) => Mathf.Lerp(0.12f, 0.5f, Mathf.Clamp01(level));
    }
}
