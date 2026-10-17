using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Procedural meshes, textures and materials for dismemberment: the stump cap over a cut (domed
    /// disc, ragged rim, muscle, vessels, bone) and flesh/skull gibs. 0.10.3: drawn with a lit opaque
    /// shader (LitBlood: the map's light, wet sheen); 0.10.2's unlit particle shader tinted by
    /// SceneLight glowed (bright red caps with a white dot, glowing skull bits in the 0.10.2 test) and
    /// stays only as the fallback when no lit shader exists. No assets ship with the mod.
    /// </summary>
    internal static class GoreMeshes
    {
        private const int Segments = 18;
        private static Material _stump, _flesh, _bone, _crater, _brain;
        private static bool _made;
        private static Mesh _stumpMesh;
        private static readonly List<Mesh> GibMeshes = new List<Mesh>();

        internal enum StumpKind { Neck, Limb }

        /// <summary>Stumps and gibs use the lit opaque shader (else the unlit fallback + ShadeByLight).</summary>
        internal static bool Lit { get; private set; }

        /// <summary>The stump cap's material (null if no shader).</summary>
        internal static Material StumpMaterial
        {
            get
            {
                MakeMaterials();
                return _stump;
            }
        }

        /// <summary>Skull burst crater (0.10.5): u around the hole, v from the rim (0, skull edge) to the bottom (1, clot).</summary>
        internal static Material CraterMaterial
        {
            get
            {
                MakeMaterials();
                return _crater;
            }
        }

        /// <summary>Brain matter (0.10.6): pinkish grey, folded, streaked with blood, wet.</summary>
        internal static Material BrainMaterial
        {
            get
            {
                MakeMaterials();
                return _brain;
            }
        }

        /// <summary>Gib material: flesh (dark wet red) or skull (dull bone, smeared).</summary>
        internal static Material GibMaterial(bool bone)
        {
            MakeMaterials();
            return bone ? _bone : _flesh;
        }

        /// <summary>Builds the gore materials and meshes while the raid loads (0.10.10: not on the first kill).</summary>
        internal static void Warm()
        {
            MakeMaterials();
            _ = StumpMesh;
            Gib(false);
        }

        private static void MakeMaterials()
        {
            if (_made) return;
            _made = true;
            var stumpTex = StumpTexture();
            var fleshTex = GibTexture(false);
            var boneTex = GibTexture(true);
            var craterTex = CraterTexture();
            var brainTex = BrainTexture();
            if (LitBlood.HasOpaque)
            {
                Lit = true;
                _stump = LitBlood.Opaque(stumpTex, Color.white, 0.8f, "RealTargetsStump");
                _flesh = LitBlood.Opaque(fleshTex, Color.white, 0.75f, "RealTargetsGib");
                _bone = LitBlood.Opaque(boneTex, Color.white, 0.35f, "RealTargetsGibBone");
                _crater = LitBlood.Opaque(craterTex, Color.white, 0.85f, "RealTargetsCrater");
                _brain = LitBlood.Opaque(brainTex, Color.white, 0.9f, "RealTargetsBrain");
            }
            else
            {
                _stump = Unlit(stumpTex, "RealTargetsStump");
                _flesh = Unlit(fleshTex, "RealTargetsGib");
                _bone = Unlit(boneTex, "RealTargetsGibBone");
                _crater = Unlit(craterTex, "RealTargetsCrater");
                _brain = Unlit(brainTex, "RealTargetsBrain");
            }
            Plugin.Log.LogInfo($"Gore materials: {(Lit ? "lit " + LitBlood.OpaqueName : "unlit fallback")}");
        }

        private static Material Unlit(Texture2D texture, string name)
        {
            var shader = GroundBlood.FindShader(true, out var shaderName);
            if (shader == null || !shaderName.Contains("Alpha Blended")) return null;
            var m = new Material(shader) { mainTexture = texture, name = name, hideFlags = HideFlags.HideAndDontSave };
            GroundBlood.NoSoftFade(m);
            m.renderQueue = 2994;
            return m;
        }

        /// <summary>Renderer settings for a stump or gib: lit (probes) or the unlit fallback tinted by light.</summary>
        internal static void Shade(Renderer r)
        {
            if (Lit)
            {
                LitBlood.Setup(r);
                return;
            }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.gameObject.AddComponent<ShadeByLight>().Init(r, 1f);
        }

        /// <summary>
        /// A stump cap: unit-radius domed disc in the XY plane facing +Z (the cut surface faces +Z),
        /// centre 0.25 above the rim. Scale it to the limb's radius.
        /// </summary>
        internal static Mesh StumpMesh
        {
            get
            {
                if (_stumpMesh != null) return _stumpMesh;
                var vertices = new List<Vector3> { new Vector3(0f, 0f, 0.25f) };
                var uvs = new List<Vector2> { new Vector2(0.5f, 0.5f) };
                var tris = new List<int>();
                int rings = 3;
                for (int r = 1; r <= rings; r++)
                {
                    float f = r / (float)rings;
                    for (int s = 0; s < Segments; s++)
                    {
                        float a = s / (float)Segments * Mathf.PI * 2f;
                        // A little torn: the rim wobbles in and out.
                        float wobble = r == rings ? 1f + 0.12f * Mathf.Sin(a * 5f + 0.7f) + 0.06f * Mathf.Sin(a * 11f) : 1f;
                        float x = Mathf.Cos(a) * f * wobble, y = Mathf.Sin(a) * f * wobble;
                        vertices.Add(new Vector3(x, y, 0.25f * (1f - f * f)));
                        uvs.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a) * f, 0.5f + 0.5f * Mathf.Sin(a) * f));
                    }
                }
                // Wound so the front faces +Z (0.10.3: lit shaders cull back faces; 0.10.2 faced -Z,
                // unseen because the particle shader drew both sides).
                for (int s = 0; s < Segments; s++)
                {
                    int n = (s + 1) % Segments;
                    tris.Add(0); tris.Add(1 + s); tris.Add(1 + n);
                }
                for (int r = 1; r < rings; r++)
                {
                    int inner = 1 + (r - 1) * Segments, outer = 1 + r * Segments;
                    for (int s = 0; s < Segments; s++)
                    {
                        int n = (s + 1) % Segments;
                        tris.Add(inner + s); tris.Add(outer + s); tris.Add(inner + n);
                        tris.Add(inner + n); tris.Add(outer + s); tris.Add(outer + n);
                    }
                }
                _stumpMesh = new Mesh { name = "RealTargetsStump", hideFlags = HideFlags.HideAndDontSave };
                _stumpMesh.SetVertices(vertices);
                _stumpMesh.SetUVs(0, uvs);
                _stumpMesh.SetTriangles(tris, 0);
                _stumpMesh.RecalculateNormals();
                _stumpMesh.RecalculateBounds();
                return _stumpMesh;
            }
        }

        /// <summary>
        /// Puts a stump cap at <paramref name="at"/> facing <paramref name="outward"/>, radius
        /// <paramref name="radius"/>, parented to <paramref name="parent"/> (so it moves with the body).
        /// </summary>
        internal static GameObject Stump(Transform parent, Vector3 at, Vector3 outward, float radius, int layer)
        {
            var material = StumpMaterial;
            if (material == null) return null;
            var go = new GameObject("RealTargetsStump") { layer = layer };
            go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(at, Quaternion.LookRotation(outward.normalized, Mathf.Abs(outward.normalized.y) < 0.95f ? Vector3.up : Vector3.forward) *
                                                    Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)));
            var lossy = parent != null ? parent.lossyScale : Vector3.one;
            go.transform.localScale = new Vector3(radius / Mathf.Max(lossy.x, 1e-4f), radius / Mathf.Max(lossy.y, 1e-4f), radius / Mathf.Max(lossy.z, 1e-4f));
            go.AddComponent<MeshFilter>().sharedMesh = StumpMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            Shade(r);
            return go;
        }

        /// <summary>A random flesh or skull gib mesh (lumpy blob or curved plate), unit size.</summary>
        internal static Mesh Gib(bool bone)
        {
            if (GibMeshes.Count == 0)
            {
                var rng = new System.Random(4242);
                for (int i = 0; i < 6; i++) GibMeshes.Add(Lump(rng, false));
                for (int i = 0; i < 4; i++) GibMeshes.Add(Lump(rng, true));
            }
            return bone ? GibMeshes[6 + Random.Range(0, 4)] : GibMeshes[Random.Range(0, 6)];
        }

        /// <summary>Low-poly lump: an octahedron subdivided once, pushed in and out (flesh), or flattened and bent (skull plate).</summary>
        private static Mesh Lump(System.Random rng, bool plate)
        {
            var dirs = new List<Vector3>
            {
                Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
                new Vector3(1, 1, 1).normalized, new Vector3(-1, 1, 1).normalized, new Vector3(1, -1, 1).normalized, new Vector3(1, 1, -1).normalized,
                new Vector3(-1, -1, 1).normalized, new Vector3(-1, 1, -1).normalized, new Vector3(1, -1, -1).normalized, new Vector3(-1, -1, -1).normalized,
            };
            var vertices = new List<Vector3>();
            var colors = new List<Color32>();
            foreach (var d in dirs)
            {
                float k = 0.35f + 0.25f * (float)rng.NextDouble();
                var v = d * k;
                if (plate)
                {
                    v.y *= 0.25f;
                    v.y += 0.18f * (v.x * v.x + v.z * v.z);
                }
                vertices.Add(v);
                byte shade = (byte)(150 + rng.Next(105));
                colors.Add(plate ? new Color32(235, 225, 200, 255) : new Color32(shade, (byte)(shade / 6), (byte)(shade / 7), 255));
            }
            // Triangles of the convex hull of these 14 points (cube corners + axis points).
            int[] tris =
            {
                0, 6, 8, 0, 8, 12, 0, 12, 9, 0, 9, 6,
                1, 7, 11, 1, 11, 13, 1, 13, 10, 1, 10, 7,
                2, 6, 9, 2, 9, 11, 2, 11, 7, 2, 7, 6,
                3, 8, 10, 3, 10, 13, 3, 13, 12, 3, 12, 8,
                4, 6, 7, 4, 7, 10, 4, 10, 8, 4, 8, 6,
                5, 9, 12, 5, 12, 13, 5, 13, 11, 5, 11, 9,
            };
            var mesh = new Mesh { name = plate ? "RealTargetsSkullGib" : "RealTargetsFleshGib", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            var uvs = new List<Vector2>();
            foreach (var v in vertices) uvs.Add(new Vector2(v.x + 0.5f, v.z + 0.5f));
            mesh.SetUVs(0, uvs);
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Cut surface: dark ragged skin rim, red muscle with fibres, darker vessel holes, a pale bone disc.</summary>
        private static Texture2D StumpTexture()
        {
            const int size = 128;
            var pixels = new Color[size * size];
            // Albedo for a lit shader (0.10.3, darker than 0.10.2's unlit colours): wet muscle is a deep
            // red, the bone end is dull and partly covered in blood.
            var muscle = new Color(0.36f, 0.035f, 0.03f);
            var dark = new Color(0.12f, 0.005f, 0.005f);
            var bone = new Color(0.55f, 0.47f, 0.38f);
            var skin = new Color(0.25f, 0.06f, 0.05f);
            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    float x = (i + 0.5f) / size - 0.5f, y = (j + 0.5f) / size - 0.5f;
                    float r = Mathf.Sqrt(x * x + y * y) * 2f, a = Mathf.Atan2(y, x);
                    float n = Mathf.PerlinNoise(x * 9f + 3f, y * 9f + 1f);
                    float fibre = Mathf.PerlinNoise(x * 3f + 7f, y * 40f + 2f);
                    var c = Color.Lerp(muscle * 0.75f, muscle * 1.15f, fibre) * (0.85f + 0.3f * n);
                    // Bone slightly off centre (spine at the back of a neck, humerus/femur in a limb).
                    float bx = x - 0.08f, by = y + 0.02f;
                    float b = Mathf.Sqrt(bx * bx + by * by) * 2f;
                    if (b < 0.17f)
                    {
                        c = Color.Lerp(bone, bone * 0.7f, b / 0.17f + 0.2f * n);
                        // Marrow and blood over the cut bone.
                        float smear = Mathf.PerlinNoise(x * 14f + 5f, y * 14f + 9f);
                        if (smear > 0.5f) c = Color.Lerp(c, dark * 1.6f, Mathf.InverseLerp(0.5f, 0.68f, smear));
                    }
                    else if (b < 0.22f) c = Color.Lerp(c, dark, 0.6f);
                    // Vessels and airway: dark holes.
                    float v1 = Mathf.Sqrt((x + 0.15f) * (x + 0.15f) + (y - 0.12f) * (y - 0.12f)) * 2f;
                    float v2 = Mathf.Sqrt((x + 0.15f) * (x + 0.15f) + (y + 0.14f) * (y + 0.14f)) * 2f;
                    float v3 = Mathf.Sqrt((x + 0.28f) * (x + 0.28f) + y * y) * 2f;
                    if (v1 < 0.07f || v2 < 0.07f) c = dark;
                    if (v3 < 0.11f) c = dark * 0.5f;
                    // Ragged skin rim, then transparent.
                    float rim = 0.86f + 0.08f * Mathf.Sin(a * 9f + 1.1f) + 0.05f * n;
                    float alpha = 1f;
                    if (r > rim) c = Color.Lerp(skin, dark, Mathf.InverseLerp(rim, 1f, r));
                    if (r > 0.97f) alpha = Mathf.Clamp01((1f - r) / 0.03f);
                    c.a = alpha;
                    pixels[j * size + i] = c;
                }
            }
            return Texture(size, pixels);
        }

        /// <summary>
        /// Gib surface albedo (0.10.3: lit shaders ignore the mesh's vertex colours): flesh is mottled
        /// deep red with darker clots; skull is dull bone with blood smears and a red inner side.
        /// </summary>
        private static Texture2D GibTexture(bool bone)
        {
            const int size = 64;
            var pixels = new Color[size * size];
            var flesh = new Color(0.33f, 0.03f, 0.025f);
            var clot = new Color(0.12f, 0.005f, 0.005f);
            var boneCol = new Color(0.52f, 0.45f, 0.37f);
            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    float n = Mathf.PerlinNoise(i * 0.15f + 2f, j * 0.15f + 5f);
                    float m = Mathf.PerlinNoise(i * 0.4f + 11f, j * 0.4f + 3f);
                    Color c;
                    if (bone)
                    {
                        c = boneCol * Mathf.Lerp(0.75f, 1f, n);
                        if (m > 0.55f) c = Color.Lerp(c, clot * 1.8f, Mathf.InverseLerp(0.55f, 0.7f, m));
                    }
                    else
                    {
                        c = Color.Lerp(flesh * 0.8f, flesh * 1.15f, n);
                        if (m > 0.62f) c = Color.Lerp(c, clot, Mathf.InverseLerp(0.62f, 0.75f, m));
                    }
                    c.a = 1f;
                    pixels[j * size + i] = c;
                }
            }
            return Texture(size, pixels);
        }

        /// <summary>
        /// Inside of a burst skull, u around the hole (wraps), v down from the rim: a broken bone edge with
        /// blood in its cracks, then torn scalp and brain (pinkish grey, dark veins, blood), then clot.
        /// </summary>
        private static Texture2D CraterTexture()
        {
            const int size = 128;
            var pixels = new Color[size * size];
            var bone = new Color(0.55f, 0.48f, 0.4f);
            var brain = new Color(0.38f, 0.22f, 0.21f);
            var blood = new Color(0.3f, 0.025f, 0.02f);
            var clot = new Color(0.1f, 0.005f, 0.005f);
            for (int j = 0; j < size; j++)
            {
                float vv = (j + 0.5f) / size;
                for (int i = 0; i < size; i++)
                {
                    float uu = (i + 0.5f) / size;
                    // Tileable in u: noise sampled on a circle.
                    float cx = Mathf.Cos(uu * 2f * Mathf.PI), cy = Mathf.Sin(uu * 2f * Mathf.PI);
                    float n = Mathf.PerlinNoise(cx * 2.5f + 10f + vv * 3f, cy * 2.5f + 10f);
                    float m = Mathf.PerlinNoise(cx * 6f + 30f, cy * 6f + 30f + vv * 9f);
                    float fold = Mathf.Abs(Mathf.Sin((cx * 7f + cy * 5f) * 3f + vv * 20f + n * 6f));
                    float edge = 0.1f + 0.08f * n;
                    Color c;
                    if (vv < edge)
                    {
                        c = bone * Mathf.Lerp(0.7f, 1f, m);
                        if (m > 0.6f) c = Color.Lerp(c, blood, Mathf.InverseLerp(0.6f, 0.75f, m));
                    }
                    else if (vv < 0.62f)
                    {
                        // Brain folds (sulci darker) under a film of blood that thickens toward the bottom.
                        c = Color.Lerp(brain * 0.6f, brain * 1.1f, fold) * (0.85f + 0.3f * n);
                        c = Color.Lerp(c, blood, Mathf.Clamp01(0.25f + (vv - edge) * 0.9f + (m - 0.5f)));
                    }
                    else c = Color.Lerp(blood, clot, Mathf.InverseLerp(0.62f, 1f, vv) * (0.7f + 0.3f * m));
                    c.a = 1f;
                    pixels[j * size + i] = c;
                }
            }
            var tex = Texture(size, pixels);
            tex.wrapModeU = TextureWrapMode.Repeat;
            return tex;
        }

        /// <summary>Brain tissue: pinkish grey with darker folds (sulci) and blood streaks.</summary>
        private static Texture2D BrainTexture()
        {
            const int size = 64;
            var pixels = new Color[size * size];
            var tissue = new Color(0.46f, 0.3f, 0.28f);
            var blood = new Color(0.28f, 0.02f, 0.02f);
            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    float x = i / (float)size, y = j / (float)size;
                    float n = Mathf.PerlinNoise(x * 5f + 4f, y * 5f + 8f);
                    float fold = Mathf.Abs(Mathf.Sin((x * 11f + Mathf.PerlinNoise(x * 3f, y * 3f) * 4f) * 3.1f + y * 7f));
                    var c = Color.Lerp(tissue * 0.55f, tissue * 1.05f, Mathf.SmoothStep(0f, 1f, fold)) * (0.9f + 0.2f * n);
                    float streak = Mathf.PerlinNoise(x * 2f + 20f, y * 9f + 3f);
                    if (streak > 0.6f) c = Color.Lerp(c, blood, Mathf.InverseLerp(0.6f, 0.78f, streak));
                    c.a = 1f;
                    pixels[j * size + i] = c;
                }
            }
            return Texture(size, pixels);
        }

        private static Texture2D Texture(int size, Color[] pixels)
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

        /// <summary>LOD0 skinned meshes of a player (lower LODs are enabled renderers too; the LODGroup only hides them).</summary>
        internal static List<SkinnedMeshRenderer> Lod0Skins(Player player)
        {
            var low = new HashSet<Renderer>();
            foreach (var group in player.GetComponentsInChildren<LODGroup>())
            {
                var lods = group.GetLODs();
                for (int l = 1; l < lods.Length; l++)
                {
                    foreach (var r in lods[l].renderers)
                    {
                        if (r != null) low.Add(r);
                    }
                }
            }
            var list = new List<SkinnedMeshRenderer>();
            foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr == null || !smr.enabled || smr.sharedMesh == null || low.Contains(smr) || smr.name.StartsWith("RealTargets")) continue;
                list.Add(smr);
            }
            return list;
        }
    }

    /// <summary>
    /// Tints an unlit alpha-blended renderer by the light where it is, a few times a second while
    /// visible (same idea as the wounds' colour pass): visible in light, dark in the dark.
    /// </summary>
    internal sealed class ShadeByLight : MonoBehaviour
    {
        private static readonly int TintId = Shader.PropertyToID("_TintColor");
        private Renderer _renderer;
        private MaterialPropertyBlock _block;
        private float _alpha, _next;

        internal void Init(Renderer renderer, float alpha)
        {
            _renderer = renderer;
            _alpha = alpha;
            _block = new MaterialPropertyBlock();
            Refresh();
        }

        private void Update()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Gore);
            if (_renderer == null || Time.time < _next) return;
            _next = Time.time + Random.Range(0.2f, 0.35f);
            if (_renderer.isVisible) Refresh();
        }

        private void Refresh()
        {
            if (_renderer == null) return;
            float light = SceneLight.At(transform.position, transform.forward);
            float t = 0.5f * SceneLight.Unlit(light) * 1.6f;
            _renderer.GetPropertyBlock(_block);
            _block.SetColor(TintId, new Color(t, t, t, 0.5f * _alpha));
            _renderer.SetPropertyBlock(_block);
        }
    }

    /// <summary>
    /// A physics piece (gib, thrown helmet, severed limb): registered with EFT's script-stepped
    /// physics while it moves, frozen once it has landed and lies still; leaves a blood splat where it
    /// first lands on the world.
    /// 0.10.3 (0.10.2 test: a grenade-severed calf "standing without physics"): the piece spawns inside
    /// its own body (the blast kills the bot, but the corpse is still standing for a few frames), was
    /// wedged between the floor and the knee's colliders, read as "still" and was frozen in place.
    /// Now it ignores every collider of its body (again at 0.3 s and 1.5 s, when the corpse's ragdoll
    /// colliders are live), and only freezes after touching the static world. Splats only on the
    /// static world (0.10.2: gib-on-gib or gib-on-corpse contacts left marks in mid-air).
    /// </summary>
    internal sealed class GorePiece : MonoBehaviour
    {
        private Rigidbody _body;
        private Collider[] _own;
        private Player _owner;
        private float _since, _stillSince = -1f, _landedAt = -1f;
        private int _ignorePass;
        private bool _splatted, _frozen, _reportedStuck;
        private Vector3 _start;
        internal bool Bloody = true;
        internal string Label;

        // Wet tissue sticks to walls and ceilings (0.10.6, Stompa): stuck for a few seconds, sliding down and
        // smearing, then it drops. Chance per piece (0 = never: helmets, limbs).
        internal float StickChance;
        private bool _stickTried, _stuck;
        private Vector3 _stuckNormal, _slideDir, _lastSmear;
        private float _stuckUntil, _slideSpeed;
        private int _smears;
        private const int MaxSmears = 20;
        internal static int StuckCount { get; private set; }
        /// <summary>Pieces still under physics (for the [Perf] log).</summary>
        internal static int Moving { get; private set; }
        private bool _counted;

        internal void Init(Rigidbody body, Player owner = null)
        {
            _body = body;
            _owner = owner;
            _own = GetComponentsInChildren<Collider>();
            _since = Time.time;
            _start = transform.position;
            _counted = true;
            Moving++;
            IgnoreOwner();
            PhysicsExtensions.UpdateController.SupportRigidbody(body, 0f);
        }

        private void IgnoreOwner()
        {
            if (_owner == null || _own == null) return;
            foreach (var theirs in OwnerColliders(_owner))
            {
                if (theirs == null) continue;
                foreach (var mine in _own)
                {
                    if (mine != null) Physics.IgnoreCollision(mine, theirs, true);
                }
            }
        }

        private static Player _cachedOwner;
        private static int _cachedFrame = -1;
        private static Collider[] _cached;

        /// <summary>The body's colliders (hit colliders, ragdoll, character controller), cached per frame for a burst of gibs.</summary>
        private static Collider[] OwnerColliders(Player owner)
        {
            if (_cachedOwner != owner || _cachedFrame != Time.frameCount)
            {
                _cachedOwner = owner;
                _cachedFrame = Time.frameCount;
                _cached = owner.GetComponentsInChildren<Collider>(true);
            }
            return _cached;
        }

        private void Update()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Gore);
            if (_frozen || _body == null) return;
            if (_stuck)
            {
                Slide();
                return;
            }
            float age = Time.time - _since;
            if ((_ignorePass == 0 && age > 0.3f) || (_ignorePass == 1 && age > 1.5f))
            {
                _ignorePass++;
                IgnoreOwner();
            }

            bool still = _body.velocity.sqrMagnitude < 0.01f && _body.angularVelocity.sqrMagnitude < 0.05f;
            if (still && _stillSince < 0f) _stillSince = Time.time;
            if (!still) _stillSince = -1f;
            bool resting = _landedAt >= 0f && _stillSince >= 0f && Time.time - _stillSince > 0.6f;

            if (!_reportedStuck && _landedAt < 0f && _stillSince >= 0f && Time.time - _stillSince > 1f)
            {
                // Still without ever touching the world: wedged on something. Say what, then kick it free.
                _reportedStuck = true;
                if (!string.IsNullOrEmpty(Label) && Plugin.LogBlood.Value)
                {
                    Plugin.Log.LogInfo($"[Gore] {Label}: piece still in the air {Vector3.Distance(_start, transform.position):F2} m from its start; kicked loose");
                }
                _body.WakeUp();
                _body.velocity = Random.onUnitSphere * 1.5f + Vector3.up;
            }

            if (resting || age > 20f)
            {
                _frozen = true;
                if (_counted) Moving--;
                PhysicsExtensions.UpdateController.UnsupportRigidbody(_body);
                _body.isKinematic = true;
                PhysicsExtensions.UpdateController.CheckRigidbodiesDone();
                if (!string.IsNullOrEmpty(Label) && Plugin.LogBlood.Value)
                {
                    Plugin.Log.LogInfo($"[Gore] {Label}: piece at rest after {age:F1} s, {Vector3.Distance(_start, transform.position):F2} m from where it came off" +
                                       (_landedAt < 0f ? " (never touched the world: timed out)" : ""));
                }
            }
        }

        /// <summary>Static world: no rigidbody, or a kinematic one (doors); never a corpse, a gib or a bot.</summary>
        private static bool IsWorld(Collision collision)
        {
            var rb = collision.rigidbody;
            if (rb != null && !rb.isKinematic) return false;
            int layer = collision.collider.gameObject.layer;
            return layer != LayersMaskController.DeadbodyLayer && layer != LayersMaskController.HitColliderLayer &&
                   collision.collider.GetComponentInParent<Player>() == null;
        }

        private void OnCollisionEnter(Collision collision) => Touch(collision);

        private void OnCollisionStay(Collision collision)
        {
            if (_landedAt < 0f) Touch(collision);
        }

        private void Touch(Collision collision)
        {
            if (collision.contactCount == 0 || !IsWorld(collision)) return;
            if (_landedAt < 0f) _landedAt = Time.time;
            if (!_stickTried && StickChance > 0f)
            {
                _stickTried = true;
                var contact = collision.GetContact(0);
                bool wall = Mathf.Abs(contact.normal.y) < 0.7f, ceiling = contact.normal.y < -0.6f;
                // 0.10.14: never on a door or window (it would hang in the air once that opens or breaks).
                if ((wall || ceiling) && Random.value < StickChance && !GroundBlood.Movable(collision.collider)) StickNow(contact.point, contact.normal);
            }
            if (_splatted || !Bloody) return;
            _splatted = true;
            var c = collision.GetContact(0);
            // The contact is on the physics collider, which may sit off the visible surface: put the
            // splat on the drawn (ballistic) surface right there, or nowhere.
            if (Physics.Raycast(c.point + c.normal * 0.15f, -c.normal, out var hit, 0.3f, LayersMaskController.HighPolyWithTerrainMask) &&
                !GroundBlood.Movable(hit.collider))
            {
                GroundBlood.Instance?.Splat(hit.point, hit.normal, _body != null ? _body.velocity : Vector3.down);
            }
        }

        /// <summary>
        /// Sticks the piece to the surface at <paramref name="point"/> (outward <paramref name="normal"/>): held
        /// there kinematic, sliding down a wall at 1-3.5 cm/s for 2-10 s (a ceiling: 1-4 s in place), then it drops.
        /// </summary>
        internal void StickNow(Vector3 point, Vector3 normal)
        {
            if (_body == null || _frozen) return;
            _stickTried = true;
            _stuck = true;
            StuckCount++;
            _splatted = true;
            _stuckNormal = normal.normalized;
            bool ceiling = _stuckNormal.y < -0.6f;
            _slideDir = ceiling ? Vector3.zero : Vector3.ProjectOnPlane(Vector3.down, _stuckNormal).normalized;
            _slideSpeed = ceiling ? 0f : Random.Range(0.01f, 0.035f);
            _stuckUntil = Time.time + (ceiling ? Random.Range(1f, 4f) : Random.Range(2f, 10f));
            _body.velocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
            PhysicsExtensions.UpdateController.UnsupportRigidbody(_body);
            _body.isKinematic = true;
            transform.position = point + _stuckNormal * Radius() * 0.5f;
            _lastSmear = point;
            if (Bloody) GroundBlood.Instance?.Splat(point, _stuckNormal, Vector3.zero);
        }

        private float Radius() => Mathf.Max(0.005f, transform.lossyScale.x * 0.45f);

        private void Slide()
        {
            if (Time.time < _stuckUntil && _slideSpeed > 0f)
            {
                var next = transform.position + _slideDir * (_slideSpeed * Time.deltaTime);
                // Follow the surface; past an edge (nothing behind) it falls.
                if (Physics.Raycast(next + _stuckNormal * 0.1f, -_stuckNormal, out var hit, 0.25f, LayersMaskController.HighPolyWithTerrainMask) &&
                    Mathf.Abs(hit.normal.y) < 0.7f && !GroundBlood.Movable(hit.collider))
                {
                    _stuckNormal = hit.normal;
                    _slideDir = Vector3.ProjectOnPlane(Vector3.down, _stuckNormal).normalized;
                    transform.position = hit.point + _stuckNormal * Radius() * 0.5f;
                    if (Bloody && _smears < MaxSmears && (hit.point - _lastSmear).sqrMagnitude > 0.02f * 0.02f)
                    {
                        _smears++;
                        _lastSmear = hit.point;
                        GroundBlood.Instance?.Smear(hit.point, _stuckNormal, _slideDir, Radius() * 2.2f);
                    }
                    return;
                }
            }
            else if (Time.time < _stuckUntil) return;

            // Drop off.
            _stuck = false;
            StuckCount--;
            _stillSince = -1f;
            // Falls again from here: the rest/timeout clock restarts, and it must land again to freeze.
            _since = Time.time;
            _landedAt = -1f;
            _reportedStuck = true;
            _body.isKinematic = false;
            PhysicsExtensions.UpdateController.SupportRigidbody(_body, 0f);
            _body.WakeUp();
            _body.velocity = _stuckNormal * 0.2f + _slideDir * 0.2f;
        }

        private void OnDestroy()
        {
            if (_stuck) StuckCount--;
            if (!_frozen && _counted) Moving--;
            if (!_frozen && _body != null) PhysicsExtensions.UpdateController.UnsupportRigidbody(_body);
        }
    }
}
