using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// How wounds, ground blood and gore are drawn. Lit (0.10.3 default): a multiply pass (darkens what
    /// is under it) plus a colour pass with a real lit shader (LitBlood), so the map's lights and EFT's
    /// exposure decide the brightness. Hybrid (0.9.3-0.10.2): the colour pass is unlit, tinted by our
    /// own light estimate; it glowed in dark rooms (0.10.2 test). Multiply: darken only.
    /// AlphaBlended: unlit colour pass only.
    /// </summary>
    internal enum DecalShaderMode { Lit, Hybrid, Multiply, AlphaBlended }

    /// <summary>
    /// Our own blood on the ground and walls (0.9.1). Until 0.9.0 drips, splats and pools went
    /// through EFT's DeferredDecalRenderer, whose static decals share ONE ring of _maxDecals slots
    /// (500 by default) across every decal type: at our rates the ring wrapped within a minute, so
    /// old blood vanished while new blood landed on it (Stompa: "flickering"), and we were also
    /// overwriting EFT's bullet holes. Now:
    /// - marks (drips and splats where droplets land) are quads in a few shared meshes, kept for
    ///   the raid (ring of 4096; oldest reused only past that). A drip landing on an existing drip
    ///   grows that one instead of stacking a new quad, so a bleeding body makes one spreading spot;
    /// - pools are one patch per wound: a 9x9 grid laid on the floor by raycasts (follows steps and
    ///   rubble, stops at edges and obstacles), regrown as the wound's drained volume rises. If the
    ///   body is moved away the old pool stays and a new one starts.
    /// Drawn with a multiply shader: the blood darkens and tints whatever is under it as the map lit
    /// it, so it never glows in the dark (as alpha-blended lit decals did).
    /// </summary>
    internal sealed class GroundBlood : MonoBehaviour
    {
        private const int QuadsPerChunk = 512;
        /// <summary>Marks are grouped by 6 m cells (0.10.3): a lit chunk takes its light probe at its own bounds.</summary>
        private const float ChunkCell = 6f;
        private const int MaxChunks = 48;
        private const float Cell = 0.035f;
        private const int MaxPools = 96;
        private const int PoolGrid = 9;
        private const int PoolVariants = 4;
        private const float Lift = 0.008f;
        private const float PoolLift = 0.006f;

        internal static GroundBlood Instance { get; private set; }
        internal int ChunkCount => _chunks.Count;
        internal int PoolCount => _allPools.Count;
        internal int MarkCount
        {
            get
            {
                int n = 0;
                foreach (var c in _chunks.Values) n += c.Count;
                return n;
            }
        }
        internal static string ShaderName { get; private set; } = "-";

        private struct Mark
        {
            public Vector3 Point, Normal, Axis;
            public float Size, Stretch;
            public int Atlas;
            public bool Mirror;
            public long Key;
            public byte Alpha;
            /// <summary>Light level where it landed (vertex RGB): scales the colour pass, never glows in the dark.</summary>
            public byte Light;
        }

        private sealed class Chunk
        {
            public GameObject Go;
            public Mesh Mesh;
            public long CellKey;
            public Vector3 Centre;
            public Mark[] Marks = new Mark[QuadsPerChunk];
            /// <summary>Quads used; past QuadsPerChunk the oldest in this cell is reused (ring at Next).</summary>
            public int Count, Next;
            public float LastUse;
            public Vector3[] Vertices = new Vector3[QuadsPerChunk * 4];
            public Vector2[] Uvs = new Vector2[QuadsPerChunk * 4];
            public Color32[] Colors = new Color32[QuadsPerChunk * 4];
            // 0.10.8: lit shaders need them (0.10.3-0.10.7 had none: zero normal = no diffuse light = near-black marks).
            public Vector3[] Normals = new Vector3[QuadsPerChunk * 4];
            public Vector4[] Tangents = new Vector4[QuadsPerChunk * 4];
            public bool Dirty;
        }

        private sealed class PoolPatch
        {
            public GameObject Go;
            public Mesh Mesh;
            public Vector3 Center, Normal, Axis;
            public float Radius, Built;
            /// <summary>Last time it grew: drying (darkening) starts a while after.</summary>
            public float GrewAt;
            public byte Dry;
            public byte Light;
            public int Variant;
            public string Label;
            public Color32[] Colors;
        }

        private readonly Dictionary<long, Chunk> _chunks = new Dictionary<long, Chunk>();
        private readonly Dictionary<long, (Chunk chunk, int slot)> _byCell = new Dictionary<long, (Chunk, int)>();
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();

        private readonly Dictionary<object, PoolPatch> _pools = new Dictionary<object, PoolPatch>();
        private float _nextDry;
        private readonly Queue<PoolPatch> _allPools = new Queue<PoolPatch>();

        // Multiply (darken) and colour passes: lit (Lit mode, 0.10.3) or unlit (Hybrid). One pool
        // material per texture variant (irregular outlines).
        private Material _markMaterial, _markColour, _markLit;
        private readonly Material[] _poolMaterial = new Material[PoolVariants], _poolColour = new Material[PoolVariants], _poolLit = new Material[PoolVariants];
        private int[] _chunkTriangles;
        private bool _failed;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            ResetRaid();
        }

        internal void ResetRaid()
        {
            MovableCache.Clear();
            foreach (var chunk in _chunks.Values) DestroyChunk(chunk);
            _chunks.Clear();
            _byCell.Clear();

            while (_allPools.Count > 0) DestroyPool(_allPools.Dequeue());
            _pools.Clear();
        }

        private void LateUpdate()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Ground);
            if (Time.time >= _nextDry)
            {
                _nextDry = Time.time + 4f;
                DryPools();
            }

            foreach (var chunk in _chunks.Values)
            {
                if (chunk == null || !chunk.Dirty || chunk.Mesh == null) continue;
                chunk.Dirty = false;
                chunk.Mesh.vertices = chunk.Vertices;
                chunk.Mesh.uv = chunk.Uvs;
                chunk.Mesh.colors32 = chunk.Colors;
                chunk.Mesh.normals = chunk.Normals;
                chunk.Mesh.tangents = chunk.Tangents;
                // Fixed cell bounds: unused quads sit at the origin, and the probe is sampled at the centre.
                chunk.Mesh.bounds = new Bounds(chunk.Centre, Vector3.one * (ChunkCell + 1f));
            }
        }

        // ---- Marks -------------------------------------------------------------------------------

        /// <summary>A drop landing slowly on a floor: a round drip mark, merged with one already there.</summary>
        internal void Drip(Vector3 point, Vector3 normal)
        {
            if (!Ready()) return;

            long key = Key(point);
            if (_byCell.TryGetValue(key, out var at) && at.chunk.Go != null && at.chunk.Marks[at.slot].Key == key)
            {
                // Blood keeps landing on the same spot: the spot spreads and darkens.
                ref var m = ref at.chunk.Marks[at.slot];
                m.Size = Mathf.Min(m.Size * 1.07f + 0.0015f, 0.11f);
                m.Alpha = (byte)Mathf.Min(255, m.Alpha + 12);
                Write(at.chunk, at.slot);
                return;
            }

            Add(new Mark
            {
                Point = point,
                Normal = normal.normalized,
                Axis = RandomTangent(normal),
                Size = Random.Range(0.018f, 0.04f),
                Stretch = 1f,
                Atlas = Random.value < 0.5f ? 0 : 1,
                Key = key,
                Alpha = (byte)Random.Range(170, 225),
                Light = LightAt(point, normal),
            });
        }

        /// <summary>A droplet arriving with sideways speed (spurt) or hitting a wall: a splat stretched along its travel.</summary>
        internal void Splat(Vector3 point, Vector3 normal, Vector3 velocity)
        {
            if (!Ready()) return;

            normal = normal.normalized;
            var along = Vector3.ProjectOnPlane(velocity, normal);
            float speed = along.magnitude;
            Add(new Mark
            {
                Point = point,
                Normal = normal,
                Axis = speed > 0.3f ? along / speed : RandomTangent(normal),
                Size = Random.Range(0.025f, 0.055f),
                Stretch = Mathf.Clamp(1f + speed * 0.35f, 1f, 2.8f),
                Atlas = speed > 0.3f ? 2 : 3,
                Key = long.MinValue,
                Alpha = (byte)Random.Range(180, 240),
                Light = LightAt(point, normal),
            });
        }

        /// <summary>
        /// A splash where an exit spray meets a surface (0.10.0): <paramref name="count"/> splats in a
        /// disc of <paramref name="radius"/>, dense in the middle and thinning out, smaller at the rim;
        /// each stretched along the spray's travel over the surface (round on a square-on wall).
        /// </summary>
        internal void Spatter(Vector3 point, Vector3 normal, Vector3 velocity, float radius, int count)
        {
            if (!Ready()) return;

            normal = normal.normalized;
            var t = RandomTangent(normal);
            var b = Vector3.Cross(normal, t);
            var along = Vector3.ProjectOnPlane(velocity, normal);
            float slide = along.magnitude;
            byte light = LightAt(point, normal);
            for (int i = 0; i < count; i++)
            {
                // Square of a uniform number: most drops near the centre.
                float r = radius * Random.value * Random.value * 1.4f;
                float a = Random.Range(0f, Mathf.PI * 2f);
                var offset = t * (Mathf.Cos(a) * r) + b * (Mathf.Sin(a) * r);
                // Drops thrown sideways off the cone's axis arrive moving outward too.
                var travel = along + offset.normalized * (velocity.magnitude * (r / Mathf.Max(radius, 0.01f)) * 0.5f);
                float speed = travel.magnitude;
                float rim = r / Mathf.Max(radius, 0.01f);
                Add(new Mark
                {
                    Point = point + offset,
                    Normal = normal,
                    Axis = speed > 0.3f ? travel / speed : RandomTangent(normal),
                    Size = Random.Range(0.012f, 0.045f) * Mathf.Lerp(1.2f, 0.45f, Mathf.Clamp01(rim)),
                    Stretch = Mathf.Clamp(1f + speed * 0.25f, 1f, 3f),
                    Atlas = speed > 0.3f ? 2 : 3,
                    Key = long.MinValue,
                    Alpha = (byte)Random.Range(170, 240),
                    Light = light,
                });
            }
            if (slide < 0.5f && count >= 6)
            {
                // Square-on: one bigger central blot where the bulk hit.
                Add(new Mark
                {
                    Point = point,
                    Normal = normal,
                    Axis = RandomTangent(normal),
                    Size = Mathf.Min(0.09f, radius * 0.9f),
                    Stretch = 1f,
                    Atlas = 3,
                    Key = long.MinValue,
                    Alpha = 230,
                    Light = light,
                });
            }
        }

        /// <summary>
        /// A bloody boot print (0.10.6): toe along <paramref name="forward"/>, <paramref name="wetness"/> 1 (fresh
        /// from a pool) to 0 (dry) picks the wet/drying/faint print; <paramref name="left"/> mirrors it.
        /// </summary>
        internal void Footprint(Vector3 point, Vector3 normal, Vector3 forward, bool left, float wetness, bool slip = false)
        {
            if (!Ready()) return;
            normal = normal.normalized;
            var axis = Vector3.ProjectOnPlane(forward, normal);
            if (axis.sqrMagnitude < 1e-4f) return;
            // Feet don't land in a perfect line: toes out a little, each print a bit different (0.10.7).
            axis = Quaternion.AngleAxis(Random.Range(-7f, 7f) + (left ? -5f : 5f), normal) * axis.normalized;
            Add(new Mark
            {
                Point = point + axis * Random.Range(-0.03f, 0.03f),
                Normal = normal,
                Axis = axis,
                Size = Random.Range(0.095f, 0.112f),
                // The first step out of a pool slides a little: a longer, smeared print.
                Stretch = slip ? Random.Range(3.1f, 3.6f) : Random.Range(2.6f, 2.9f),
                Atlas = wetness > 0.55f ? 4 : wetness > 0.25f ? 5 : 6,
                Mirror = left,
                Key = long.MinValue,
                Alpha = (byte)Mathf.RoundToInt(Mathf.Lerp(150f, 235f, Mathf.Clamp01(wetness))),
                Light = LightAt(point, normal),
            });
        }

        /// <summary>
        /// One step of a drag streak (DragSmears): <paramref name="width"/> across, twice that along <paramref name="along"/>,
        /// centred on <paramref name="point"/>; wet (cell 8) or drying (cell 9) by <paramref name="wetness"/>, which also sets
        /// its alpha (thinner under the lit pass's cutout as the cloth runs dry).
        /// </summary>
        internal void DragStreak(Vector3 point, Vector3 normal, Vector3 along, float width, float wetness)
        {
            if (!Ready()) return;
            normal = normal.normalized;
            var axis = Vector3.ProjectOnPlane(along, normal);
            if (axis.sqrMagnitude < 1e-6f) return;
            wetness = Mathf.Clamp01(wetness);
            Add(new Mark
            {
                Point = point,
                Normal = normal,
                Axis = axis.normalized,
                Size = width,
                Stretch = 2f,
                Atlas = wetness > 0.45f ? 8 : 9,
                // Mirrored across at random: the same cell every half length would show its pattern repeating.
                Mirror = Random.value < 0.5f,
                Key = long.MinValue,
                Alpha = (byte)Mathf.RoundToInt(Mathf.Lerp(120f, 235f, wetness) * Random.Range(0.92f, 1f)),
                Light = LightAt(point, normal),
            });
        }

        /// <summary>One step of a smear left by something sliding along <paramref name="along"/> (a gib down a wall).</summary>
        internal void Smear(Vector3 point, Vector3 normal, Vector3 along, float width, float wetness = 1f)
        {
            if (!Ready()) return;
            normal = normal.normalized;
            var axis = Vector3.ProjectOnPlane(along, normal);
            if (axis.sqrMagnitude < 1e-4f) axis = RandomTangent(normal);
            Add(new Mark
            {
                Point = point,
                Normal = normal,
                Axis = axis.normalized,
                Size = width,
                Stretch = 1.6f,
                Atlas = 7,
                Key = long.MinValue,
                Alpha = (byte)Mathf.RoundToInt(Random.Range(170f, 230f) * Mathf.Lerp(0.45f, 1f, Mathf.Clamp01(wetness))),
                Light = LightAt(point, normal),
            });
        }

        /// <summary>
        /// The wettest pool <paramref name="point"/> stands in (within 90 % of its radius, about the same height):
        /// 1 = fresh, falling as it dries; 0 = none (or dried out).
        /// </summary>
        internal float PoolWetness(Vector3 point)
        {
            float best = 0f;
            foreach (var pool in _allPools)
            {
                if (pool?.Go == null || pool.Radius < 0.08f || pool.Dry > 230) continue;
                var d = point - pool.Center;
                if (Mathf.Abs(d.y) > 0.35f) continue;
                d.y = 0f;
                if (d.sqrMagnitude > pool.Radius * pool.Radius * 0.81f) continue;
                best = Mathf.Max(best, 1f - pool.Dry / 255f * 0.85f);
            }
            return best;
        }

        /// <summary>Vertex light for the unlit (Hybrid/AlphaBlended) colour pass; unused by Lit.</summary>
        private static byte LightAt(Vector3 point, Vector3 normal) =>
            (byte)Mathf.RoundToInt(255f * SceneLight.Unlit(SceneLight.At(point, normal)));

        /// <summary>
        /// F3 menu "Blood render test" (0.10.2): a 24-drop splash where the camera looks (if a surface is
        /// within 30 m), a 50 cm pool on the floor 1.5 m ahead, and a mist cloud 2 m ahead. Shows at once
        /// whether ground blood and mist render at all, independent of any bleeding.
        /// </summary>
        internal string RenderTest(Transform camera)
        {
            if (!Ready()) return "ground blood not ready (no shader)";
            var parts = new List<string>();
            var fwd = camera.forward;
            if (Physics.Raycast(camera.position, fwd, out var wall, 30f, LayersMaskController.HighPolyWithTerrainMask))
            {
                Spatter(wall.point, wall.normal, fwd * 6f, 0.25f, 24);
                parts.Add($"splash {wall.distance:F1} m ahead on {wall.collider.name}");
            }
            else
            {
                parts.Add("no surface ahead for the splash");
            }

            var flat = Vector3.ProjectOnPlane(fwd, Vector3.up);
            var spot = camera.position + (flat.sqrMagnitude > 0.01f ? flat.normalized : Vector3.forward) * 1.5f;
            int before = _allPools.Count;
            Pool(new object(), "render test", spot, 0.5f);
            parts.Add(_allPools.Count > before ? "pool 50 cm, 1.5 m ahead" : "no floor found for the pool");

            // 0.10.3: gore pieces (stump cap, gibs) 1 m to the right of the pool, to judge their lit look.
            var side = spot + Vector3.ProjectOnPlane(camera.right, Vector3.up).normalized * 1f;
            if (Dismemberment.Instance != null && Physics.Raycast(side + Vector3.up * 0.5f, Vector3.down, out var floor, 3f, LayersMaskController.HighPolyWithTerrainMask))
            {
                parts.Add(Dismemberment.Instance.RenderTest(floor.point));
            }

            BloodMist.Instance?.Puff(camera.position + fwd * 2f, camera.right, 0.25f, 0.9f, 15);
            parts.Add("mist 2 m ahead");
            string result = string.Join(", ", parts);
            Plugin.Log.LogInfo($"[Blood test] {result}; ground {ShaderName}, mode {Plugin.DecalShader.Value}, {SoftParticleState()}");
            return result;
        }

        private void Add(Mark mark)
        {
            var chunk = GetChunk(mark.Point);
            if (chunk == null) return;
            int slot;
            if (chunk.Count < QuadsPerChunk)
            {
                slot = chunk.Count++;
            }
            else
            {
                slot = chunk.Next;
                chunk.Next = (chunk.Next + 1) % QuadsPerChunk;
                var old = chunk.Marks[slot];
                if (old.Key != long.MinValue && _byCell.TryGetValue(old.Key, out var at) && at.chunk == chunk && at.slot == slot) _byCell.Remove(old.Key);
            }

            chunk.Marks[slot] = mark;
            if (mark.Key != long.MinValue) _byCell[mark.Key] = (chunk, slot);
            Write(chunk, slot);
        }

        private void Write(Chunk chunk, int slot)
        {
            int v = slot * 4;
            var m = chunk.Marks[slot];
            chunk.LastUse = Time.time;

            var n = m.Normal;
            var t = m.Axis * (m.Size * m.Stretch * 0.5f);
            var b = Vector3.Cross(n, m.Axis).normalized * (m.Size * 0.5f);
            // Marks above pools, each a hair apart (0.10.3: the lit cutout pass is depth-tested, no z-fighting).
            var c = m.Point + n * (Lift + (slot % 7) * 0.00015f);
            // Splats start a little behind the impact and run ahead of it along the travel.
            if (m.Atlas == 2) c += m.Axis * (m.Size * (m.Stretch - 1f) * 0.35f);

            chunk.Vertices[v] = c - t - b;
            chunk.Vertices[v + 1] = c + t - b;
            chunk.Vertices[v + 2] = c + t + b;
            chunk.Vertices[v + 3] = c - t + b;

            // 4x4 atlas (gore-5; 4x2 since 0.10.6): row 0 drips/splats, row 1 footprints (wet, medium, faint) and the smear,
            // row 2 drag streaks (wet, drying); row 3 free.
            float u0 = (m.Atlas % 4) * 0.25f + 0.002f, v0 = (m.Atlas / 4) * 0.25f + 0.002f, su = 0.246f, sv = 0.246f;
            // A left foot is the right foot's print mirrored across its length.
            float va = m.Mirror ? v0 + sv : v0, vb = m.Mirror ? v0 : v0 + sv;
            chunk.Uvs[v] = new Vector2(u0, va);
            chunk.Uvs[v + 1] = new Vector2(u0 + su, va);
            chunk.Uvs[v + 2] = new Vector2(u0 + su, vb);
            chunk.Uvs[v + 3] = new Vector2(u0, vb);

            var col = new Color32(m.Light, m.Light, m.Light, m.Alpha);
            chunk.Colors[v] = chunk.Colors[v + 1] = chunk.Colors[v + 2] = chunk.Colors[v + 3] = col;
            var tangent = new Vector4(m.Axis.x, m.Axis.y, m.Axis.z, m.Mirror ? -1f : 1f);
            for (int k = 0; k < 4; k++)
            {
                chunk.Normals[v + k] = n;
                chunk.Tangents[v + k] = tangent;
            }
            chunk.Dirty = true;
        }

        /// <summary>The chunk for the 6 m cell around <paramref name="point"/>; past MaxChunks the least recently used cell is cleared.</summary>
        private Chunk GetChunk(Vector3 point)
        {
            long key = ChunkKey(point);
            if (_chunks.TryGetValue(key, out var chunk) && chunk.Go != null) return chunk;

            if (_chunks.Count >= MaxChunks)
            {
                Chunk oldest = null;
                foreach (var c in _chunks.Values)
                {
                    if (oldest == null || c.LastUse < oldest.LastUse) oldest = c;
                }
                if (oldest != null)
                {
                    _chunks.Remove(oldest.CellKey);
                    DestroyChunk(oldest);
                }
            }

            chunk = new Chunk
            {
                CellKey = key,
                Centre = new Vector3((Mathf.Floor(point.x / ChunkCell) + 0.5f) * ChunkCell, (Mathf.Floor(point.y / ChunkCell) + 0.5f) * ChunkCell,
                    (Mathf.Floor(point.z / ChunkCell) + 0.5f) * ChunkCell),
                LastUse = Time.time,
            };
            chunk.Mesh = new Mesh { name = "RealTargetsBloodMarks", hideFlags = HideFlags.HideAndDontSave };
            chunk.Mesh.MarkDynamic();
            chunk.Mesh.vertices = chunk.Vertices;
            chunk.Mesh.uv = chunk.Uvs;
            chunk.Mesh.colors32 = chunk.Colors;
            chunk.Mesh.normals = chunk.Normals;
            chunk.Mesh.tangents = chunk.Tangents;
            chunk.Mesh.triangles = _chunkTriangles;
            chunk.Mesh.bounds = new Bounds(chunk.Centre, Vector3.one * (ChunkCell + 1f));
            chunk.Go = new GameObject("RealTargetsBloodMarks");
            DontDestroyOnLoad(chunk.Go);
            chunk.Go.AddComponent<MeshFilter>().sharedMesh = chunk.Mesh;
            var renderer = chunk.Go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = Materials(false, 0);
            Shade(renderer);
            _chunks[key] = chunk;
            return chunk;
        }

        private void DestroyChunk(Chunk chunk)
        {
            if (chunk == null) return;
            for (int i = 0; i < chunk.Count; i++)
            {
                var k = chunk.Marks[i].Key;
                if (k != long.MinValue && _byCell.TryGetValue(k, out var at) && at.chunk == chunk) _byCell.Remove(k);
            }
            if (chunk.Go != null) Destroy(chunk.Go);
            if (chunk.Mesh != null) Destroy(chunk.Mesh);
            chunk.Go = null;
        }

        private static long ChunkKey(Vector3 p)
        {
            long x = Mathf.FloorToInt(p.x / ChunkCell) & 0x1FFFFF;
            long y = Mathf.FloorToInt(p.y / ChunkCell) & 0x1FFFFF;
            long z = Mathf.FloorToInt(p.z / ChunkCell) & 0x1FFFFF;
            return (x << 42) | (y << 21) | z;
        }

        /// <summary>Lit mode: lit by nearby probes; unlit modes: no probes (the colour comes from the vertex light).</summary>
        private static void Shade(Renderer renderer)
        {
            if (Plugin.DecalShader.Value == DecalShaderMode.Lit && LitBlood.HasTransparent)
            {
                LitBlood.Setup(renderer);
                return;
            }
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private static long Key(Vector3 p)
        {
            long x = Mathf.FloorToInt(p.x / Cell) & 0x1FFFFF;
            long y = Mathf.FloorToInt(p.y / Cell) & 0x1FFFFF;
            long z = Mathf.FloorToInt(p.z / Cell) & 0x1FFFFF;
            return (x << 42) | (y << 21) | z;
        }

        private static Vector3 RandomTangent(Vector3 normal)
        {
            var t = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
            return Quaternion.AngleAxis(Random.Range(0f, 360f), normal) * t;
        }

        // ---- Pools -------------------------------------------------------------------------------

        /// <summary>Grows the wound's pool under <paramref name="site"/> to the given radius (m).</summary>
        internal void Pool(object wound, string label, Vector3 site, float radius)
        {
            if (!Ready()) return;

            if (_pools.TryGetValue(wound, out var pool) && pool.Go != null)
            {
                var off = Vector3.ProjectOnPlane(site - pool.Center, Vector3.up);
                if (off.magnitude > 0.35f + pool.Radius * 0.5f)
                {
                    pool = null; // body moved away: the old pool stays, a new one starts here
                }
            }
            else
            {
                pool = null;
            }

            if (pool == null)
            {
                if (!Physics.Raycast(site + Vector3.up * 0.4f, Vector3.down, out var ground, 2.5f, LayersMaskController.HighPolyWithTerrainMask) ||
                    Movable(ground.collider))
                {
                    if (Plugin.LogBlood.Value) Plugin.Log.LogInfo($"[Pool] {label}: no floor under {site}");
                    _pools[wound] = new PoolPatch { Center = site }; // don't retry every update
                    return;
                }
                pool = new PoolPatch
                {
                    Center = ground.point,
                    Normal = ground.normal.y > 0.5f ? ground.normal.normalized : Vector3.up,
                    Radius = 0f,
                    Built = 0f,
                    Label = label,
                    Variant = Random.Range(0, PoolVariants),
                };
                pool.Light = LightAt(ground.point, pool.Normal);
                pool.Axis = RandomTangent(pool.Normal);
                pool.Mesh = new Mesh { name = "RealTargetsBloodPool", hideFlags = HideFlags.HideAndDontSave };
                pool.Mesh.MarkDynamic();
                pool.Go = new GameObject("RealTargetsBloodPool");
                pool.Go.AddComponent<MeshFilter>().sharedMesh = pool.Mesh;
                var renderer = pool.Go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = Materials(true, pool.Variant);
                Shade(renderer);
                _pools[wound] = pool;
                _allPools.Enqueue(pool);
                while (_allPools.Count > MaxPools) DestroyPool(_allPools.Dequeue());
            }

            if (pool.Mesh == null) return;
            if (radius > pool.Radius + 0.002f) pool.GrewAt = Time.time;
            pool.Radius = Mathf.Max(pool.Radius, radius);
            if (pool.Radius - pool.Built >= 0.008f || pool.Built == 0f)
            {
                Build(pool);
            }
        }

        /// <summary>
        /// Lays the pool's 9x9 grid on the floor. A grid cell with a corner off the floor (ledge, rubble,
        /// nothing below) is left out of the mesh (0.10.3): the lit colour pass ignores vertex alpha, so a
        /// faded-out corner would otherwise float.
        /// </summary>
        private void Build(PoolPatch pool)
        {
            int n = PoolGrid;
            var vertices = new Vector3[n * n];
            var uvs = new Vector2[n * n];
            var colors = new Color32[n * n];
            var on = new bool[n * n];
            var up = pool.Normal;
            var t = pool.Axis;
            var b = Vector3.Cross(up, t).normalized;
            // The texture's blob fills ~80 % of the patch, so the patch is a bit larger than the pool.
            float half = pool.Radius * 1.25f;
            int visible = 0;

            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    float u = i / (float)(n - 1), w = j / (float)(n - 1);
                    var flat = pool.Center + t * ((u - 0.5f) * 2f * half) + b * ((w - 0.5f) * 2f * half);
                    int k = j * n + i;
                    uvs[k] = new Vector2(u, w);
                    byte alpha = 255;
                    var p = flat + up * PoolLift;
                    if (Physics.Raycast(flat + Vector3.up * 0.2f, Vector3.down, out var hit, 0.45f, LayersMaskController.HighPolyWithTerrainMask))
                    {
                        float rise = Vector3.Dot(hit.point - pool.Center, up);
                        // Blood doesn't climb onto rubble or run down past a ledge: fade out there.
                        if (rise > 0.06f || rise < -0.12f) alpha = 0;
                        p = hit.point + hit.normal * PoolLift;
                    }
                    else
                    {
                        alpha = 0;
                    }
                    vertices[k] = p;
                    on[k] = alpha > 0;
                    if (alpha > 0) visible++;
                    colors[k] = new Color32(Scale(DryRed(pool.Dry), pool.Light), pool.Light, pool.Light, alpha);
                }
            }

            var tris = new List<int>((n - 1) * (n - 1) * 6);
            for (int j = 0; j < n - 1; j++)
            {
                for (int i = 0; i < n - 1; i++)
                {
                    int a = j * n + i;
                    if (!on[a] || !on[a + 1] || !on[a + n] || !on[a + n + 1]) continue;
                    tris.Add(a); tris.Add(a + 1); tris.Add(a + n);
                    tris.Add(a + 1); tris.Add(a + n + 1); tris.Add(a + n);
                }
            }

            pool.Mesh.Clear();
            pool.Mesh.vertices = vertices;
            pool.Mesh.uv = uvs;
            pool.Mesh.colors32 = colors;
            pool.Colors = colors;
            pool.Mesh.SetTriangles(tris, 0);
            pool.Mesh.RecalculateNormals();
            pool.Mesh.RecalculateTangents();
            pool.Mesh.RecalculateBounds();
            if (pool.Built == 0f)
            {
                ShadePool(pool);
                if (Plugin.LogBlood.Value)
                {
                    Plugin.Log.LogInfo($"[Pool] {pool.Label}: new at {pool.Center} r {pool.Radius * 100f:F0} cm, {visible}/{n * n} grid points on the floor, " +
                                       $"{Plugin.DecalShader.Value}{(Plugin.DecalShader.Value == DecalShaderMode.Lit ? "" : $" light {pool.Light}")}, outline {pool.Variant}");
                }
            }
            pool.Built = pool.Radius;
        }

        private static byte Scale(byte value, byte light) => (byte)(value * light / 255);

        /// <summary>
        /// Blood dries from the edge in: ~30 s after a pool stops growing it starts to darken and
        /// loses its red over ~5 min (the multiply factor's red channel falls; the lit colour darkens).
        /// </summary>
        private void DryPools()
        {
            float now = Time.time;
            foreach (var pool in _allPools)
            {
                if (pool?.Mesh == null || pool.Colors == null) continue;
                byte dry = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01((now - pool.GrewAt - 30f) / 300f));
                // Fresh blood on top (it grew again) makes it fresh again.
                if (dry == pool.Dry || (Mathf.Abs(dry - pool.Dry) < 6 && dry != 0 && dry != 255)) continue;
                pool.Dry = dry;
                byte red = Scale(DryRed(dry), pool.Light);
                for (int i = 0; i < pool.Colors.Length; i++) pool.Colors[i].r = red;
                pool.Mesh.colors32 = pool.Colors;
                ShadePool(pool);
            }
        }

        /// <summary>Multiply factor's red channel: fresh 255, dried ~140 (darker, browner).</summary>
        private static byte DryRed(byte dry) => (byte)(255 - dry * 115 / 255);

        /// <summary>Lit colour pass: fresh blood dark red and glossy; dried ~45 % darker, browner (per pool, property block).</summary>
        private void ShadePool(PoolPatch pool)
        {
            var r = pool.Go != null ? pool.Go.GetComponent<Renderer>() : null;
            if (r == null || Plugin.DecalShader.Value != DecalShaderMode.Lit || _poolLit[pool.Variant] == null) return;
            float d = pool.Dry / 255f;
            var c = Color.Lerp(PoolFresh, PoolDried, d);
            LitBlood.SetColor(r, r.sharedMaterials.Length - 1, c, _block);
        }

        private static readonly Color MarkTint = new Color(1f, 1f, 1f, 0.9f);
        // 0.10.6: ~10 % darker (0.10.5 test, Stompa: "maybe a tiny bit too bright" under the helmet light).
        private static readonly Color PoolFresh = new Color(0.88f, 0.88f, 0.88f, 0.97f);
        private static readonly Color PoolDried = new Color(0.5f, 0.4f, 0.36f, 0.97f);

        private static void DestroyPool(PoolPatch pool)
        {
            if (pool == null) return;
            if (pool.Go != null) Destroy(pool.Go);
            if (pool.Mesh != null) Destroy(pool.Mesh);
        }

        // ---- Resources ---------------------------------------------------------------------------

        /// <summary>Builds the ground blood textures and materials while the raid loads (0.10.10).</summary>
        internal void Warm() => Ready();

        private bool Ready()
        {
            if (_markMaterial != null || _markColour != null || _markLit != null) return true;
            if (_failed) return false;

            var multiply = FindShader(false, out var multiplyName);
            var blended = FindShader(true, out var blendedName);
            if (multiply == null && blended == null && !LitBlood.HasTransparent)
            {
                _failed = true;
                Plugin.Log.LogWarning("Ground blood: no usable shader found.");
                return false;
            }

            var markTex = BloodTextures.MarkAtlas();
            var poolTex = new Texture2D[PoolVariants];
            for (int v = 0; v < PoolVariants; v++) poolTex[v] = BloodTextures.Pool(v);
            if (multiply != null && multiplyName.Contains("Multiply"))
            {
                _markMaterial = new Material(multiply) { mainTexture = markTex, hideFlags = HideFlags.HideAndDontSave };
                NoSoftFade(_markMaterial);
                // Pools under marks: drips landing in a pool still show on top.
                _markMaterial.renderQueue = 2992;
                for (int v = 0; v < PoolVariants; v++)
                {
                    _poolMaterial[v] = new Material(multiply) { mainTexture = poolTex[v], hideFlags = HideFlags.HideAndDontSave };
                    NoSoftFade(_poolMaterial[v]);
                    _poolMaterial[v].renderQueue = 2990;
                }
            }
            if (blended != null && blendedName.Contains("Alpha Blended"))
            {
                // Unlit colour pass (Hybrid): RGB = texture x vertex light, alpha 0.8 x coverage.
                var tint = new Color(0.5f, 0.5f, 0.5f, 0.4f);
                _markColour = new Material(blended) { mainTexture = markTex, hideFlags = HideFlags.HideAndDontSave };
                _markColour.SetColor("_TintColor", tint);
                NoSoftFade(_markColour);
                _markColour.renderQueue = 2993;
                for (int v = 0; v < PoolVariants; v++)
                {
                    _poolColour[v] = new Material(blended) { mainTexture = poolTex[v], hideFlags = HideFlags.HideAndDontSave };
                    _poolColour[v].SetColor("_TintColor", tint);
                    NoSoftFade(_poolColour[v]);
                    _poolColour[v].renderQueue = 2991;
                }
            }
            // Lit colour pass (0.10.3): albedo = texture (dark red), lit by the map; pools glossy.
            _markLit = LitBlood.Transparent(markTex, MarkTint, 0.55f, 2993, "RealTargetsBloodMarksLit", 0.4f);
            for (int v = 0; v < PoolVariants; v++) _poolLit[v] = LitBlood.Transparent(poolTex[v], PoolFresh, 0.85f, 2991, "RealTargetsBloodPoolLit", 0.4f);
            ShaderName = $"{(_markMaterial != null ? multiplyName : "-")} + {(_markColour != null ? blendedName : "-")} + lit {LitBlood.TransparentName}";

            _chunkTriangles = new int[QuadsPerChunk * 6];
            for (int q = 0; q < QuadsPerChunk; q++)
            {
                int v = q * 4, i = q * 6;
                _chunkTriangles[i] = v; _chunkTriangles[i + 1] = v + 1; _chunkTriangles[i + 2] = v + 2;
                _chunkTriangles[i + 3] = v; _chunkTriangles[i + 4] = v + 2; _chunkTriangles[i + 5] = v + 3;
            }

            Plugin.Log.LogInfo($"Ground blood: shader {ShaderName}, mode {Plugin.DecalShader.Value}, {SoftParticleState()}");
            return true;
        }

        /// <summary>
        /// Legacy particle shaders fade a pixel by its distance to the depth buffer behind it when
        /// soft particles are on (fade = saturate(_InvFade * gap), _InvFade 1 per metre by default).
        /// Our decals sit 3-8 mm above the surface, so they faded to ~0.5 %: invisible wounds, pools
        /// and drips in the 0.9.1 test. A huge _InvFade makes any positive gap fully opaque.
        /// </summary>
        internal static void NoSoftFade(Material material)
        {
            if (material != null && material.HasProperty("_InvFade")) material.SetFloat("_InvFade", 5000f);
        }

        private static readonly string[] MultiplyNames = { "Legacy Shaders/Particles/Multiply", "Particles/Multiply" };
        private static readonly string[] BlendedNames = { "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended" };

        /// <summary>Multiply or alpha-blended particle shader (falls back to the other if missing or unsupported).</summary>
        private static readonly Dictionary<int, bool> MovableCache = new Dictionary<int, bool>();

        /// <summary>
        /// The surface belongs to something that moves or breaks: a door, gate, hatch or container lid (EFT's
        /// interactive objects) or a breakable window. Marks are baked into static chunk meshes, so blood laid on one
        /// stays in mid-air once it opens or shatters (0.10.13 raid: exit spatter on
        /// `Outside_Door_Metal_..._Door`, then "blood floating at a doorway"). Cached per collider.
        /// </summary>
        internal static bool Movable(Component surface)
        {
            if (surface == null) return false;
            int id = surface.GetInstanceID();
            if (MovableCache.TryGetValue(id, out bool movable)) return movable;
            movable = surface.GetComponentInParent<EFT.Interactive.WorldInteractiveObject>() != null ||
                      surface.GetComponentInParent<EFT.Interactive.WindowBreaker>() != null;
            if (!movable && surface is Collider c && c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) movable = true;
            MovableCache[id] = movable;
            return movable;
        }

        internal static Shader FindShader(bool blended, out string name)
        {
            var first = blended ? BlendedNames : MultiplyNames;
            var second = blended ? MultiplyNames : BlendedNames;
            foreach (var candidate in first)
            {
                var shader = Shader.Find(candidate);
                if (shader != null && shader.isSupported)
                {
                    name = candidate;
                    return shader;
                }
            }
            foreach (var candidate in second)
            {
                var shader = Shader.Find(candidate);
                if (shader != null && shader.isSupported)
                {
                    name = candidate;
                    return shader;
                }
            }
            name = "none";
            return null;
        }

        /// <summary>Materials for marks or pools in the current `Decal shader` mode (falls back to what exists).</summary>
        private Material[] Materials(bool pool, int variant)
        {
            var mul = pool ? _poolMaterial[variant] : _markMaterial;
            var col = pool ? _poolColour[variant] : _markColour;
            var lit = pool ? _poolLit[variant] : _markLit;
            switch (Plugin.DecalShader.Value)
            {
                case DecalShaderMode.Lit when lit != null:
                    return mul != null && !LitBlood.IsCutout ? new[] { mul, lit } : new[] { lit };
                case DecalShaderMode.Multiply when mul != null:
                    return new[] { mul };
                case DecalShaderMode.AlphaBlended when col != null:
                    return new[] { col };
                default:
                    if (mul != null && col != null) return new[] { mul, col };
                    return new[] { mul ?? col ?? lit };
            }
        }

        /// <summary>`Visuals / Decal shader` changed: re-shade ground blood, wound patches and gore live.</summary>
        internal static void ApplyDecalShader()
        {
            var inst = Instance;
            if (inst != null && (inst._markMaterial != null || inst._markColour != null || inst._markLit != null))
            {
                foreach (var chunk in inst._chunks.Values)
                {
                    var r = chunk?.Go != null ? chunk.Go.GetComponent<Renderer>() : null;
                    if (r == null) continue;
                    r.sharedMaterials = inst.Materials(false, 0);
                    Shade(r);
                }
                foreach (var pool in inst._allPools)
                {
                    var r = pool?.Go != null ? pool.Go.GetComponent<Renderer>() : null;
                    if (r == null) continue;
                    r.SetPropertyBlock(null);
                    r.sharedMaterials = inst.Materials(true, pool.Variant);
                    Shade(r);
                    inst.ShadePool(pool);
                }
            }
            WoundDecals.Instance?.Reshade();
            Plugin.Log.LogInfo($"Decal shader now {Plugin.DecalShader.Value} (ground {ShaderName})");
        }

        internal static string SoftParticleState() =>
            $"soft particles {(QualitySettings.softParticles ? "on" : "off")}, keyword {(Shader.IsKeywordEnabled("SOFTPARTICLES_ON") ? "on" : "off")}";
    }

    /// <summary>
    /// Procedural ground-blood textures for a multiply shader: RGB is the factor the surface is
    /// multiplied by (dark red), alpha how much of it applies.
    /// </summary>
    internal static class BloodTextures
    {
        private static readonly Color Fresh = new Color(0.30f, 0.015f, 0.015f);
        private static readonly Color Rim = new Color(0.40f, 0.03f, 0.025f);

        /// <summary>4x2 atlas, 128 px cells. Row 0: 0/1 round drips with satellites, 2 directional splat (runs toward +U), 3 star splat. Row 1 (0.10.6): 4-6 boot prints wet/medium/faint (toe toward +U, right foot), 7 smear (runs toward +U).</summary>
        internal static Texture2D MarkAtlas()
        {
            const int width = 512, height = 512, cell = 128;
            var pixels = new Color[width * height];
            var rng = new System.Random(77);
            for (int c = 0; c < 4; c++)
            {
                // Satellite droplets per cell (position in -0.5..0.5, radius).
                var dots = new List<Vector3>();
                int count = c == 2 ? 9 : c == 3 ? 10 : 5;
                for (int d = 0; d < count; d++)
                {
                    float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float dist = c == 2 ? 0.25f + 0.2f * (float)rng.NextDouble() : 0.3f + 0.15f * (float)rng.NextDouble();
                    var pos = c == 2
                        ? new Vector2(0.05f + 0.4f * (float)rng.NextDouble(), ((float)rng.NextDouble() - 0.5f) * 0.35f)
                        : new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
                    dots.Add(new Vector3(pos.x, pos.y, 0.015f + 0.03f * (float)rng.NextDouble()));
                }

                int ox = c * cell, oy = 0;
                for (int j = 0; j < cell; j++)
                {
                    for (int i = 0; i < cell; i++)
                    {
                        float x = (i + 0.5f) / cell - 0.5f, y = (j + 0.5f) / cell - 0.5f;
                        float a = Mathf.Atan2(y, x), r = Mathf.Sqrt(x * x + y * y);
                        float edge;
                        switch (c)
                        {
                            case 0: edge = 0.22f + 0.025f * Mathf.Sin(a * 7f + 1f) + 0.015f * Mathf.Sin(a * 13f); break;
                            case 1: edge = 0.19f + 0.04f * Mathf.Pow(Mathf.Abs(Mathf.Sin(a * 4f + 0.5f)), 4f); break;
                            case 2:
                            {
                                // Teardrop: round head at -U, thinning tail toward +U.
                                float head = Mathf.Sqrt((x + 0.18f) * (x + 0.18f) + y * y);
                                float tail = x > -0.18f && x < 0.42f ? Mathf.Abs(y) / Mathf.Lerp(0.15f, 0.02f, Mathf.InverseLerp(-0.18f, 0.42f, x)) : 9f;
                                // Distance in "edge units" (1 = on the outline), scaled to the shared 0.2 edge.
                                r = Mathf.Min(head / 0.17f, tail) * 0.2f;
                                edge = 0.2f;
                                break;
                            }
                            default: edge = 0.16f + 0.09f * Mathf.Pow(Mathf.Abs(Mathf.Sin(a * 5f + 0.3f)), 6f); break;
                        }
                        float body = 1f - Smooth(edge - 0.015f, edge + 0.01f, r);
                        float dot = 0f;
                        foreach (var d in dots)
                        {
                            float dd = Mathf.Sqrt((x - d.x) * (x - d.x) + (y - d.y) * (y - d.y));
                            dot = Mathf.Max(dot, 1f - Smooth(d.z * 0.8f, d.z, dd));
                        }
                        float cover = Mathf.Max(body, dot);
                        float rimness = Mathf.Clamp01(1f - Mathf.Abs(r - edge) / 0.03f) * body;
                        var col = Color.Lerp(Fresh, Rim, rimness * 0.7f);
                        col.a = cover * 0.95f;
                        pixels[(oy + j) * width + ox + i] = col;
                    }
                }
            }
            Footprints(pixels, width, cell);
            Smear(pixels, width, cell);
            DragStreaks(pixels, width, cell);
            return Make(width, height, pixels);
        }

        /// <summary>
        /// Cells 4-6: a right military boot sole (toe at +U, outer edge at +V), wet / drying / nearly dry. x runs
        /// heel (-0.5) to toe (+0.5), y across; the quad is stretched ~2.8x along x. 0.10.8 (Stompa: "pattern is
        /// off", wants a generic military boot sole; "colour way too dark, should read as blood"): the common
        /// combat-boot lug layout drawn here, no outside texture: a ring of block lugs along the edge, chevron
        /// lugs in the ball (pointing to the toe) and in the heel (pointing back) split by a centre channel, a
        /// heel breast gap with no print, a few bars across the arch. Only lug tops print; wet prints almost
        /// whole lugs, drying ones the ball and heel patchily, faint ones a few lugs. Clear dark red.
        /// Twin of the offline preview (scratchpad footprint.js).
        /// </summary>
        private static void Footprints(Color[] pixels, int width, int cell)
        {
            var colours = new[] { new Color(0.36f, 0.03f, 0.025f), new Color(0.36f, 0.05f, 0.035f), new Color(0.37f, 0.08f, 0.05f) };
            for (int level = 0; level < 3; level++)
            {
                int ox = level * cell, oy = cell;
                for (int j = 0; j < cell; j++)
                {
                    for (int i = 0; i < cell; i++)
                    {
                        float x = (i + 0.5f) / cell - 0.5f, y = (j + 0.5f) / cell - 0.5f;
                        float rag = Mathf.PerlinNoise(x * 11f + level * 3.7f, y * 11f + 2.3f);
                        if (!SoleLug(x, y, rag)) continue;

                        float smear = Mathf.PerlinNoise(x * 6f + 13f + level * 5f, y * 9f + 7f);
                        float pressure = Mathf.Max(Mathf.Exp(-(Sq(x - 0.2f) / 0.03f + Sq(y) / 0.07f)), Mathf.Exp(-(Sq(x + 0.33f) / 0.015f + Sq(y - 0.04f) / 0.05f)));
                        float a;
                        switch (level)
                        {
                            case 0: a = 0.3f + smear * 0.8f; break;
                            case 1: a = smear * 0.6f + pressure * 0.55f - 0.05f; break;
                            default: a = smear * 0.5f + pressure * 0.6f - 0.35f; break;
                        }
                        var c = colours[level] * Mathf.Lerp(0.85f, 1.1f, smear);
                        c.a = Mathf.Clamp01(a);
                        pixels[(oy + j) * width + ox + i] = c;
                    }
                }
            }
        }

        /// <summary>True where a lug of the generic military sole touches the ground (see Footprints).</summary>
        private static bool SoleLug(float x, float y, float rag)
        {
            // Outline: heel 0.3, arch 0.25 shifted outward, ball 0.4 (half widths), round heel and toe.
            float heelToArch = Smooth(-0.25f, -0.1f, x), archToBall = Smooth(0.0f, 0.15f, x);
            float half = Mathf.Lerp(Mathf.Lerp(0.3f, 0.25f, heelToArch), 0.4f, archToBall);
            float centre = Mathf.Lerp(Mathf.Lerp(0.03f, 0.1f, heelToArch), 0.02f, archToBall);
            if (x < -0.38f) half *= Mathf.Sqrt(Mathf.Clamp01(1f - Sq((x + 0.38f) / 0.1f)));
            if (x > 0.28f) half *= Mathf.Sqrt(Mathf.Clamp01(1f - Sq((x - 0.28f) / 0.19f)));
            half *= 0.96f + 0.08f * rag;
            float across = Mathf.Abs(y - centre);
            if (across >= half) return false;

            // Heel breast: the step between heel block and arch leaves no print.
            if (x > -0.17f && x < -0.11f) return false;
            // Arch: three bars across, narrower than the sole.
            if (x >= -0.11f && x < 0.03f)
            {
                return across < half * 0.7f && Mathf.Repeat((x + 0.11f) / 0.047f, 1f) < 0.55f;
            }

            float edge = half - across;
            // Ring of block lugs along the edge, cut into blocks by grooves across it.
            if (edge < 0.09f)
            {
                return Mathf.Repeat((x + 0.5f) / 0.055f, 1f) > 0.22f;
            }
            // Groove between the edge ring and the inner lugs.
            if (edge < 0.115f) return false;
            // Centre channel.
            float side = y - centre;
            if (Mathf.Abs(side) < 0.025f) return false;
            // Chevron lugs: in the ball pointing to the toe, in the heel pointing back; brick-split across.
            bool heel = x < -0.17f;
            float u = heel ? x + 0.09f * Mathf.Abs(side) : x - 0.09f * Mathf.Abs(side);
            float period = heel ? 0.07f : 0.075f;
            float row = u / period;
            if (Mathf.Repeat(row, 1f) >= 0.62f) return false;
            bool odd = (Mathf.FloorToInt(row) & 1) == 1;
            return !(odd && Mathf.Abs(Mathf.Abs(side) - 0.17f) < 0.02f);
        }

        private static float Sq(float v) => v * v;

        /// <summary>
        /// Cells 8 (wet) and 9 (drying): a drag streak (gore-5, Stompa: the first drag smears read as "droplets every here and
        /// there", then as a painted strip with a ladder edge). A band of even width along U with ragged edges, fine stripes
        /// along the drag (cloth pressed over the floor: noise stretched ~25x along U), soft ends so overlapping steps join
        /// into one trail. Drying: narrower, the stripes break into dashes with gaps. Darker than a fresh pool (a thin film
        /// over concrete). The mark's own alpha thins it further under the lit pass's cutout as the cloth runs dry.
        /// </summary>
        private static void DragStreaks(Color[] pixels, int width, int cell)
        {
            var wetColour = new Color(0.24f, 0.012f, 0.012f);
            var dryColour = new Color(0.22f, 0.035f, 0.022f);
            for (int level = 0; level < 2; level++)
            {
                int ox = level * cell, oy = 2 * cell;
                bool dry = level == 1;
                for (int j = 0; j < cell; j++)
                {
                    for (int i = 0; i < cell; i++)
                    {
                        float x = (i + 0.5f) / cell - 0.5f, y = (j + 0.5f) / cell - 0.5f;
                        // Even width with a ragged, slowly wandering edge on each side.
                        float side = y >= 0f ? 1f : 0f;
                        float half = (dry ? 0.3f : 0.38f) + 0.05f * (Mathf.PerlinNoise(x * 3f + 4f + side * 7f, side * 3f) - 0.5f)
                                     + 0.035f * (Mathf.PerlinNoise(x * 22f + 11f, side * 5f + 1f) - 0.5f);
                        float band = 1f - Smooth(half - 0.03f, half, Mathf.Abs(y));
                        // Stripes along the drag, wandering a little across it.
                        float stripe = Mathf.PerlinNoise(x * 2.2f + 3f, y * 48f + 0.6f * Mathf.Sin(x * 5f) + 20f);
                        float grain = Mathf.PerlinNoise(x * 18f + 40f, y * 18f + 7f);
                        float cover = dry
                            ? Smooth(0.45f, 0.62f, stripe * 0.8f + grain * 0.25f) * Smooth(0.25f, 0.4f, Mathf.PerlinNoise(x * 7f + 60f, y * 2f))
                            : Mathf.Lerp(0.25f, 1f, Smooth(0.38f, 0.62f, stripe)) * Mathf.Lerp(0.8f, 1f, grain) *
                              Mathf.Lerp(0.7f, 1f, Smooth(0.2f, 0.45f, Mathf.PerlinNoise(x * 5f + 80f, y * 3f)));
                        // Soft ends (U +-0.5): the next step overlaps and takes over.
                        float ends = Smooth(-0.5f, -0.3f, x) * (1f - Smooth(0.3f, 0.5f, x));
                        var c = Color.Lerp(dry ? dryColour : wetColour, Rim * 0.8f, (1f - stripe) * 0.35f);
                        c.a = band * cover * ends * 0.95f;
                        pixels[(oy + j) * width + ox + i] = c;
                    }
                }
            }
        }

        /// <summary>Cell 7: a smear left by a gib sliding down a wall: wide where it stuck (-U), thinning toward +U, patchy.</summary>
        private static void Smear(Color[] pixels, int width, int cell)
        {
            int ox = 3 * cell, oy = cell;
            for (int j = 0; j < cell; j++)
            {
                for (int i = 0; i < cell; i++)
                {
                    float x = (i + 0.5f) / cell - 0.5f, y = (j + 0.5f) / cell - 0.5f;
                    float half = Mathf.Lerp(0.36f, 0.12f, Mathf.InverseLerp(-0.45f, 0.45f, x)) * (1f + 0.15f * Mathf.Sin(x * 23f));
                    float n = Mathf.PerlinNoise(x * 6f + 9f, y * 14f + 2f);
                    float body = Mathf.Abs(y) < half && x > -0.46f && x < 0.46f ? 1f : 0f;
                    var c = Color.Lerp(Fresh, Rim, n * 0.5f);
                    c.a = body * (n > 0.3f ? 0.92f : 0.35f);
                    pixels[(oy + j) * width + ox + i] = c;
                }
            }
        }

        private static Texture2D Make(int width, int height, Color[] pixels)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixels(pixels);
            tex.Apply(true, false);
            return tex;
        }

        /// <summary>
        /// Pool outline <paramref name="variant"/> (0.10.3: four, picked at random per pool; 0.10.2's single
        /// round-ish blob read as a stamped shape): a lobed blob filling ~80 % of the patch with random
        /// harmonics, 1-3 tongues where blood ran along the floor, a darker centre and edge droplets.
        /// </summary>
        internal static Texture2D Pool(int variant)
        {
            const int size = 256;
            var pixels = new Color[size * size];
            var rng = new System.Random(1301 + variant * 7919);
            float R() => (float)rng.NextDouble();
            var freq = new float[4];
            var amp = new float[4];
            var phase = new float[4];
            for (int h = 0; h < 4; h++)
            {
                freq[h] = 2 + rng.Next(h * 2 + 2);
                amp[h] = (0.045f - h * 0.009f) * (0.5f + R());
                phase[h] = R() * Mathf.PI * 2f;
            }
            int tongues = 1 + rng.Next(3);
            var tongueAngle = new float[tongues];
            var tongueLength = new float[tongues];
            for (int q = 0; q < tongues; q++)
            {
                tongueAngle[q] = R() * Mathf.PI * 2f;
                tongueLength[q] = 0.04f + 0.07f * R();
            }
            float nx = R() * 50f, ny = R() * 50f;
            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    float x = (i + 0.5f) / size - 0.5f, y = (j + 0.5f) / size - 0.5f;
                    float a = Mathf.Atan2(y, x), r = Mathf.Sqrt(x * x + y * y);
                    float edge = 0.32f + 0.03f * (Mathf.PerlinNoise(x * 7f + nx, y * 7f + ny) - 0.5f);
                    for (int h = 0; h < 4; h++) edge += amp[h] * Mathf.Sin(a * freq[h] + phase[h]);
                    for (int q = 0; q < tongues; q++)
                    {
                        float da = Mathf.DeltaAngle(a * Mathf.Rad2Deg, tongueAngle[q] * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                        edge += tongueLength[q] * Mathf.Exp(-da * da / 0.03f);
                    }
                    edge = Mathf.Min(edge, 0.46f);
                    float body = 1f - Smooth(edge - 0.012f, edge + 0.006f, r);
                    float speck = Mathf.PerlinNoise(x * 40f + 11f, y * 40f + 5f);
                    float ring = r > edge && r < edge + 0.07f && speck > 0.74f ? Mathf.InverseLerp(0.74f, 0.8f, speck) : 0f;
                    float depth = Mathf.Clamp01(1f - r / Mathf.Max(edge, 0.01f));
                    var col = Color.Lerp(Rim, Fresh * 0.75f, Smooth(0f, 0.6f, depth));
                    col.a = Mathf.Max(body * 0.97f, ring * 0.9f);
                    pixels[j * size + i] = col;
                }
            }
            return Make(size, pixels);
        }

        /// <summary>
        /// Shader-style smoothstep: 0 below e0, 1 above e1. Unity's Mathf.SmoothStep(from, to, t) is a
        /// different function (eases between from and to by t); using it here made every mark and pool
        /// a solid rectangle (0.9.2 test).
        /// </summary>
        private static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        private static Texture2D Make(int size, Color[] pixels)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixels(pixels);
            tex.Apply(true, false);
            return tex;
        }
    }
}
