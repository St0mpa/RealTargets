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
    /// - fika-3 (Stompa, Fika T1 raid: "was it caused by us or fika or combined?"): every 10 s line also counts the
    ///   hitches (frames over 2x the window's median and over 25 ms) and how many were ours (our time covered at
    ///   least half of what the frame ran over the median), plus the split by area. Unity reports a frame's length
    ///   one frame later (unscaledDeltaTime = the previous frame), so our time is paired with the next delta.
    /// - perf-3 (perf-2: >= 2.2 ms/frame more with 9 bleeding bodies, but our own timers saw only 0.17-0.27 ms, so the rest is
    ///   Unity drawing or simulating our objects): F8 (`General / Split benchmark key`) runs <see cref="SplitSteps"/>, 30 s
    ///   windows each with some kinds of our blood hidden (renderers off; droplets cleared and not emitted); the gap to the
    ///   all-on steps is what that kind costs. The first and last steps are both all on: their gap is the drift (bleeding
    ///   slowing, pools growing) over the run. Hiding stops the drawing only: our CPU work (pool fields, texture uploads,
    ///   patch uploads) goes on and stays in our timers.
    /// Runs last in the frame (execution order 32000) so most of the frame's scopes are already counted, and after every
    /// gore LateUpdate, so a renderer an owner switched on this frame is hidden again before the frame is drawn.
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

        /// <summary>perf-4: a named step's time (main thread only: the counters are not thread-safe).</summary>
        internal readonly struct PartScope : IDisposable
        {
            private readonly long _start;
            private readonly PerfPart _part;

            internal PartScope(long start, PerfPart part)
            {
                _start = start;
                _part = part;
            }

            public void Dispose()
            {
                PartTicks[(int)_part] += Clock.ElapsedTicks - _start;
                PartCounts[(int)_part]++;
            }
        }

        private static readonly int AreaCount = Enum.GetValues(typeof(PerfArea)).Length;
        private static readonly string[] AreaNames = Enum.GetNames(typeof(PerfArea));
        private static readonly int PartCount = Enum.GetValues(typeof(PerfPart)).Length;
        private static readonly string[] PartNames = Enum.GetNames(typeof(PerfPart));
        private static readonly long[] PartTicks = new long[PartCount];
        private static readonly int[] PartCounts = new int[PartCount];
        /// <summary>perf-4: a frame of ours at least this long logs its named steps (`[Perf] slow frame`), at most twice a second.</summary>
        private const double SlowFrameMs = 3.0;
        private float _nextSlowLog;

        private sealed class Window
        {
            public readonly List<float> FrameMs = new List<float>(8192);
            /// <summary>Our time in each frame of FrameMs (same index).</summary>
            public readonly List<float> ModMs = new List<float>(8192);
            public readonly double[] AreaMsSum = new double[AreaCount];
            public double ModMsSum, ModMsMax;
            public int WorstArea = -1;
            public double WorstAreaMs;
            public float Start;
            public int GcStart;

            public void Reset(float now)
            {
                FrameMs.Clear();
                ModMs.Clear();
                Array.Clear(AreaMsSum, 0, AreaMsSum.Length);
                ModMsSum = 0;
                ModMsMax = 0;
                WorstArea = -1;
                WorstAreaMs = 0;
                Start = now;
                GcStart = GC.CollectionCount(0);
            }
        }

        private const float LogEvery = 10f;
        private const float BenchmarkSeconds = 60f;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly long[] AreaTicks = new long[AreaCount];
        private static readonly double[] AreaMs = new double[AreaCount];
        // Last frame's numbers: this frame's unscaledDeltaTime is last frame's length.
        private static readonly double[] PrevAreaMs = new double[AreaCount];
        private static double _prevModMs;
        private static bool _havePrev;
        private static readonly List<float> Sorted = new List<float>(8192);
        private const float HitchMinMs = 25f;
        private static long _ticks;
        private static int _depth;

        private readonly Window _log = new Window();
        private readonly Window _bench = new Window();
        private bool _benchRunning, _inRaid;
        /// <summary>Benchmarks started this raid (shown and logged as #1, #2, ...).</summary>
        private int _benchNumber;
        private string _banner;
        private float _bannerUntil;
        private GUIStyle _bannerStyle;

        // ---- perf-3 split benchmark ----
        private static readonly (string Name, bool Droplets, bool Patches, bool Ground, bool Overlay)[] SplitSteps =
        {
            ("all on", false, false, false, false),
            ("droplets hidden", true, false, false, false),
            ("body patches hidden", false, true, false, false),
            ("pools and ground marks hidden", false, false, true, false),
            ("all blood hidden", true, true, true, false),
            ("all blood and the overlay hidden", true, true, true, true),
            ("all on again", false, false, false, false),
        };
        private const float SplitSettle = 3f;
        private const float SplitStepSeconds = 30f;

        /// <summary>Kinds hidden right now by the split benchmark (the overlay reads HideOverlay itself).</summary>
        internal static bool HideDroplets, HidePatches, HideGround, HideOverlay;
        private static readonly List<Renderer> Found = new List<Renderer>(256);
        private static readonly HashSet<Renderer> HiddenPatches = new HashSet<Renderer>();
        private static readonly HashSet<Renderer> HiddenGround = new HashSet<Renderer>();

        private readonly Window _split = new Window();
        private readonly double[] _splitMs = new double[SplitSteps.Length];
        private readonly float[] _splitLow = new float[SplitSteps.Length];
        /// <summary>Step running, -1 = none. Frames count from SplitSettle after the step's start.</summary>
        private int _splitStep = -1;
        private int _splitNumber;
        private float _splitStepStart;
        private bool _splitCounting;

        /// <summary>
        /// perf-1 (Stompa: "make instructions for me in raid clearer"): the benchmark shows itself at the top of the screen, a
        /// countdown while it runs and its result for 10 s after, so nobody has to count a minute or guess whether F7 took.
        /// </summary>
        private void OnGUI()
        {
            if (!_inRaid) return;
            float now = Time.realtimeSinceStartup;
            string text = _benchRunning
                ? $"BENCHMARK #{_benchNumber}  {Mathf.CeilToInt(BenchmarkSeconds - (now - _bench.Start))} s left  -  keep the view still, don't press anything"
                : _splitStep >= 0 ? SplitBanner(now)
                : now < _bannerUntil ? _banner : null;
            if (text == null) return;
            if (_bannerStyle == null)
            {
                _bannerStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false };
            }
            var rect = new Rect(0f, 40f, Screen.width, 36f);
            var shadow = new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height);
            var old = GUI.color;
            GUI.color = Color.black;
            GUI.Label(shadow, text, _bannerStyle);
            GUI.color = _benchRunning || _splitStep >= 0 ? new Color(1f, 0.85f, 0.3f) : new Color(0.6f, 1f, 0.6f);
            GUI.Label(rect, text, _bannerStyle);
            GUI.color = old;
        }

        /// <summary>"109 fps, 1 % low 92 fps" for the on-screen result (after Summary sorted the frames).</summary>
        private static string Short(Window w)
        {
            int n = w.FrameMs.Count;
            if (n == 0) return "no frames";
            double sum = 0;
            foreach (var ms in w.FrameMs) sum += ms;
            float p99 = w.FrameMs[Mathf.Clamp(Mathf.CeilToInt(n * 0.99f) - 1, 0, n - 1)];
            return $"{1000.0 / (sum / n):F0} fps, 1 % low {1000f / Mathf.Max(0.01f, p99):F0} fps";
        }

        /// <summary>Opens a timing scope: `using var perf = Perf.Measure(PerfArea.X);` at the top of a per-frame entry point.</summary>
        internal static Scope Measure(PerfArea area)
        {
            bool outer = _depth == 0;
            if (outer) _depth = 1;
            return new Scope(Clock.ElapsedTicks, area, outer);
        }

        /// <summary>Times a named step: `using var part = Perf.Part(PerfPart.X);` (main thread only).</summary>
        internal static PartScope Part(PerfPart part) => new PartScope(Clock.ElapsedTicks, part);

        /// <summary>"[Perf] slow frame 5.92 ms (Ground 5.80 ms): PoolColour 5.61 ms x1, PoolTick 5.78 ms x1" (steps may nest).</summary>
        private static string SlowFrame(double modMs)
        {
            int top = 0;
            for (int a = 1; a < AreaCount; a++)
            {
                if (AreaMs[a] > AreaMs[top]) top = a;
            }
            var sb = new StringBuilder();
            sb.Append($"[Perf] slow frame {modMs:F2} ms ({AreaNames[top]} {AreaMs[top]:F2} ms):");
            bool any = false;
            // Biggest first; steps under 0.05 ms left out.
            var order = new List<int>(PartCount);
            for (int p = 0; p < PartCount; p++)
            {
                if (PartTicks[p] * 1000.0 / Stopwatch.Frequency >= 0.05) order.Add(p);
            }
            order.Sort((x, y) => PartTicks[y].CompareTo(PartTicks[x]));
            foreach (int p in order)
            {
                sb.Append(any ? ", " : " ").Append(PartNames[p]).Append(' ')
                  .Append((PartTicks[p] * 1000.0 / Stopwatch.Frequency).ToString("F2")).Append(" ms x").Append(PartCounts[p]);
                any = true;
            }
            if (!any) sb.Append(" no named step over 0.05 ms");
            return sb.ToString();
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
            if (inRaid && _inRaid && modMs >= SlowFrameMs && Plugin.PerfLog.Value && now >= _nextSlowLog)
            {
                _nextSlowLog = now + 0.5f;
                Plugin.Log.LogInfo(SlowFrame(modMs));
            }
            Array.Clear(PartTicks, 0, PartCount);
            Array.Clear(PartCounts, 0, PartCount);
            if (!inRaid)
            {
                if (_benchRunning) Plugin.Log.LogInfo($"[Perf] BENCHMARK #{_benchNumber} cut short: the raid ended");
                if (_splitStep >= 0)
                {
                    Plugin.Log.LogInfo($"[Perf] SPLIT #{_splitNumber} cut short in step {_splitStep + 1}: the raid ended");
                    _splitStep = -1;
                }
                _splitCounting = false;
                SetHidden(false, false, false, false);
                HiddenPatches.Clear(); // their objects died with the raid
                HiddenGround.Clear();
                _inRaid = false;
                _benchRunning = false;
                _havePrev = false;
                return;
            }
            if (!_inRaid)
            {
                // First frame of a raid: start clean (loading frames are not the mod's).
                _inRaid = true;
                _benchNumber = 0;
                _splitNumber = 0;
                _log.Reset(now);
                Keep(modMs);
                return;
            }

            // This delta is last frame's length: pair it with last frame's time of ours.
            float frameMs = Time.unscaledDeltaTime * 1000f;
            if (_havePrev)
            {
                Add(_log, frameMs, _prevModMs, PrevAreaMs);
                if (_benchRunning) Add(_bench, frameMs, _prevModMs, PrevAreaMs);
                if (_splitCounting) Add(_split, frameMs, _prevModMs, PrevAreaMs);
            }
            Keep(modMs);

            if (Plugin.BenchmarkKey.Value.IsDown())
            {
                if (_benchRunning)
                {
                    // perf-1 (Stompa's run 1: a second F7 restarted the running benchmark, so it never reported): ignored.
                    Plugin.Log.LogInfo($"[Perf] BENCHMARK #{_benchNumber} still running ({BenchmarkSeconds - (now - _bench.Start):F0} s left): key ignored");
                }
                else if (_splitStep >= 0)
                {
                    Plugin.Log.LogInfo($"[Perf] SPLIT #{_splitNumber} running (step {_splitStep + 1}): benchmark key ignored");
                }
                else
                {
                    _benchRunning = true;
                    _benchNumber++;
                    _bench.Reset(now);
                    Plugin.Log.LogInfo($"[Perf] BENCHMARK #{_benchNumber} started: {BenchmarkSeconds:F0} s, RealTargets {(Plugin.ModEnabled.Value ? "ENABLED" : "DISABLED")}, {FrameCap()}; keep the view steady");
                }
            }
            if (Plugin.SplitBenchmarkKey.Value.IsDown())
            {
                if (_benchRunning || _splitStep >= 0)
                {
                    Plugin.Log.LogInfo($"[Perf] split key ignored: {(_benchRunning ? $"BENCHMARK #{_benchNumber}" : $"SPLIT #{_splitNumber}")} still running");
                }
                else
                {
                    _splitNumber++;
                    Plugin.Log.LogInfo($"[Perf] SPLIT #{_splitNumber} started: {SplitSteps.Length} steps of {SplitStepSeconds:F0} s (+{SplitSettle:F0} s settle each), " +
                                       $"RealTargets {(Plugin.ModEnabled.Value ? "ENABLED" : "DISABLED")}, {FrameCap()} | {Counts()}");
                    BeginSplitStep(0, now);
                }
            }
            if (_splitStep >= 0)
            {
                if (!_splitCounting && now - _splitStepStart >= SplitSettle)
                {
                    _splitCounting = true;
                    _split.Reset(now);
                }
                if (now - _splitStepStart >= SplitSettle + SplitStepSeconds) FinishSplitStep(now);
            }
            ApplyHide();
            if (_benchRunning && now - _bench.Start >= BenchmarkSeconds)
            {
                _benchRunning = false;
                string summary = Summary(_bench, now);
                Plugin.Log.LogInfo($"[Perf] BENCHMARK #{_benchNumber} {summary} | by area {Areas(_bench)} | RealTargets {(Plugin.ModEnabled.Value ? "ENABLED" : "DISABLED")} | {Counts()}");
                _banner = $"Benchmark #{_benchNumber} done: {Short(_bench)}";
                _bannerUntil = now + 10f;
            }
            if (now - _log.Start >= LogEvery)
            {
                if (Plugin.PerfLog.Value) Plugin.Log.LogInfo($"[Perf] {Summary(_log, now)} | by area {Areas(_log)} | {Counts()}");
                _log.Reset(now);
            }
        }

        // ---- perf-3 split benchmark ----

        private string SplitBanner(float now)
        {
            float step = SplitSettle + SplitStepSeconds;
            float left = step - (now - _splitStepStart) + (SplitSteps.Length - 1 - _splitStep) * step;
            return $"SPLIT #{_splitNumber}  step {_splitStep + 1}/{SplitSteps.Length}: {SplitSteps[_splitStep].Name}  -  " +
                   $"{Mathf.CeilToInt(left)} s left  -  keep the view still, don't press anything";
        }

        private void BeginSplitStep(int step, float now)
        {
            _splitStep = step;
            _splitStepStart = now;
            _splitCounting = false;
            var s = SplitSteps[step];
            SetHidden(s.Droplets, s.Patches, s.Ground, s.Overlay);
        }

        private void FinishSplitStep(float now)
        {
            int n = _split.FrameMs.Count;
            double sum = 0;
            foreach (var ms in _split.FrameMs) sum += ms;
            string summary = Summary(_split, now); // sorts FrameMs
            _splitMs[_splitStep] = n > 0 ? sum / n : 0;
            _splitLow[_splitStep] = n > 0 ? 1000f / Mathf.Max(0.01f, _split.FrameMs[Mathf.Clamp(Mathf.CeilToInt(n * 0.99f) - 1, 0, n - 1)]) : 0f;
            Plugin.Log.LogInfo($"[Perf] SPLIT #{_splitNumber} step {_splitStep + 1}/{SplitSteps.Length} {SplitSteps[_splitStep].Name}: {summary} | " +
                               $"by area {Areas(_split)} | {Counts()}");
            if (_splitStep + 1 < SplitSteps.Length)
            {
                BeginSplitStep(_splitStep + 1, now);
                return;
            }

            // Saved = baseline (mean of the two all-on steps) minus the step's frame time.
            int last = SplitSteps.Length - 1;
            double baseline = (_splitMs[0] + _splitMs[last]) / 2.0;
            var sb = new StringBuilder();
            sb.Append($"[Perf] SPLIT #{_splitNumber} result, RealTargets {(Plugin.ModEnabled.Value ? "ENABLED" : "DISABLED")}: all on {baseline:F2} ms " +
                      $"({Fps(baseline)} fps; first {_splitMs[0]:F2}, last {_splitMs[last]:F2} = drift {_splitMs[last] - _splitMs[0]:+0.00;-0.00} ms)");
            for (int i = 1; i < last; i++)
            {
                sb.Append($" | {SplitSteps[i].Name} {_splitMs[i]:F2} ms = saves {baseline - _splitMs[i]:+0.00;-0.00} ms ({Fps(_splitMs[i])} fps, 1 % low {_splitLow[i]:F0})");
            }
            Plugin.Log.LogInfo(sb.ToString());
            _banner = $"Split #{_splitNumber} done: all blood {baseline - _splitMs[4]:F2} ms (droplets {baseline - _splitMs[1]:F2}, patches " +
                      $"{baseline - _splitMs[2]:F2}, ground {baseline - _splitMs[3]:F2}), overlay {_splitMs[4] - _splitMs[5]:F2} ms";
            _bannerUntil = now + 20f;
            _splitStep = -1;
            _splitCounting = false;
            SetHidden(false, false, false, false);
        }

        private static string Fps(double ms) => ms > 0 ? (1000.0 / ms).ToString("F1") : "-";

        private static void SetHidden(bool droplets, bool patches, bool ground, bool overlay)
        {
            if (droplets && !HideDroplets)
            {
                // What is in flight goes too, or the step would still draw (and collide) the last 2 s of drops.
                if (BloodParticles.Instance != null) BloodParticles.Instance.ClearDroplets();
                if (BloodMist.Instance != null) BloodMist.Instance.ResetRaid();
            }
            HideDroplets = droplets;
            BloodParticles.PerfHidden = droplets;
            HidePatches = patches;
            HideGround = ground;
            HideOverlay = overlay;
        }

        /// <summary>Every frame: renderers of a hidden kind off (new ones too), the ones we switched off back on when shown.</summary>
        private static void ApplyHide()
        {
            Found.Clear();
            if (HidePatches && WoundDecals.Instance != null) WoundDecals.Instance.CollectRenderers(Found);
            Hide(HidePatches, HiddenPatches);
            Found.Clear();
            if (HideGround && GroundBlood.Instance != null) GroundBlood.Instance.CollectRenderers(Found);
            Hide(HideGround, HiddenGround);
        }

        private static void Hide(bool hide, HashSet<Renderer> hidden)
        {
            if (hide)
            {
                foreach (var r in Found)
                {
                    if (r == null || !r.enabled) continue;
                    r.enabled = false;
                    hidden.Add(r);
                }
            }
            else if (hidden.Count > 0)
            {
                foreach (var r in hidden)
                {
                    if (r != null) r.enabled = true;
                }
                hidden.Clear();
            }
        }

        /// <summary>"fps cap -1, vsync 0": the game's own limits (perf-2's mod-off runs sat on a 144 fps cap).</summary>
        private static string FrameCap() => $"fps cap {Application.targetFrameRate} (-1 = none), vsync {QualitySettings.vSyncCount}";

        private static void Keep(double modMs)
        {
            _prevModMs = modMs;
            Array.Copy(AreaMs, PrevAreaMs, AreaCount);
            _havePrev = true;
        }

        private static void Add(Window w, float frameMs, double modMs, double[] areaMs)
        {
            w.FrameMs.Add(frameMs);
            w.ModMs.Add((float)modMs);
            w.ModMsSum += modMs;
            for (int a = 0; a < AreaCount; a++) w.AreaMsSum[a] += areaMs[a];
            if (modMs > w.ModMsMax)
            {
                w.ModMsMax = modMs;
                // The area that spent most of the worst frame.
                int top = 0;
                for (int a = 1; a < AreaCount; a++)
                {
                    if (areaMs[a] > areaMs[top]) top = a;
                }
                w.WorstArea = top;
                w.WorstAreaMs = areaMs[top];
            }
        }

        /// <summary>
        /// "hitches 6 (> 38 ms, worst 92 ms): ours 1, our 14.0 of 260.3 ms over": frames over 2x the median and
        /// HitchMinMs; ours = our time covered at least half of what that frame ran over the median.
        /// </summary>
        private static string Hitches(Window w)
        {
            int n = w.FrameMs.Count;
            if (n == 0) return "hitches -";
            Sorted.Clear();
            Sorted.AddRange(w.FrameMs);
            Sorted.Sort();
            float median = Sorted[n / 2];
            float limit = Mathf.Max(2f * median, HitchMinMs);
            int count = 0, ours = 0;
            double over = 0, ourOver = 0;
            float worst = 0f;
            for (int i = 0; i < n; i++)
            {
                float ms = w.FrameMs[i];
                if (ms <= limit) continue;
                count++;
                float extra = ms - median;
                float mine = Mathf.Min(w.ModMs[i], extra);
                over += extra;
                ourOver += mine;
                if (mine >= 0.5f * extra) ours++;
                if (ms > worst) worst = ms;
            }
            return count == 0
                ? $"hitches 0 (> {limit:F0} ms)"
                : $"hitches {count} (> {limit:F0} ms, worst {worst:F0} ms): ours {ours}, our {ourOver:F1} of {over:F1} ms over";
        }

        /// <summary>"10 s: 71.8 fps (13.9 ms), 1 % low 48 fps | RealTargets 0.21 ms/frame = 1.5 %, worst 3.4 ms (Gore 3.1 ms)".</summary>
        private static string Summary(Window w, float now)
        {
            int n = w.FrameMs.Count;
            if (n == 0) return "no frames";
            // Before the sort below: hitches pair each frame with our time in it.
            string hitches = Hitches(w);
            double sum = 0;
            foreach (var ms in w.FrameMs) sum += ms;
            double avgMs = sum / n;
            w.FrameMs.Sort();
            // 1 % low: the frame time 99 % of frames are faster than.
            float p99 = w.FrameMs[Mathf.Clamp(Mathf.CeilToInt(n * 0.99f) - 1, 0, n - 1)];
            double modAvg = w.ModMsSum / n;
            string worstArea = w.WorstArea >= 0 && w.ModMsMax >= 0.05 ? $" ({AreaNames[w.WorstArea]} {w.WorstAreaMs:F2} ms)" : "";
            return $"{now - w.Start:F0} s, {n} frames: {1000.0 / avgMs:F1} fps ({avgMs:F1} ms), 1 % low {1000f / Mathf.Max(0.01f, p99):F0} fps | " +
                   $"RealTargets {modAvg:F3} ms/frame = {modAvg / avgMs * 100.0:F2} % of the frame, worst frame {w.ModMsMax:F2} ms{worstArea} | {hitches} | GC {GC.CollectionCount(0) - w.GcStart}";
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
            var drops = BloodParticles.Instance;
            return $"bleeding bodies {(bleed != null ? bleed.Count : 0)}, droplets {(drops != null ? drops.AliveCount : 0)}, gore pieces {(gore != null ? gore.PieceCount : 0)} " +
                   $"({GorePiece.Moving} moving), ground marks {(ground != null ? ground.MarkCount : 0)} in {(ground != null ? ground.ChunkCount : 0)} chunks, " +
                   $"pools {(ground != null ? ground.PoolCount : 0)}, body patches {(wounds != null ? wounds.PatchCount : 0)} in {(wounds != null ? wounds.RendererCount : 0)} renderers, " +
                   $"light scans {SceneLight.TakeScanStats()}";
        }
    }
}
