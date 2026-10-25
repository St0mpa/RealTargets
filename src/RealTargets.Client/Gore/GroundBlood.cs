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
        private const float Lift = 0.008f;

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
            public BloodTone Tone;
            public byte Alpha;
            /// <summary>Light level where it landed (vertex RGB): scales the colour pass, never glows in the dark.</summary>
            public byte Light;
        }

        private sealed class Chunk
        {
            public GameObject Go;
            public Mesh Mesh;
            public (long cell, BloodTone tone) CellKey;
            public BloodTone Tone;
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

        private readonly Dictionary<(long, BloodTone), Chunk> _chunks = new Dictionary<(long, BloodTone), Chunk>();
        private readonly Dictionary<long, (Chunk chunk, int slot)> _byCell = new Dictionary<long, (Chunk, int)>();
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();

        private readonly Dictionary<object, BloodPool> _pools = new Dictionary<object, BloodPool>();
        private readonly Queue<BloodPool> _allPools = new Queue<BloodPool>();

        // Multiply (darken) and colour passes: lit (Lit mode, 0.10.3) or unlit (Hybrid). Pools (1.2.0): one material per
        // pass, each pool's own field texture set per renderer.
        private Material _markMaterial, _markColour, _markLit;
        private Material _poolMaterial, _poolColour, _poolLit;
        /// <summary>blood-5: marks and pools on EFT's own shaders, per GroundShaderMode (0 wet, 1 matte), built on first use.</summary>
        private readonly Material[] _markEft = new Material[2], _poolEft = new Material[2];
        private readonly bool[] _eftTried = new bool[2];
        private Texture2D _markTex;
        private int[] _chunkTriangles;
        private bool _failed;

        /// <summary>perf-3 (dev split benchmark): every renderer drawing ground marks (chunks) or pools.</summary>
        internal void CollectRenderers(List<Renderer> into)
        {
            foreach (var chunk in _chunks.Values)
            {
                if (chunk?.Go != null) into.Add(chunk.Go.GetComponent<Renderer>());
            }
            foreach (var pool in _allPools)
            {
                if (pool?.Go != null && pool.Renderer != null) into.Add(pool.Renderer);
            }
        }

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

            while (_allPools.Count > 0) _allPools.Dequeue()?.Destroy();
            _pools.Clear();
        }

        private void LateUpdate()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Ground);
            using (RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.PoolTick)) TickPools();
            using var marks = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.MarkUpload);

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
        internal void Drip(Vector3 point, Vector3 normal, BloodTone tone = BloodTone.Venous)
        {
            if (!Ready()) return;

            long key = Key(point);
            // blood-3 (a standing bleeder left a confetti of separate drops): a drop within ~7 cm of a drip spot joins it.
            if (NearDrip(point, out var at))
            {
                // Blood keeps landing on the same spot: the spot spreads and darkens. blood-2: one mark scaled up to 11 cm stayed a
                // perfect disc; past 5.5 cm it stops growing and the puddle grows lumps (overlapping drops beside it) instead.
                ref var m = ref at.chunk.Marks[at.slot];
                m.Alpha = (byte)Mathf.Min(255, m.Alpha + 12);
                if (m.Size < 0.055f)
                {
                    m.Size = Mathf.Min(m.Size * 1.07f + 0.0015f, 0.055f);
                    Write(at.chunk, at.slot);
                    return;
                }
                Write(at.chunk, at.slot);
                if (Random.value > 0.35f) return;
                var lump = Random.insideUnitCircle.normalized * (m.Size * Random.Range(0.45f, 0.8f));
                var side = Vector3.Cross(m.Normal, m.Axis);
                Add(new Mark
                {
                    Point = m.Point + m.Axis * lump.x + side * lump.y,
                    Normal = m.Normal,
                    Axis = RandomTangent(m.Normal),
                    Size = m.Size * Random.Range(0.55f, 0.9f),
                    Stretch = Random.Range(1f, 1.4f),
                    Atlas = Random.value < 0.5f ? 0 : 1,
                    Key = long.MinValue,
                    Tone = m.Tone,
                    Alpha = m.Alpha,
                    Light = m.Light,
                });
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
                Tone = tone,
                Alpha = (byte)Random.Range(170, 225),
                Light = LightAt(point, normal),
            });
        }

        /// <summary>A fine-spray droplet (cough, the fine part of an exit spray): a 4-12 mm speckle, a little stretched along its travel.</summary>
        internal void Speckle(Vector3 point, Vector3 normal, Vector3 velocity, BloodTone tone)
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
                Size = Random.Range(0.004f, 0.012f),
                Stretch = Mathf.Clamp(1f + speed * 0.12f, 1f, 1.6f),
                Atlas = Random.value < 0.5f ? 0 : 1,
                Key = long.MinValue,
                Tone = tone,
                Alpha = (byte)Random.Range(180, 240),
                Light = LightAt(point, normal),
            });
        }

        /// <summary>
        /// A keyed drip mark within ~10 cm of <paramref name="point"/> (the 5x5 cells around it, same height cell; blood-5: was
        /// 3x3, ~5 cm, while a swaying bleeder's drops land over 10-20 cm).
        /// </summary>
        private bool NearDrip(Vector3 point, out (Chunk chunk, int slot) found)
        {
            for (int dz = -2; dz <= 2; dz++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    long key = Key(point + new Vector3(dx * Cell, 0f, dz * Cell));
                    if (_byCell.TryGetValue(key, out var at) && at.chunk.Go != null && at.chunk.Marks[at.slot].Key == key)
                    {
                        found = at;
                        return true;
                    }
                }
            }
            found = default;
            return false;
        }

        /// <summary>A droplet arriving with sideways speed (spurt) or hitting a wall: a splat stretched along its travel.</summary>
        internal void Splat(Vector3 point, Vector3 normal, Vector3 velocity, BloodTone tone = BloodTone.Venous)
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
                Tone = tone,
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
                // blood-5b (Stompa: the 1.1.0 drag trail "looked more realistic"): the 1.1.0 step again (2 widths long, DragSmears
                // lays one every width), only the colours stay on the blood-2 palette.
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
                if (pool?.Go == null || pool.Dry > 230) continue;
                best = Mathf.Max(best, pool.WetnessAt(point));
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
            LogDecalsAround(spot);

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

        /// <summary>
        /// blood-1 diagnosis: what the floor under <paramref name="spot"/> is drawn with, and every renderer within 2 m whose
        /// shader or material says decal (shader, render queue, layer): tells whether map decals draw after our pools.
        /// </summary>
        private static void LogDecalsAround(Vector3 spot)
        {
            try
            {
                if (Physics.Raycast(spot + Vector3.up * 0.4f, Vector3.down, out var hit, 2.5f, LayersMaskController.HighPolyWithTerrainMask))
                {
                    var floor = hit.collider.GetComponent<Renderer>() ?? hit.collider.GetComponentInParent<Renderer>();
                    var m = floor != null ? floor.sharedMaterial : null;
                    Plugin.Log.LogInfo($"[Blood test] floor {hit.collider.name}: {(m != null ? $"{m.name}, shader {m.shader?.name}, queue {m.renderQueue}" : "no renderer")}");
                }
                int n = 0;
                foreach (var r in FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled || (r.bounds.ClosestPoint(spot) - spot).sqrMagnitude > 4f) continue;
                    var m = r.sharedMaterial;
                    string shader = m != null && m.shader != null ? m.shader.name : "?";
                    if (shader.IndexOf("decal", System.StringComparison.OrdinalIgnoreCase) < 0 &&
                        (m == null || m.name.IndexOf("decal", System.StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    Plugin.Log.LogInfo($"[Blood test] decal near: {r.name} ({r.GetType().Name}) shader {shader}, queue {m?.renderQueue}, layer {LayerMask.LayerToName(r.gameObject.layer)}");
                    if (++n >= 15) break;
                }
                Plugin.Log.LogInfo($"[Blood test] {n} decal renderer(s) within 2 m; our pools/marks queue {(Instance?._poolLit != null ? Instance._poolLit.renderQueue : -1)}");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Blood test] decal scan: {ex.Message}");
            }
        }

        private void Add(Mark mark)
        {
            var chunk = GetChunk(mark.Point, mark.Tone);
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
        private Chunk GetChunk(Vector3 point, BloodTone tone)
        {
            var key = (ChunkKey(point), tone);
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
                Tone = tone,
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
            renderer.sharedMaterials = Materials(false);
            Shade(renderer);
            TintChunk(chunk);
            _chunks[key] = chunk;
            return chunk;
        }

        /// <summary>Lit marks of a brighter tone (artery, lung): the venous atlas tinted per chunk (1.2.0).</summary>
        private void TintChunk(Chunk chunk)
        {
            if (chunk.Tone == BloodTone.Venous || chunk.Go == null || Plugin.DecalShader.Value != DecalShaderMode.Lit) return; // venous: the material colour
            var r = chunk.Go.GetComponent<Renderer>();
            if (r == null) return;
            var tint = LitBlood.Scaled(BloodPalette.MarkTint(chunk.Tone), LitBlood.GroundSurface);
            tint.a = MarkTint.a;
            LitBlood.SetColor(r, r.sharedMaterials.Length - 1, tint, _block);
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

        /// <summary>Grows the wound's pool under <paramref name="site"/> to the given radius (m), venous, one source.</summary>
        internal void Pool(object wound, string label, Vector3 site, float radius) =>
            Pool(wound, label, site, radius, 0f, null);

        /// <summary>
        /// The body's pool (1.2.0, <see cref="BloodPool"/>): wet area = pi r^2 for <paramref name="radius"/>, spreading from
        /// <paramref name="sources"/> (the openings blood runs from; null = <paramref name="site"/>), as scarlet as
        /// <paramref name="oxygen"/> (0 venous .. 1 arterial) while fresh. The edge moves every frame (LateUpdate).
        /// </summary>
        internal void Pool(object wound, string label, Vector3 site, float radius, float oxygen, IList<Vector3> sources)
        {
            if (!Ready()) return;
            using var part = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.PoolFeed);

            if (_pools.TryGetValue(wound, out var pool) && pool != null)
            {
                if (pool.Go == null && pool.Mesh == null)
                {
                    // No floor here last time; try again once the body has moved.
                    if ((site - pool.Center).sqrMagnitude < 0.3f * 0.3f) return;
                    pool = null;
                }
                else
                {
                    var off = Vector3.ProjectOnPlane(site - pool.Center, Vector3.up);
                    if (off.magnitude > 0.35f + pool.Radius * 0.5f && off.magnitude > 0.5f)
                    {
                        pool = null; // body moved away: the old pool stays, a new one starts here
                    }
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
                    _pools[wound] = new BloodPool { Center = site }; // don't retry every update
                    return;
                }
                pool = new BloodPool { Label = label, Oxygen = Mathf.Clamp01(oxygen) };
                pool.Mesh = new Mesh { name = "RealTargetsBloodPool", hideFlags = HideFlags.HideAndDontSave };
                pool.Mesh.MarkDynamic();
                pool.Go = new GameObject("RealTargetsBloodPool");
                pool.Go.AddComponent<MeshFilter>().sharedMesh = pool.Mesh;
                pool.Renderer = pool.Go.AddComponent<MeshRenderer>();
                pool.Renderer.sharedMaterials = Materials(true);
                pool.Renderer.enabled = false;
                Shade(pool.Renderer);
                var normal = ground.normal.y > 0.5f ? ground.normal.normalized : Vector3.up;
                var starts = new List<Vector3>();
                if (sources != null) starts.AddRange(sources);
                if (starts.Count == 0) starts.Add(site);
                pool.Start(ground.point, normal, starts);
                _pools[wound] = pool;
                _allPools.Enqueue(pool);
                while (_allPools.Count > MaxPools) _allPools.Dequeue().Destroy();
            }
            else
            {
                pool.UpdateSources(sources);
                // More oxygenated blood joining (a cut artery) shifts the fresh colour a little.
                pool.Oxygen = Mathf.Max(pool.Oxygen * 0.98f, Mathf.Clamp01(oxygen));
            }

            pool.TargetArea = Mathf.Max(pool.TargetArea, Mathf.PI * radius * radius);

            // blood-5c (HollywoodFX's splash beside our pool read real, ours a clean sticker): drops that fell round the body
            // while it bled; a few small ones just past the edge each time the pool's radius grows another 8 cm (max 18).
            float r = Mathf.Sqrt(pool.TargetArea / Mathf.PI);
            if (pool.Go != null && pool.Satellites < 18 && r >= pool.NextSatellite)
            {
                pool.NextSatellite = r + 0.08f;
                int n = Random.Range(2, 5);
                for (int i = 0; i < n && pool.Satellites < 18; i++) Satellite(pool, r);
            }
        }

        /// <summary>One small drop on the floor just outside a pool of radius <paramref name="r"/> (m).</summary>
        private void Satellite(BloodPool pool, float r)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            float d = r * Random.Range(0.95f, 1.4f) + 0.02f;
            var at = pool.Center + (pool.Axis * Mathf.Cos(a) + pool.Side * Mathf.Sin(a)) * d;
            if (!Physics.Raycast(at + pool.Normal * 0.3f, -pool.Normal, out var hit, 0.6f, LayersMaskController.HighPolyWithTerrainMask) ||
                Movable(hit.collider) || hit.normal.y < 0.6f) return;
            pool.Satellites++;
            float s = Random.value;
            Add(new Mark
            {
                Point = hit.point,
                Normal = hit.normal.normalized,
                Axis = RandomTangent(hit.normal),
                Size = 0.008f + 0.024f * s * s,
                Stretch = Random.Range(1f, 1.3f),
                Atlas = Random.value < 0.7f ? (Random.value < 0.5f ? 0 : 1) : 3,
                Key = long.MinValue,
                Tone = BloodTone.Venous,
                Alpha = (byte)Random.Range(200, 240),
                Light = LightAt(hit.point, hit.normal),
            });
        }

        /// <summary>Every pool: finished fields picked up, edges moved, colours re-baked as they clot and dry.</summary>
        private void TickPools()
        {
            if (_allPools.Count == 0) return;
            bool cutout = Plugin.DecalShader.Value == DecalShaderMode.Lit && LitBlood.IsCutout;
            float dt = Time.deltaTime;
            foreach (var pool in _allPools)
            {
                if (pool?.Go == null) continue;
                try
                {
                    pool.Tick(dt, cutout, _block);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Pool] {pool.Label}: {ex.Message}");
                    pool.Destroy();
                }
            }
        }

        private static readonly Color MarkTint = new Color(1f, 1f, 1f, 0.9f);

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
            _markTex = markTex;
            if (multiply != null && multiplyName.Contains("Multiply"))
            {
                _markMaterial = new Material(multiply) { mainTexture = markTex, hideFlags = HideFlags.HideAndDontSave };
                NoSoftFade(_markMaterial);
                // Pools under marks: drips landing in a pool still show on top.
                _markMaterial.renderQueue = 2992;
                _poolMaterial = new Material(multiply) { hideFlags = HideFlags.HideAndDontSave };
                NoSoftFade(_poolMaterial);
                _poolMaterial.renderQueue = 2990;
            }
            if (blended != null && blendedName.Contains("Alpha Blended"))
            {
                // Unlit colour pass (Hybrid): RGB = texture x vertex light, alpha 0.8 x coverage.
                var tint = new Color(0.5f, 0.5f, 0.5f, 0.4f);
                _markColour = new Material(blended) { mainTexture = markTex, hideFlags = HideFlags.HideAndDontSave };
                _markColour.SetColor("_TintColor", tint);
                NoSoftFade(_markColour);
                _markColour.renderQueue = 2993;
                _poolColour = new Material(blended) { hideFlags = HideFlags.HideAndDontSave };
                _poolColour.SetColor("_TintColor", tint);
                NoSoftFade(_poolColour);
                _poolColour.renderQueue = 2991;
            }
            // Lit colour pass (0.10.3): albedo = texture (dark red), lit by the map; pools glossy.
            _markLit = LitBlood.Transparent(markTex, MarkTint, 0.55f, 2993, "RealTargetsBloodMarksLit", 0.4f);
            LateOpaque(_markLit);
            // Pools: colour and alpha (arrival time) come from each pool's own texture (BloodPool); the cutoff moves its edge.
            _poolLit = LitBlood.Transparent(null, Color.white, 0.85f, 2991, "RealTargetsBloodPoolLit", 1.01f);
            LateOpaque(_poolLit);
            ShaderName = $"{(_markMaterial != null ? multiplyName : "-")} + {(_markColour != null ? blendedName : "-")} + lit {LitBlood.TransparentName}";

            _chunkTriangles = new int[QuadsPerChunk * 6];
            for (int q = 0; q < QuadsPerChunk; q++)
            {
                int v = q * 4, i = q * 6;
                _chunkTriangles[i] = v; _chunkTriangles[i + 1] = v + 1; _chunkTriangles[i + 2] = v + 2;
                _chunkTriangles[i + 3] = v; _chunkTriangles[i + 4] = v + 2; _chunkTriangles[i + 5] = v + 3;
            }

            EftMaterial(false);
            EftMaterial(true);
            Plugin.Log.LogInfo($"Ground blood: shader {ShaderName}, mode {Plugin.DecalShader.Value}, ground shader {Plugin.GroundShader.Value}" +
                               $"{(LitBlood.GroundIsEft ? "" : " (not in use)")}, {SoftParticleState()}, colour space {QualitySettings.activeColorSpace}");
            return true;
        }

        /// <summary>The marks' or pools' material on EFT's shader for the picked GroundShaderMode; null = Stock or not found.</summary>
        private Material EftMaterial(bool pool)
        {
            var mode = Plugin.GroundShader.Value;
            if (mode == GroundShaderMode.Stock) return null;
            int i = mode == GroundShaderMode.EftWet ? 0 : 1;
            if (!_eftTried[i])
            {
                _eftTried[i] = true;
                _markEft[i] = LitBlood.EftGround(mode, _markTex, MarkTint, "RealTargetsBloodMarksEft", 0.4f);
                _poolEft[i] = LitBlood.EftGround(mode, null, Color.white, "RealTargetsBloodPoolEft", 1.01f);
            }
            return pool ? _poolEft[i] : _markEft[i];
        }

        /// <summary>
        /// blood-1 test: the floor's grime and scratches showed ON our pools (torch on, or on gritty concrete): probably the
        /// map's own decal meshes drawn after us in the deferred pass. A cutout material sits at AlphaTest (2450); 2500 is the
        /// last queue the deferred pass still draws (shadows kept), so ours lands after decals queued in between. Test.
        /// </summary>
        private static void LateOpaque(Material m)
        {
            if (m != null && LitBlood.IsCutout) m.renderQueue = 2500;
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
        private Material[] Materials(bool pool)
        {
            var mul = pool ? _poolMaterial : _markMaterial;
            var col = pool ? _poolColour : _markColour;
            var lit = pool ? _poolLit : _markLit;
            switch (Plugin.DecalShader.Value)
            {
                case DecalShaderMode.Lit when lit != null:
                    var eft = LitBlood.GroundIsEft ? EftMaterial(pool) : null;
                    if (eft != null) return new[] { eft };
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
                    r.SetPropertyBlock(null);
                    r.sharedMaterials = inst.Materials(false);
                    Shade(r);
                    inst.TintChunk(chunk);
                }
                foreach (var pool in inst._allPools)
                {
                    var r = pool?.Go != null ? pool.Go.GetComponent<Renderer>() : null;
                    if (r == null) continue;
                    r.SetPropertyBlock(null);
                    r.sharedMaterials = inst.Materials(true);
                    Shade(r);
                    pool.Reshade(inst._block);
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
        // blood-1 (palette): drops and splats at venous film level, a slightly lighter thin rim (were 0.30 / 0.40: paint).
        private static readonly Color Fresh = BloodPalette.VenousFilm;
        private static readonly Color Rim = new Color(0.235f, 0.022f, 0.024f);

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
                            case 0: edge = 0.22f + 0.012f * Mathf.Sin(a * 7f + 1f) + 0.01f * Mathf.Sin(a * 3f) + 0.02f * (Mathf.PerlinNoise(x * 9f + 3f, y * 9f) - 0.5f); break;
                            case 1: edge = 0.19f + 0.02f * Mathf.Sin(a * 3f + 0.5f) + 0.015f * Mathf.Sin(a * 5f) + 0.02f * (Mathf.PerlinNoise(x * 9f, y * 9f + 7f) - 0.5f); break;
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
                            // blood-1: was a star (0.16 + 0.09 |sin 5a|^6); now an irregular splash, lobed, ragged.
                            default: edge = 0.17f + 0.035f * Mathf.Sin(a * 2f + 0.3f) + 0.025f * Mathf.Sin(a * 3f + 2.1f) + 0.04f * (Mathf.PerlinNoise(x * 7f + 5f, y * 7f) - 0.5f); break;
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
            // blood-1: on the palette (were 0.36-0.37 R: bright red stamps); a drying print browner.
            var colours = new[] { new Color(0.2f, 0.018f, 0.021f), new Color(0.2f, 0.03f, 0.026f), new Color(0.19f, 0.045f, 0.034f) };
            for (int level = 0; level < 3; level++)
            {
                int ox = level * cell, oy = cell;
                for (int j = 0; j < cell; j++)
                {
                    for (int i = 0; i < cell; i++)
                    {
                        float x = (i + 0.5f) / cell - 0.5f, y = (j + 0.5f) / cell - 0.5f;
                        float rag = Mathf.PerlinNoise(x * 11f + level * 3.7f, y * 11f + 2.3f);
                        float smear = Mathf.PerlinNoise(x * 6f + 13f + level * 5f, y * 9f + 7f);
                        // blood-1 (Stompa: "blocky"): lug edges broken up, and a wet print fills some grooves where blood pooled.
                        bool lug = SoleLug(x, y, rag) && Mathf.PerlinNoise(x * 34f + level * 9f, y * 34f + 4f) > 0.27f;
                        bool groove = !lug && level == 0 && SoleInside(x, y, rag) && smear > 0.6f;
                        if (!lug && !groove) continue;

                        float pressure = Mathf.Max(Mathf.Exp(-(Sq(x - 0.2f) / 0.03f + Sq(y) / 0.07f)), Mathf.Exp(-(Sq(x + 0.33f) / 0.015f + Sq(y - 0.04f) / 0.05f)));
                        float a;
                        switch (level)
                        {
                            case 0: a = 0.3f + smear * 0.8f; break;
                            case 1: a = smear * 0.6f + pressure * 0.55f - 0.05f; break;
                            default: a = smear * 0.5f + pressure * 0.6f - 0.35f; break;
                        }
                        var c = colours[level] * Mathf.Lerp(0.85f, 1.1f, smear);
                        if (groove) a *= 0.85f;
                        c.a = Mathf.Clamp01(a);
                        pixels[(oy + j) * width + ox + i] = c;
                    }
                }
            }
        }

        /// <summary>Inside the sole outline (lug or groove).</summary>
        private static bool SoleInside(float x, float y, float rag)
        {
            float heelToArch = Smooth(-0.25f, -0.1f, x), archToBall = Smooth(0.0f, 0.15f, x);
            float half = Mathf.Lerp(Mathf.Lerp(0.3f, 0.25f, heelToArch), 0.4f, archToBall);
            float centre = Mathf.Lerp(Mathf.Lerp(0.03f, 0.1f, heelToArch), 0.02f, archToBall);
            if (x < -0.38f) half *= Mathf.Sqrt(Mathf.Clamp01(1f - Sq((x + 0.38f) / 0.1f)));
            if (x > 0.28f) half *= Mathf.Sqrt(Mathf.Clamp01(1f - Sq((x - 0.28f) / 0.19f)));
            half *= 0.96f + 0.08f * rag;
            return Mathf.Abs(y - centre) < half && x > -0.48f && x < 0.47f;
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
        /// blood-5b: back to this 1.1.0 shape (blood-3..5 tried a smooth capsule band; Stompa preferred the 1.1.0 look), with
        /// the blood-2 colours.
        /// </summary>
        private static void DragStreaks(Color[] pixels, int width, int cell)
        {
            var wetColour = new Color(0.165f, 0.013f, 0.016f);
            var dryColour = new Color(0.15f, 0.036f, 0.026f);
            for (int level = 0; level < 2; level++)
            {
                int ox = level * cell, oy = 2 * cell;
                bool dry = level == 1;
                for (int j = 0; j < cell; j++)
                {
                    for (int i = 0; i < cell; i++)
                    {
                        float x = (i + 0.5f) / cell - 0.5f, y = (j + 0.5f) / cell - 0.5f;
                        // blood-5b (Stompa: the 1.1.0 trail on the promo card "looked more realistic" than blood-3..5's smooth
                        // capsule band): the 1.1.0 (gore-5) cell again. Even width with a ragged, slowly wandering edge on each side.
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
