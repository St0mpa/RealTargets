using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Comfort.Common;
using EFT;
using RealTargets.Features;
using RealTargets.Gore;
using UnityEngine;

namespace RealTargets.Diagnostics
{
    /// <summary>
    /// Performance numbers for the release page (0.10.9, Stompa: "some number to refer how it affects the FPS").
    /// - Our own CPU time: every per-frame entry point of the mod (each component's Update/LateUpdate, the
    ///   per-object gore updates, hit processing in Plugin.LateUpdate, the shot patch) opens a
    ///   <see cref="Measure"/> scope; the ticks add up per frame. That is the mod's main-thread cost, exact.
    ///   Only the outermost scope counts, so a scope opened inside another is not counted twice.
    /// - The game's frame time (Time.unscaledDeltaTime) for FPS average and 1 % low. GPU cost of our meshes and
    ///   decals only shows here, so the FPS comparison is `General / Enabled` on vs off in the same scene.
    /// - `[Perf]` line every 10 s in a raid (`General / Performance log`); F7 (`General / Benchmark key`) = one
    ///   60 s window with a `[Perf] BENCHMARK` summary. Effect counts are logged with each line.
    /// - 0.10.10 (0.10.9 benchmark: a 5-6 ms worst frame in every 10 s window with nothing moving): each scope
    ///   names its <see cref="PerfArea"/>; the worst frame is logged with the area that spent most of it, and
    ///   the benchmark line splits the average by area.
    /// Runs last in the frame (execution order 32000) so most of the frame's scopes are already counted.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class Perf : MonoBehaviour
    {
        internal readonly struct Scope : IDisposable
        {
            private readonly long _start;
            private readonly PerfArea _area;
            private readonly bool _outer;

            internal Scope(long start, PerfArea area, bool outer)
            {
                _start = start;
                _area = area;
                _outer = outer;
            }

            public void Dispose()
            {
                if (!_outer) return;
                _depth = 0;
                long spent = Clock.ElapsedTicks - _start;
                _ticks += spent;
                AreaTicks[(int)_area] += spent;
            }
        }

        private static readonly int AreaCount = Enum.GetValues(typeof(PerfArea)).Length;
        private static readonly string[] AreaNames = Enum.GetNames(typeof(PerfArea));

        private sealed class Window
        {
            public readonly List<float> FrameMs = new List<float>(8192);
            public readonly double[] AreaMsSum = new double[AreaCount];
            public double ModMsSum, ModMsMax;
            public int WorstArea = -1;
            public double WorstAreaMs;
            public float Start;

            public void Reset(float now)
            {
                FrameMs.Clear();
                Array.Clear(AreaMsSum, 0, AreaMsSum.Length);
                ModMsSum = 0;
                ModMsMax = 0;
                WorstArea = -1;
                WorstAreaMs = 0;
                Start = now;
            }
        }

        private const float LogEvery = 10f;
        private const float BenchmarkSeconds = 60f;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly long[] AreaTicks = new long[AreaCount];
        private static readonly double[] AreaMs = new double[AreaCount];
        private static long _ticks;
        private static int _depth;

        private readonly Window _log = new Window();
        private readonly Window _bench = new Window();
        private bool _benchRunning, _inRaid;

        /// <summary>Opens a timing scope: `using var perf = Perf.Measure(PerfArea.X);` at the top of a per-frame entry point.</summary>
        internal static Scope Measure(PerfArea area)
        {
            bool outer = _depth == 0;
            if (outer) _depth = 1;
            return new Scope(Clock.ElapsedTicks, area, outer);
        }

        private void LateUpdate()
        {
            double modMs = _ticks * 1000.0 / Stopwatch.Frequency;
            _ticks = 0;
            for (int a = 0; a < AreaCount; a++)
            {
                AreaMs[a] = AreaTicks[a] * 1000.0 / Stopwatch.Frequency;
                AreaTicks[a] = 0;
            }

            bool inRaid = Singleton<GameWorld>.Instantiated;
            float now = Time.realtimeSinceStartup;
            if (!inRaid)
            {
                _inRaid = false;
                _benchRunning = false;
                return;
            }
            if (!_inRaid)
            {
                // First frame of a raid: start clean (loading frames are not the mod's).
                _inRaid = true;
                _log.Reset(now);
                return;
            }

            float frameMs = Time.unscaledDeltaTime * 1000f;
            Add(_log, frameMs, modMs);
            if (_benchRunning) Add(_bench, frameMs, modMs);

            if (Plugin.BenchmarkKey.Value.IsDown())
            {
                _benchRunning = true;
                _bench.Reset(now);
                Plugin.Log.LogInfo($"[Perf] BENCHMARK started: {BenchmarkSeconds:F0} s, RealTargets {(Plugin.ModEnabled.Value ? "ENABLED" : "DISABLED")}; keep the view steady");
            }
            if (_benchRunning && now - _bench.Start >= BenchmarkSeconds)
            {
                _benchRunning = false;
                Plugin.Log.LogInfo($"[Perf] BENCHMARK {Summary(_bench, now)} | by area {Areas(_bench)} | RealTargets {(Plugin.ModEnabled.Value ? "ENABLED" : "DISABLED")} | {Counts()}");
            }
            if (now - _log.Start >= LogEvery)
            {
                if (Plugin.PerfLog.Value) Plugin.Log.LogInfo($"[Perf] {Summary(_log, now)} | {Counts()}");
                _log.Reset(now);
            }
        }

        private static void Add(Window w, float frameMs, double modMs)
        {
            w.FrameMs.Add(frameMs);
            w.ModMsSum += modMs;
            for (int a = 0; a < AreaCount; a++) w.AreaMsSum[a] += AreaMs[a];
            if (modMs > w.ModMsMax)
            {
                w.ModMsMax = modMs;
                // The area that spent most of the worst frame.
                int top = 0;
                for (int a = 1; a < AreaCount; a++)
                {
                    if (AreaMs[a] > AreaMs[top]) top = a;
                }
                w.WorstArea = top;
                w.WorstAreaMs = AreaMs[top];
            }
        }

        /// <summary>"10 s: 71.8 fps (13.9 ms), 1 % low 48 fps | RealTargets 0.21 ms/frame = 1.5 %, worst 3.4 ms (Gore 3.1 ms)".</summary>
        private static string Summary(Window w, float now)
        {
            int n = w.FrameMs.Count;
            if (n == 0) return "no frames";
            double sum = 0;
            foreach (var ms in w.FrameMs) sum += ms;
            double avgMs = sum / n;
            w.FrameMs.Sort();
            // 1 % low: the frame time 99 % of frames are faster than.
            float p99 = w.FrameMs[Mathf.Clamp(Mathf.CeilToInt(n * 0.99f) - 1, 0, n - 1)];
            double modAvg = w.ModMsSum / n;
            string worstArea = w.WorstArea >= 0 && w.ModMsMax >= 0.05 ? $" ({AreaNames[w.WorstArea]} {w.WorstAreaMs:F2} ms)" : "";
            return $"{now - w.Start:F0} s, {n} frames: {1000.0 / avgMs:F1} fps ({avgMs:F1} ms), 1 % low {1000f / Mathf.Max(0.01f, p99):F0} fps | " +
                   $"RealTargets {modAvg:F3} ms/frame = {modAvg / avgMs * 100.0:F2} % of the frame, worst frame {w.ModMsMax:F2} ms{worstArea}";
        }

        /// <summary>"Hits 0.004, Bleed 0.010, ... ms/frame".</summary>
        private static string Areas(Window w)
        {
            int n = Mathf.Max(1, w.FrameMs.Count);
            var sb = new StringBuilder();
            for (int a = 0; a < AreaCount; a++)
            {
                if (a > 0) sb.Append(", ");
                sb.Append(AreaNames[a]).Append(' ').Append((w.AreaMsSum[a] / n).ToString("F3"));
            }
            return sb.Append(" ms/frame").ToString();
        }

        private static string Counts()
        {
            var ground = GroundBlood.Instance;
            var gore = Dismemberment.Instance;
            var bleed = BleedSystem.Instance;
            var wounds = WoundDecals.Instance;
            return $"bleeding bodies {(bleed != null ? bleed.Count : 0)}, gore pieces {(gore != null ? gore.PieceCount : 0)} " +
                   $"({GorePiece.Moving} moving), ground marks {(ground != null ? ground.MarkCount : 0)} in {(ground != null ? ground.ChunkCount : 0)} chunks, " +
                   $"pools {(ground != null ? ground.PoolCount : 0)}, body patches {(wounds != null ? wounds.PatchCount : 0)} in {(wounds != null ? wounds.RendererCount : 0)} renderers, " +
                   $"light scans {SceneLight.TakeScanStats()}";
        }
    }
}
