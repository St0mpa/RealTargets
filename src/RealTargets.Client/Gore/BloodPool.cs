using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// One spreading pool of blood on the floor (1.2.0 blood realism pass). A Forge user: pools grow "in a very mechanical
    /// looking way"; until 1.1.0 every pool was one of four blob textures scaled up around a fixed centre, rebuilt in
    /// 8 mm steps every 0.4 s: a sticker being zoomed in.
    /// Now each pool has an **arrival-time field**: how long blood takes to reach each texel of a 128x128 patch, spreading
    /// from where it leaves the body (one seed per opening, a later wound starting later), faster downhill (the floor's
    /// heights from raycasts), slower where a noise field says the floor is rougher, never across a step, ledge or wall.
    /// The field is computed once off the main thread (Dijkstra on a 16-neighbour grid) and stored in the texture's alpha
    /// (early = high). The lit cutout shader draws where alpha > _Cutoff, so lowering the cutoff every frame moves the
    /// edge continuously and unevenly, lobes and fingers running ahead where the floor lets them, no mesh rebuild.
    /// The cutoff comes from the wetted area: the volume the bleed model has drained, as a 2.5 mm film (blocked texels
    /// don't count, so blood held back by a wall spreads further elsewhere).
    /// The patch starts 0.7 m across and is recomputed at 1.4 / 2.8 m when the blood nears its edge (same world-space
    /// noise and seeds, so the wet shape carries over). Colour is baked per texel: thicker (earlier) darker, the tone's
    /// oxygen early on, clotting darker after ~10 min, the thin rim drying red-brown from ~15 min while the middle stays wet.
    /// Non-cutout fallback modes threshold the alpha on the CPU a few times a second instead.
    /// </summary>
    internal sealed class BloodPool
    {
        internal const int N = 128;
        /// <summary>
        /// Floor raycast grid per side. blood-5c (Stompa: an old pool "looks really fake", straight boxy edges at a kerb): was
        /// 17, i.e. 17.5 cm cells at the 2.8 m patch, and a cell touching an obstacle was dropped whole; 33 halves that.
        /// </summary>
        private const int M = 33;
        private const float FirstHalf = 0.35f;
        private const float MaxHalf = 1.4f;
        internal const float Lift = 0.006f;
        private const float Film = 0.0025f;

        public GameObject Go;
        public Mesh Mesh;
        public MeshRenderer Renderer;
        public Texture2D Texture;
        public Vector3 Center, Normal, Axis, Side;
        public string Label;
        public float Half;
        public float NoiseX, NoiseY;
        /// <summary>0 venous .. 1 arterial: how scarlet it starts.</summary>
        public float Oxygen;
        public float TargetArea, ShownArea;
        public float Front;
        public float GrewAt;
        public float CreatedAt;
        /// <summary>For footprints/drag: 0 fresh .. 255 dried (clot and rim drying together).</summary>
        public byte Dry;
        /// <summary>blood-5c: drops laid round it so far (GroundBlood.Satellite) and the radius (m) at which the next ones come.</summary>
        public int Satellites;
        public float NextSatellite = 0.1f;

        // Current field (null until the first job finishes).
        private Field _field;
        private Field _pending;
        private float _lastCutoff = -1f;
        private float _clot = -1f, _dried = -1f, _colourFront = -1f, _nextColour, _nextWindow;
        /// <summary>Arrival time mapped to alpha 1..0.2 (see AlphaOf); re-baked as the edge nears it.</summary>
        private float _window;
        private bool _rimBaked;
        private float _nextCpuAlpha;
        private float _nextReseed;
        // Shown colours and alpha (what the texture holds); perf-4: the next bake writes the back pair on a pool thread.
        private Color32[] _pixels;
        private byte[] _alpha;
        private Color32[] _backPixels;
        private byte[] _backAlpha;
        /// <summary>The colour bake in flight (at most one per pool), uploaded by Tick when done.</summary>
        private Colouring _colouring;
        private readonly List<Vector2> _seeds = new List<Vector2>();
        private readonly List<float> _seedStart = new List<float>();

        public bool Visible => _field != null;
        public float Radius => Mathf.Sqrt(ShownArea / Mathf.PI);

        /// <summary>Patch-plane coordinates (m) of a world point.</summary>
        private Vector2 Local(Vector3 p)
        {
            var d = p - Center;
            return new Vector2(Vector3.Dot(d, Axis), Vector3.Dot(d, Side));
        }

        /// <summary>Starts the pool at <paramref name="ground"/> with its first sources (the openings blood runs from).</summary>
        internal void Start(Vector3 ground, Vector3 normal, IList<Vector3> sources)
        {
            Center = ground;
            Normal = normal;
            Axis = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
            Axis = Quaternion.AngleAxis(Random.Range(0f, 360f), normal) * Axis;
            Side = Vector3.Cross(normal, Axis).normalized;
            NoiseX = Random.Range(0f, 200f);
            NoiseY = Random.Range(0f, 200f);
            CreatedAt = GrewAt = Time.time;
            Half = FirstHalf;
            foreach (var s in sources) AddSeed(s, 0f);
            if (_seeds.Count == 0) AddSeed(ground, 0f);
            Launch(Half);
        }

        private bool AddSeed(Vector3 world, float start)
        {
            var at = Local(world);
            foreach (var s in _seeds)
            {
                if ((s - at).sqrMagnitude < 0.08f * 0.08f) return false;
            }
            _seeds.Add(at);
            _seedStart.Add(start);
            return true;
        }

        /// <summary>New openings (a corpse shot again): each starts spreading from now; recomputed at most every 2 s.</summary>
        internal void UpdateSources(IList<Vector3> sources)
        {
            if (sources == null || _pending != null || Time.time < _nextReseed) return;
            bool added = false;
            foreach (var s in sources)
            {
                if (Local(s).magnitude > Half * 1.6f) continue;
                if (AddSeed(s, Front)) added = true;
            }
            if (!added) return;
            _nextReseed = Time.time + 2f;
            Launch(Half);
        }

        /// <summary>Raycasts the floor for a patch of half size <paramref name="half"/> and computes its field off the main thread.</summary>
        private void Launch(float half)
        {
            using var part = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.PoolRaycasts);
            var f = new Field { N = N, Half = half, NoiseX = NoiseX, NoiseY = NoiseY };
            f.Seeds.AddRange(_seeds);
            f.SeedStart.AddRange(_seedStart);
            f.Vertices = new Vector3[M * M];
            f.Height = new float[M * M];
            f.On = new bool[M * M];
            // perf-5 (perf-4 raid: 4-5.5 ms for the 1089 casts one by one, at every new pool and growth step): one batch on
            // Unity's job workers, completed here (same frame, same physics scene); the hits are read on this thread.
            var flats = new Vector3[M * M];
            var commands = new NativeArray<RaycastCommand>(M * M, Allocator.TempJob);
            var hits = new NativeArray<RaycastHit>(M * M, Allocator.TempJob);
            try
            {
                var query = new QueryParameters(LayersMaskController.HighPolyWithTerrainMask, false, QueryTriggerInteraction.UseGlobal, Physics.queriesHitBackfaces);
                for (int j = 0; j < M; j++)
                {
                    for (int i = 0; i < M; i++)
                    {
                        int k = j * M + i;
                        float u = (i / (float)(M - 1) * 2f - 1f) * half, v = (j / (float)(M - 1) * 2f - 1f) * half;
                        flats[k] = Center + Axis * u + Side * v;
                        commands[k] = new RaycastCommand(flats[k] + Vector3.up * 0.2f, Vector3.down, query, 0.45f);
                    }
                }
                RaycastCommand.ScheduleBatch(commands, hits, 32, 1).Complete();
                Floor(f, flats, hits);
            }
            finally
            {
                commands.Dispose();
                hits.Dispose();
            }
            _pending = f;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => f.Run());
        }

        /// <summary>The field's vertices, heights and floor cells from the floor casts (main thread: hit colliders).</summary>
        private void Floor(Field f, Vector3[] flats, NativeArray<RaycastHit> hits)
        {
            for (int j = 0; j < M; j++)
            {
                for (int i = 0; i < M; i++)
                {
                    int k = j * M + i;
                    var flat = flats[k];
                    var p = flat + Normal * Lift;
                    bool on = false;
                    float h = 0f;
                    var hit = hits[k];
                    if (hit.colliderInstanceID != 0 && hit.collider != null && !GroundBlood.Movable(hit.collider))
                    {
                        h = Vector3.Dot(hit.point - Center, Normal);
                        // Blood doesn't climb onto rubble or run down past a ledge.
                        on = h <= 0.06f && h >= -0.12f && hit.normal.y > 0.6f;
                        // blood-5c: an off-floor vertex stays on the pool's plane (cells half on the floor are drawn now, their
                        // blocked texels clear): on a kerb top or over a drop it would tilt the open part up or away.
                        if (on) p = hit.point + hit.normal * Lift;
                        h = Mathf.Clamp(h, -0.12f, 0.12f);
                    }
                    f.Vertices[k] = p;
                    f.Height[k] = h;
                    f.On[k] = on;
                }
            }
        }

        /// <summary>Per frame: picks up a finished field, moves the edge toward the drained volume, dries, expands.</summary>
        internal void Tick(float dt, bool cutout, MaterialPropertyBlock block)
        {
            // perf-4: a finished colour bake is uploaded first (with its field, if it brings a new one).
            if (_colouring != null && _colouring.Done) FinishColour(cutout, block);
            if (_pending != null && _pending.Done && _colouring == null)
            {
                if (_pending.Error != null)
                {
                    Plugin.Log.LogWarning($"[Pool] {Label}: field failed: {_pending.Error}");
                    _pending = null;
                }
                else
                {
                    // The new field shows together with its colours; the old one stays until then (_pending stays set, so no
                    // other field starts meanwhile).
                    Recolour(_pending, true, true, false);
                }
            }
            if (_field == null) return;

            float before = ShownArea;
            ShownArea += (TargetArea - ShownArea) * (1f - Mathf.Exp(-dt / 0.6f));
            // blood-3: only real growth counts (> 5 cm^2/s). Both the bleed model's radius and this smoothing approach their target
            // forever, so "any growth" kept every pool fresh: it never clotted or dried.
            if (dt > 0f && (ShownArea - before) / dt > 5e-4f) GrewAt = Time.time;

            Front = _field.FrontFor(ShownArea);
            if (_colouring == null) Recolour(_field, false, false, false);
            float cutoff = CutoffFor(Front);
            if (Mathf.Abs(cutoff - _lastCutoff) > 1e-4f)
            {
                _lastCutoff = cutoff;
                if (cutout) Shade(block);
                else if (Time.time >= _nextCpuAlpha) CpuAlpha(cutoff);
            }

            // Nearing the patch edge or most of it wet: recompute twice as large (keeps showing this one meanwhile).
            if (_pending == null && Half < MaxHalf && (Front > _field.BorderMin * 0.8f || ShownArea > _field.WetCapacity * 0.45f))
            {
                Half *= 2f;
                Launch(Half);
            }
        }

        /// <summary>A colour bake finished (perf-4): its field (if new) and its colours go up together; main thread.</summary>
        private void FinishColour(bool cutout, MaterialPropertyBlock block)
        {
            var job = _colouring;
            _colouring = null;
            if (job.Error != null)
            {
                Plugin.Log.LogWarning($"[Pool] {Label}: colour bake failed: {job.Error}");
                if (job.NewField) _pending = null;
                return;
            }
            using var part = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.PoolUpload);
            if (!job.NewField)
            {
                Commit(job, block);
                return;
            }
            bool first = _field == null;
            int cells = ApplyField(job.Field);
            _pending = null;
            Commit(job, block);
            if (!cutout) CpuAlpha(CutoffFor(job.Field.FrontFor(ShownArea)));
            Renderer.enabled = true;
            Shade(block);

            if (Plugin.LogBlood.Value)
            {
                var f = job.Field;
                Plugin.Log.LogInfo($"[Pool] {Label}: {(first ? "new" : "field")} at {Center} {Half * 2f * 100f:F0} cm patch, {cells}/{(M - 1) * (M - 1)} cells on the floor, " +
                                   $"{_seeds.Count} source(s), {f.Sorted.Length}/{N * N} texels reachable, {f.Millis:F1} ms field + {job.Millis:F1} ms colour off-thread");
            }
        }

        /// <summary>The field's mesh (floor-following grid, cells with a vertex on the floor); returns the cells drawn.</summary>
        private int ApplyField(Field f)
        {
            using var part = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.PoolMesh);
            _field = f;
            Half = f.Half;

            var vertices = f.Vertices;
            var uvs = new Vector2[M * M];
            var colors = new Color32[M * M];
            for (int j = 0; j < M; j++)
            {
                for (int i = 0; i < M; i++)
                {
                    int k = j * M + i;
                    uvs[k] = new Vector2(i / (float)(M - 1), j / (float)(M - 1));
                    colors[k] = new Color32(255, 255, 255, 255);
                }
            }
            var tris = new List<int>((M - 1) * (M - 1) * 6);
            for (int j = 0; j < M - 1; j++)
            {
                for (int i = 0; i < M - 1; i++)
                {
                    int a = j * M + i;
                    // blood-5c: any cell with a vertex on the floor (was: all four), so the pool's edge at an obstacle follows the
                    // field's per-texel blocking instead of whole grid cells (straight, boxy lines).
                    if (!f.On[a] && !f.On[a + 1] && !f.On[a + M] && !f.On[a + M + 1]) continue;
                    tris.Add(a); tris.Add(a + 1); tris.Add(a + M);
                    tris.Add(a + 1); tris.Add(a + M + 1); tris.Add(a + M);
                }
            }
            Mesh.Clear();
            Mesh.vertices = vertices;
            Mesh.uv = uvs;
            Mesh.colors32 = colors;
            Mesh.SetTriangles(tris, 0);
            Mesh.RecalculateNormals();
            Mesh.RecalculateTangents();
            Mesh.RecalculateBounds();
            _lastCutoff = -1f;
            return tris.Count / 6;
        }

        /// <summary>
        /// Shows a finished bake: its buffers become the shown ones (the old ones take the next bake), its alpha window
        /// takes effect (the cutoff is worked out against it from this frame on), texture uploaded, sheen set.
        /// </summary>
        private void Commit(Colouring job, MaterialPropertyBlock block)
        {
            if (job.Pixels == _backPixels)
            {
                _backPixels = _pixels;
                _backAlpha = _alpha;
            }
            _pixels = job.Pixels;
            _alpha = job.Alpha;
            _window = job.Window;
            if (Texture == null)
            {
                Texture = new Texture2D(N, N, TextureFormat.RGBA32, true)
                {
                    name = "RealTargetsBloodPoolField",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            Texture.SetPixels32(_pixels);
            Texture.Apply(true, false);
            // Clotting dulls the sheen a little; the lit shader's gloss is the alpha (the deep middle shines most).
            // blood-5: on EFT's shader the copied sheen stays (its _SpecColor means something else there).
            if (block != null && Renderer != null && !LitBlood.GroundIsEft)
            {
                Renderer.GetPropertyBlock(block);
                // A tighter, a little stronger highlight than 1.1.0: a liquid surface, not a satin one.
                block.SetColor(SpecId, new Color(0.36f, 0.34f, 0.34f, 1f) * Mathf.Lerp(1f, 0.65f, job.Clot));
                block.SetFloat(ShininessId, Mathf.Lerp(0.8f, 0.6f, job.Clot));
                Renderer.SetPropertyBlock(block);
            }
        }

        /// <summary>
        /// Bakes RGB: thickness (arrival) and the tone's oxygen while fresh; clotting darkens it over ~10 min after it
        /// stopped growing; from ~15 min the thin rim and late lobes dry red-brown, inward, the middle stays wet.
        /// perf-4 (perf-3: one 5-7 ms frame of ours every few seconds with 10 pools, area Ground): the 128x128 texel loop ran
        /// on the main thread at every clotting, drying, growth or window step. Now this decides and sets the bake's state on
        /// the main thread, the texels are baked on a pool thread into the back buffers (<see cref="Colouring"/>), and Tick
        /// uploads the result when it is done (a frame or two later; the same colours as before). <paramref name="inline"/>
        /// bakes right here into new buffers (a `Decal shader` change); <paramref name="newField"/> = the bake brings
        /// <paramref name="f"/> in with it.
        /// </summary>
        private Colouring Recolour(Field f, bool force, bool newField, bool inline)
        {
            using var part = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.PoolColour);
            float still = Time.time - GrewAt;
            float clot = Mathf.Clamp01((still - 60f) / 540f);
            float dried = Mathf.Clamp01((still - 900f) / 2400f);
            // While it spreads the thickness gradient follows the edge: re-baked when the front grew 12 % (at most every 1.5 s).
            bool grew = _colourFront > 0f && Front > _colourFront * 1.12f && Time.time >= _nextColour;
            // The edge is nearing the end of the alpha window: re-bake sooner (else it would run into the 0.2..0 tail).
            bool window = _window > 0f && _window < f.Max && Front > _window * 0.8f && Time.time >= _nextWindow;
            // The meniscus (a darker, thicker rim) only once the edge is still: drawn while it moves it would lag behind it.
            bool settle = still > 4f && !_rimBaked;
            if (!force && !grew && !window && !settle && Mathf.Abs(clot - _clot) < 0.05f && Mathf.Abs(dried - _dried) < 0.04f) return null;
            _rimBaked = still > 4f;
            _nextColour = Time.time + 1.5f;
            _nextWindow = Time.time + 0.25f;
            _clot = clot;
            _dried = dried;
            Dry = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(clot * 0.4f + dried * 0.6f));

            // Before it has spread, colour for a small pool's edge (a fresh field shows nothing yet).
            float front = Mathf.Max(Front, f.FrontFor(0.02f), 1e-4f);
            _colourFront = front;
            var job = new Colouring
            {
                Field = f,
                NewField = newField,
                Front = front,
                // blood-1: the lit shader's gloss IS the alpha, and arrival / Max left most of a grown pool matte. Now the wet
                // part and a margin ahead of the edge span alpha 1..0.2 (glossy middle, a little less at the rim), the rest below.
                Window = Front >= f.Max * 0.9f ? f.Max : Mathf.Min(f.Max, Mathf.Max(front * 1.6f, f.FrontFor(0.05f))),
                Clot = clot,
                Dried = dried,
                Rim = _rimBaked,
                Oxygen = Oxygen,
                Pixels = inline ? new Color32[N * N] : _backPixels ??= new Color32[N * N],
                Alpha = inline ? new byte[N * N] : _backAlpha ??= new byte[N * N],
            };
            if (inline)
            {
                job.Run();
                return job;
            }
            _colouring = job;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => job.Run());
            return job;
        }

        /// <summary>
        /// One colour bake (perf-4): inputs copied on the main thread, Run() reads the finished field's arrays and writes only
        /// its own buffers, so it is safe on a pool thread (plain maths, no Unity objects); Done is set last.
        /// </summary>
        private sealed class Colouring
        {
            public Field Field;
            public bool NewField, Rim;
            public float Front, Window, Clot, Dried, Oxygen;
            public Color32[] Pixels;
            public byte[] Alpha;
            public double Millis;
            public string Error;
            public volatile bool Done;

            public void Run()
            {
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    Bake();
                    Millis = watch.Elapsed.TotalMilliseconds;
                }
                catch (System.Exception ex)
                {
                    Error = ex.Message;
                }
                Done = true;
            }

            private void Bake()
            {
                var f = Field;
                for (int j = 0; j < N; j++)
                {
                    for (int i = 0; i < N; i++)
                    {
                        int k = j * N + i;
                        float arrival = f.Arrival[k];
                        if (float.IsInfinity(arrival))
                        {
                            Alpha[k] = 0;
                            Pixels[k] = new Color32(0, 0, 0, 0);
                            continue;
                        }
                        // Thickness: the first-wet middle is deepest; texels past the current edge are coloured as rim.
                        float rel = Mathf.Clamp01(arrival / Front);
                        float thick = Mathf.Clamp01(1.25f * (1f - rel));
                        var c = BloodPalette.Pool(thick, Oxygen, Clot);
                        float rim = Smooth(0.65f, 1f, rel);
                        float dry = Mathf.Clamp01(Dried * 2.5f - (1f - rim) * 2.5f);
                        c = Color.Lerp(c, BloodPalette.DriedThin, dry);
                        // Meniscus: surface tension holds the edge up, a thin darker band just inside it.
                        if (Rim && rel > 0.93f && rel <= 1f) c *= Mathf.Lerp(1f, 0.72f, Smooth(0.93f, 0.985f, rel));
                        // blood-5c (an old pool read as one flat salmon sheet): clotting gathers in darker patches, and the fine
                        // mottle is stronger (was 0.93-1.07).
                        c *= Mathf.Lerp(1f, 0.7f, Clot * Smooth(0.5f, 0.75f, f.Clots[k]));
                        float mottle = 0.88f + 0.22f * f.Mottle[k];
                        Alpha[k] = AlphaOf(arrival, Window, f.Max);
                        Pixels[k] = new Color32(ToByte(c.r * mottle), ToByte(c.g * mottle), ToByte(c.b * mottle), Alpha[k]);
                    }
                }
            }
        }

        /// <summary>Alpha byte for an arrival time: 0 never wet; inside the window 1..0.2 (early..late), past it 0.2..1/255.</summary>
        /// <remarks>Static (perf-4): the colour bake calls it on a pool thread with its own window and field.</remarks>
        private static byte AlphaOf(float t, float window, float max)
        {
            if (float.IsInfinity(t)) return 0;
            return (byte)Mathf.Clamp(Mathf.RoundToInt(AlphaValue(t, window, max) * 255f), 1, 255);
        }

        private static float AlphaValue(float t, float window, float max)
        {
            float w = Mathf.Max(window, 1e-4f);
            if (t <= w) return 1f - 0.8f * t / w;
            return 0.2f * (1f - (t - w) / Mathf.Max(max - w, 1e-4f));
        }

        private float AlphaValue(float t) => AlphaValue(t, _window, _field != null ? _field.Max : Mathf.Max(_window, 1e-4f));

        /// <summary>The alpha-test cutoff that draws every texel reached by <paramref name="front"/> (above 1 = nothing).</summary>
        private float CutoffFor(float front)
        {
            if (front <= 0f || _window <= 0f) return 1.01f;
            return Mathf.Clamp((AlphaValue(front) * 255f - 0.5f) / 255f, 0f, 1.01f);
        }

        /// <summary>Fallback modes (no alpha test): the wet part fully covered, the rest clear, refreshed at most 4x a second.</summary>
        private void CpuAlpha(float cutoff)
        {
            if (_field == null || Texture == null) return;
            _nextCpuAlpha = Time.time + 0.25f;
            byte edge = (byte)Mathf.Clamp(Mathf.RoundToInt(cutoff * 255f), 0, 255);
            for (int i = 0; i < _pixels.Length; i++) _pixels[i].a = _alpha[i] > edge ? (byte)235 : (byte)0;
            Texture.SetPixels32(_pixels);
            Texture.Apply(true, false);
            for (int i = 0; i < _pixels.Length; i++) _pixels[i].a = _alpha[i];
        }

        private void Shade(MaterialPropertyBlock block)
        {
            if (Renderer == null || Texture == null) return;
            Renderer.GetPropertyBlock(block);
            block.SetTexture(MainTexId, Texture);
            // blood-5c: EftWet may take its colour from _SpecMap (LitBlood.ApplyEftOverrides).
            if (LitBlood.GroundIsEft && Plugin.GroundShader.Value == GroundShaderMode.EftWet) block.SetTexture(SpecMapId, Texture);
            block.SetColor(ColorId, LitBlood.Scaled(Color.white, LitBlood.GroundSurface));
            block.SetFloat(CutoffId, Mathf.Clamp(_lastCutoff < 0f ? 1.01f : _lastCutoff, 0f, 1.01f));
            Renderer.SetPropertyBlock(block);
        }

        /// <summary>`Decal shader` changed: the renderer's block was cleared; set texture, cutoff and sheen again.</summary>
        internal void Reshade(MaterialPropertyBlock block)
        {
            if (_field == null) return;
            bool cutout = Plugin.DecalShader.Value == DecalShaderMode.Lit && LitBlood.IsCutout;
            for (int i = 0; i < _pixels.Length; i++) _pixels[i].a = _alpha[i];
            // Rare (a setting changed): baked right here into new buffers; a bake still running elsewhere finishes as usual.
            Commit(Recolour(_field, true, false, true), block);
            if (!cutout) CpuAlpha(_lastCutoff < 0f ? 1.01f : _lastCutoff);
            Shade(block);
        }

        /// <summary>Wet (and how fresh) at <paramref name="point"/>: the field says whether the edge has passed it.</summary>
        internal float WetnessAt(Vector3 point)
        {
            if (_field == null || ShownArea < 0.002f) return 0f;
            var d = point - Center;
            if (Mathf.Abs(Vector3.Dot(d, Normal)) > 0.35f) return 0f;
            var at = Local(point);
            float x = (at.x / Half + 1f) * 0.5f * N - 0.5f, y = (at.y / Half + 1f) * 0.5f * N - 0.5f;
            int i = Mathf.RoundToInt(x), j = Mathf.RoundToInt(y);
            if (i < 0 || j < 0 || i >= N || j >= N) return 0f;
            float arrival = _field.Arrival[j * N + i];
            // Inside the edge by a little (soles don't soak at the very rim).
            if (float.IsInfinity(arrival) || arrival > Front * 0.92f) return 0f;
            return 1f - Dry / 255f * 0.85f;
        }

        internal void Destroy()
        {
            if (Go != null) Object.Destroy(Go);
            if (Mesh != null) Object.Destroy(Mesh);
            if (Texture != null) Object.Destroy(Texture);
            Go = null;
        }

        private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int SpecMapId = Shader.PropertyToID("_SpecMap");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int CutoffId = Shader.PropertyToID("_Cutoff");
        private static readonly int SpecId = Shader.PropertyToID("_SpecColor");
        private static readonly int ShininessId = Shader.PropertyToID("_Shininess");

        /// <summary>
        /// The arrival-time field of one patch. Inputs are set on the main thread (raycast heights, seeds); Run() works on
        /// plain arrays only, so it is safe on a pool thread; Done is set last.
        /// </summary>
        private sealed class Field
        {
            public int N;
            public float Half;
            public float NoiseX, NoiseY;
            public readonly List<Vector2> Seeds = new List<Vector2>();
            public readonly List<float> SeedStart = new List<float>();
            public Vector3[] Vertices;
            public float[] Height;
            public bool[] On;

            public float[] Arrival;
            public float[] Mottle;
            /// <summary>blood-5c: broad (8 per m) noise for darker clot patches as a pool clots.</summary>
            public float[] Clots;
            public float[] Sorted;
            public float Max, BorderMin, CellArea, WetCapacity;
            public double Millis;
            public string Error;
            public volatile bool Done;

            /// <summary>Slope gain: a 2 % fall (1.1 deg) makes blood run ~1.5x faster downhill, ~0.7x uphill.</summary>
            private const float SlopeGain = 20f;

            private static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1, 2, 2, -2, -2 };
            private static readonly int[] Dy = { 0, 0, 1, -1, 1, -1, 1, -1, 2, -2, 2, -2, 1, -1, 1, -1 };

            public void Run()
            {
                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    Compute();
                    Millis = watch.Elapsed.TotalMilliseconds;
                }
                catch (System.Exception ex)
                {
                    Error = ex.Message;
                }
                Done = true;
            }

            private void Compute()
            {
                int n = N, count = n * n;
                float cell = 2f * Half / n;
                CellArea = cell * cell;
                var height = new float[count];
                var blocked = new bool[count];
                var speed = new float[count];
                Mottle = new float[count];
                Clots = new float[count];
                const int m = M;
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int k = j * n + i;
                        // Texel centre in vertex-grid units.
                        float gx = (i + 0.5f) / n * (m - 1), gy = (j + 0.5f) / n * (m - 1);
                        int x0 = Mathf.Min((int)gx, m - 2), y0 = Mathf.Min((int)gy, m - 2);
                        float fx = gx - x0, fy = gy - y0;
                        int a = y0 * m + x0;
                        height[k] = Lerp(Lerp(Height[a], Height[a + 1], fx), Lerp(Height[a + m], Height[a + m + 1], fx), fy);
                        // World-space (patch-local metres + per-pool offset) noise: the same floor at any patch size.
                        float u = ((i + 0.5f) / n * 2f - 1f) * Half, v = ((j + 0.5f) / n * 2f - 1f) * Half;
                        // blood-5c: blocked per texel from the four vertices' "on the floor" interpolated, against a slightly noisy
                        // threshold (was: blocked if any vertex was off = whole grid cells, straight boxy edges at kerbs and
                        // obstacles). 0.6 keeps the open part a little short of a ledge rather than over it.
                        float open = Lerp(Lerp(On[a] ? 1f : 0f, On[a + 1] ? 1f : 0f, fx), Lerp(On[a + m] ? 1f : 0f, On[a + m + 1] ? 1f : 0f, fx), fy);
                        blocked[k] = open < 0.6f + 0.3f * (Noise.At(u * 25f - NoiseX, v * 25f + NoiseY) - 0.5f);
                        // Mostly broad lobes and tongues (1.8 and 4.5 per m), a little fine roughness; blood-5c: plus a 35 per m
                        // octave so the edge is ragged at the cm scale (HollywoodFX's splash next to ours: a broken edge reads as
                        // blood, a smooth one as a sticker).
                        float rough = 0.5f * Noise.At(u * 1.8f + NoiseY, v * 1.8f + NoiseX) + 0.27f * Noise.At(u * 4.5f + NoiseX, v * 4.5f + NoiseY) +
                                      0.13f * Noise.At(u * 12f + NoiseY, v * 12f - NoiseX) + 0.1f * Noise.At(u * 35f - NoiseY, v * 35f + NoiseX);
                        speed[k] = Mathf.Exp(2.4f * (rough - 0.5f));
                        Mottle[k] = Noise.At(u * 30f + NoiseX * 2f, v * 30f + NoiseY * 2f);
                        Clots[k] = Noise.At(u * 8f - NoiseX * 3f, v * 8f + NoiseY * 3f);
                    }
                }

                var arrival = new float[count];
                for (int k = 0; k < count; k++) arrival[k] = float.PositiveInfinity;
                var heap = new Heap(count);
                for (int s = 0; s < Seeds.Count; s++)
                {
                    int k = NearestOpen((Seeds[s].x / Half + 1f) * 0.5f * n - 0.5f, (Seeds[s].y / Half + 1f) * 0.5f * n - 0.5f, blocked, n);
                    if (k < 0) continue;
                    float t = SeedStart[s];
                    if (t < arrival[k])
                    {
                        arrival[k] = t;
                        heap.Push(t, k);
                    }
                }

                while (heap.Count > 0)
                {
                    heap.Pop(out float t, out int k);
                    if (t > arrival[k]) continue;
                    int i = k % n, j = k / n;
                    for (int e = 0; e < 16; e++)
                    {
                        int dx = Dx[e], dy = Dy[e];
                        int ni = i + dx, nj = j + dy;
                        if (ni < 0 || nj < 0 || ni >= n || nj >= n) continue;
                        int nk = nj * n + ni;
                        if (blocked[nk]) continue;
                        // Knight moves: both cells they pass beside must be open (no hopping a thin wall).
                        if (e >= 8)
                        {
                            int sx = System.Math.Sign(dx), sy = System.Math.Sign(dy);
                            if (blocked[(j + sy) * n + (i + sx)]) continue;
                            if (System.Math.Abs(dx) == 2 ? blocked[j * n + i + sx] : blocked[(j + sy) * n + i]) continue;
                        }
                        else if (e >= 4 && (blocked[j * n + ni] || blocked[nj * n + i]))
                        {
                            continue;
                        }
                        float d = cell * Mathf.Sqrt(dx * dx + dy * dy);
                        float fall = (height[k] - height[nk]) / d;
                        float slope = Mathf.Exp(Mathf.Clamp(SlopeGain * fall, -1.5f, 1.5f));
                        float nt = t + d / (0.5f * (speed[k] + speed[nk]) * slope);
                        if (nt < arrival[nk])
                        {
                            arrival[nk] = nt;
                            heap.Push(nt, nk);
                        }
                    }
                }

                // Surface tension rounds a real pool's edge: two 3x3 smoothing passes over the reached texels.
                Smooth(arrival, n);
                Smooth(arrival, n);

                int reachable = 0;
                Max = 0f;
                BorderMin = float.PositiveInfinity;
                for (int k = 0; k < count; k++)
                {
                    float t = arrival[k];
                    if (float.IsInfinity(t)) continue;
                    reachable++;
                    if (t > Max) Max = t;
                    int i = k % n, j = k / n;
                    if (i < 3 || j < 3 || i >= n - 3 || j >= n - 3) BorderMin = Mathf.Min(BorderMin, t);
                }
                Sorted = new float[reachable];
                int w = 0;
                for (int k = 0; k < count; k++)
                {
                    if (!float.IsInfinity(arrival[k])) Sorted[w++] = arrival[k];
                }
                System.Array.Sort(Sorted);
                WetCapacity = reachable * CellArea;
                if (Max <= 0f) Max = 1e-3f;
                Arrival = arrival;
            }

            /// <summary>The front (arrival time) at which <paramref name="area"/> m^2 is wet; interpolated between texels.</summary>
            public float FrontFor(float area)
            {
                if (Sorted.Length == 0) return 0f;
                float k = area / CellArea;
                if (k <= 0f) return 0f;
                if (k >= Sorted.Length - 1) return Max;
                int lo = (int)k;
                return Lerp(Sorted[lo], Sorted[lo + 1], k - lo);
            }

            private static void Smooth(float[] arrival, int n)
            {
                var copy = (float[])arrival.Clone();
                for (int j = 1; j < n - 1; j++)
                {
                    for (int i = 1; i < n - 1; i++)
                    {
                        int k = j * n + i;
                        if (float.IsInfinity(copy[k])) continue;
                        float sum = 0f;
                        int c = 0;
                        for (int dj = -1; dj <= 1; dj++)
                        {
                            for (int di = -1; di <= 1; di++)
                            {
                                float t = copy[k + dj * n + di];
                                if (float.IsInfinity(t)) continue;
                                sum += t;
                                c++;
                            }
                        }
                        arrival[k] = sum / c;
                    }
                }
            }

            private static int NearestOpen(float x, float y, bool[] blocked, int n)
            {
                int ci = Mathf.Clamp(Mathf.RoundToInt(x), 0, n - 1), cj = Mathf.Clamp(Mathf.RoundToInt(y), 0, n - 1);
                for (int r = 0; r <= 4; r++)
                {
                    for (int dj = -r; dj <= r; dj++)
                    {
                        for (int di = -r; di <= r; di++)
                        {
                            if (System.Math.Max(System.Math.Abs(di), System.Math.Abs(dj)) != r) continue;
                            int i = ci + di, j = cj + dj;
                            if (i < 0 || j < 0 || i >= n || j >= n || blocked[j * n + i]) continue;
                            return j * n + i;
                        }
                    }
                }
                return -1;
            }

            private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        }

        /// <summary>Binary min-heap of (time, texel), lazy deletion (stale entries skipped on pop).</summary>
        private sealed class Heap
        {
            private float[] _keys;
            private int[] _values;
            public int Count;

            public Heap(int capacity)
            {
                _keys = new float[capacity];
                _values = new int[capacity];
            }

            public void Push(float key, int value)
            {
                if (Count == _keys.Length)
                {
                    System.Array.Resize(ref _keys, Count * 2);
                    System.Array.Resize(ref _values, Count * 2);
                }
                int i = Count++;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (_keys[parent] <= key) break;
                    _keys[i] = _keys[parent];
                    _values[i] = _values[parent];
                    i = parent;
                }
                _keys[i] = key;
                _values[i] = value;
            }

            public void Pop(out float key, out int value)
            {
                key = _keys[0];
                value = _values[0];
                float lastKey = _keys[--Count];
                int lastValue = _values[Count];
                int i = 0;
                while (true)
                {
                    int child = 2 * i + 1;
                    if (child >= Count) break;
                    if (child + 1 < Count && _keys[child + 1] < _keys[child]) child++;
                    if (_keys[child] >= lastKey) break;
                    _keys[i] = _keys[child];
                    _values[i] = _values[child];
                    i = child;
                }
                _keys[i] = lastKey;
                _values[i] = lastValue;
            }
        }

        /// <summary>Managed 2D gradient noise, 0..1 (Unity's PerlinNoise is not for worker threads).</summary>
        private static class Noise
        {
            private static readonly int[] Perm = BuildPerm();

            private static int[] BuildPerm()
            {
                var p = new int[512];
                var rng = new System.Random(4242);
                var basis = new int[256];
                for (int i = 0; i < 256; i++) basis[i] = i;
                for (int i = 255; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (basis[i], basis[j]) = (basis[j], basis[i]);
                }
                for (int i = 0; i < 512; i++) p[i] = basis[i & 255];
                return p;
            }

            public static float At(float x, float y)
            {
                int xi = (int)System.Math.Floor(x), yi = (int)System.Math.Floor(y);
                float xf = x - xi, yf = y - yi;
                int X = xi & 255, Y = yi & 255;
                float u = Fade(xf), v = Fade(yf);
                int aa = Perm[Perm[X] + Y], ab = Perm[Perm[X] + Y + 1], ba = Perm[Perm[X + 1] + Y], bb = Perm[Perm[X + 1] + Y + 1];
                float x1 = L(Grad(aa, xf, yf), Grad(ba, xf - 1f, yf), u);
                float x2 = L(Grad(ab, xf, yf - 1f), Grad(bb, xf - 1f, yf - 1f), u);
                // Gradient noise is about -0.7..0.7: map to 0..1.
                return System.Math.Max(0f, System.Math.Min(1f, 0.5f + 0.7f * L(x1, x2, v)));
            }

            private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
            private static float L(float a, float b, float t) => a + (b - a) * t;

            private static float Grad(int hash, float x, float y)
            {
                switch (hash & 7)
                {
                    case 0: return x + y;
                    case 1: return -x + y;
                    case 2: return x - y;
                    case 3: return -x - y;
                    case 4: return x;
                    case 5: return -x;
                    case 6: return y;
                    default: return -y;
                }
            }
        }
    }
}
