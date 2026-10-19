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
        private static Material _stump, _flesh, _bone, _crater, _brain, _neck, _limbCut;
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
            var fleshTex = GibTexture(false);
            var boneTex = GibTexture(true);
            var craterTex = CraterTexture();
            var brainTex = BrainTexture();
            var neckTex = CutTexture(StumpKind.Neck);
            var limbTex = CutTexture(StumpKind.Limb);
            var stumpTex = limbTex;
            if (LitBlood.HasOpaque)
            {
                Lit = true;
                _neck = LitBlood.Opaque(neckTex, Color.white, 0.45f, "RealTargetsNeckCut");
                _limbCut = LitBlood.Opaque(limbTex, Color.white, 0.45f, "RealTargetsLimbCut");
                _stump = LitBlood.Opaque(stumpTex, Color.white, 0.8f, "RealTargetsStump");
                _flesh = LitBlood.Opaque(fleshTex, Color.white, 0.75f, "RealTargetsGib");
                _bone = LitBlood.Opaque(boneTex, Color.white, 0.35f, "RealTargetsGibBone");
                _crater = LitBlood.Opaque(craterTex, Color.white, 0.85f, "RealTargetsCrater");
                _brain = LitBlood.Opaque(brainTex, Color.white, 0.9f, "RealTargetsBrain");
            }
            else
            {
                _neck = Unlit(neckTex, "RealTargetsNeckCut");
                _limbCut = Unlit(limbTex, "RealTargetsLimbCut");
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
                var vertices = new List<Vector3> { new Vector3(0f, 0f, 0.08f) };
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
                        vertices.Add(new Vector3(x, y, 0.08f * (1f - f * f)));
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

        /// <summary>The cut surface material for a neck or a limb (cross-section texture, 1.1.0 gore pass).</summary>
        internal static Material CutMaterial(StumpKind kind)
        {
            MakeMaterials();
            return kind == StumpKind.Neck ? _neck : _limbCut;
        }

        /// <summary>
        /// The largest connected run of a cut's open edges (by length): the one loop around the neck or limb, without small
        /// loops elsewhere on the skin (gore build test: a 2.3 cm loop got the severed thigh's cap, the trouser end stayed
        /// open). Empty when there are none.
        /// </summary>
        /// <summary>How many separate open edges (connected loops) a cut left on one skin, for the log (more than one = a hole or an island).</summary>
        internal static int LoopCount(List<(Vector3 a, Vector3 b)> edges)
        {
            if (edges == null || edges.Count == 0) return 0;
            var parent = new Dictionary<long, long>();
            long Find(long x)
            {
                while (parent.TryGetValue(x, out long p) && p != x)
                {
                    parent.TryGetValue(p, out long gp);
                    parent[x] = gp;
                    x = p;
                }
                return x;
            }
            foreach (var (a, b) in edges)
            {
                long ka = WeldKey(a), kb = WeldKey(b);
                if (!parent.ContainsKey(ka)) parent[ka] = ka;
                if (!parent.ContainsKey(kb)) parent[kb] = kb;
                long ra = Find(ka), rb = Find(kb);
                if (ra != rb) parent[ra] = rb;
            }
            int roots = 0;
            foreach (var key in new List<long>(parent.Keys))
            {
                if (Find(key) == key) roots++;
            }
            return roots;
        }

        internal static List<(Vector3 a, Vector3 b)> LargestLoop(List<(Vector3 a, Vector3 b)> edges, out float length)
        {
            length = 0f;
            var best = new List<(Vector3, Vector3)>();
            if (edges == null || edges.Count == 0) return best;
            var index = new Dictionary<long, int>();
            int Point(Vector3 p)
            {
                long k = WeldKey(p);
                if (!index.TryGetValue(k, out int i)) index[k] = i = index.Count;
                return i;
            }
            var ends = new (int, int)[edges.Count];
            var byPoint = new Dictionary<int, List<int>>();
            for (int e = 0; e < edges.Count; e++)
            {
                ends[e] = (Point(edges[e].a), Point(edges[e].b));
                foreach (int p in new[] { ends[e].Item1, ends[e].Item2 })
                {
                    if (!byPoint.TryGetValue(p, out var list)) byPoint[p] = list = new List<int>(2);
                    list.Add(e);
                }
            }
            var seen = new bool[edges.Count];
            for (int s = 0; s < edges.Count; s++)
            {
                if (seen[s]) continue;
                var loop = new List<(Vector3, Vector3)>();
                float sum = 0f;
                var stack = new Stack<int>();
                stack.Push(s);
                seen[s] = true;
                while (stack.Count > 0)
                {
                    int e = stack.Pop();
                    loop.Add(edges[e]);
                    sum += Vector3.Distance(edges[e].a, edges[e].b);
                    foreach (int p in new[] { ends[e].Item1, ends[e].Item2 })
                    {
                        foreach (int n in byPoint[p])
                        {
                            if (seen[n]) continue;
                            seen[n] = true;
                            stack.Push(n);
                        }
                    }
                }
                if (sum > length)
                {
                    length = sum;
                    best = loop;
                }
            }
            return best;
        }

        private static long WeldKey(Vector3 p) =>
            ((long)(Mathf.RoundToInt(p.x * 1000f) & 0xFFFFF) << 40) | ((long)(Mathf.RoundToInt(p.y * 1000f) & 0xFFFFF) << 20) |
            (long)(Mathf.RoundToInt(p.z * 1000f) & 0xFFFFF);

        /// <summary>
        /// Closes a cut on its own open edge (1.1.0 gore pass; SkinCut.Ring): every ring edge is joined through three inner rings to a
        /// centre, so the lid follows the skin exactly instead of a round disc at the joint (the 1.1.0 neck cap hovered above
        /// a cut inside a collar and read as a red ball). The rim stays on the edge, a lip just inside it sinks a little
        /// (skin retracts), the middle bulges a few mm with noise (tissue pushed out), so it shades unevenly and never reads
        /// as a dome. UVs: the texture's circle mapped onto the ring by angle and fraction of the rim's own radius, its +y
        /// toward <paramref name="front"/> (neck: trachea at the front, spine at the back). Only the largest connected loop
        /// of the edges is used (LargestLoop); points further than 3 x its median radius are dropped. Built in <paramref name="parent"/>'s space and
        /// parented to it; the bone stub is a child. Null when the ring is too small or too broken (< 6 edges).
        /// </summary>
        /// <summary>Degrees between the last cap's own plane normal and the outward it was given (> 20 = built on its own plane).</summary>
        internal static float LastCapTilt;

        internal static GameObject RingCap(Transform parent, List<(Vector3 a, Vector3 b)> edges, Vector3 outward, Vector3 front, StumpKind kind,
            int layer, out Vector3 centre, out float radius, bool stub = true)
        {
            centre = default;
            radius = 0f;
            var material = CutMaterial(kind);
            edges = LargestLoop(edges, out _);
            if (material == null || parent == null || edges.Count < 6) return null;
            var n = outward.normalized;

            var index = new Dictionary<long, int>();
            var points = new List<Vector3>();
            var pairs = new List<(int, int)>(edges.Count);
            int Weld(Vector3 p)
            {
                long k = WeldKey(p);
                if (index.TryGetValue(k, out int i)) return i;
                i = points.Count;
                index[k] = i;
                points.Add(p);
                return i;
            }
            foreach (var (a, b) in edges)
            {
                int ia = Weld(a), ib = Weld(b);
                if (ia != ib) pairs.Add((ia, ib));
            }
            if (points.Count < 6) return null;

            var c = Vector3.zero;
            foreach (var p in points) c += p;
            c /= points.Count;

            // Close the cut across its own plane when the edge runs far from square to the limb (gore-3 test: a shoulder cut
            // goes from the armpit up over the deltoid; across the arm's axis the cap stood in steep walls whose faces, culled
            // from the side, showed as wedge-shaped holes in the pit). The plane: the edges' fan normals, all turned outward.
            var plane = Vector3.zero;
            foreach (var (ia, ib) in pairs)
            {
                var x = Vector3.Cross(points[ia] - c, points[ib] - c);
                plane += Vector3.Dot(x, n) >= 0f ? x : -x;
            }
            LastCapTilt = plane.sqrMagnitude > 1e-12f ? Vector3.Angle(plane, n) : 0f;
            if (LastCapTilt > 20f) n = plane.normalized;
            var radial = new float[points.Count];
            var sorted = new List<float>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                radial[i] = Vector3.ProjectOnPlane(points[i] - c, n).magnitude;
                sorted.Add(radial[i]);
            }
            sorted.Sort();
            float median = sorted[sorted.Count / 2];
            var keep = new bool[points.Count];
            var kc = Vector3.zero;
            int kept = 0;
            for (int i = 0; i < points.Count; i++)
            {
                keep[i] = radial[i] <= median * 3f + 0.01f;
                if (!keep[i]) continue;
                kc += points[i];
                kept++;
            }
            if (kept < 6) return null;
            c = kc / kept;
            sorted.Clear();
            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i]) sorted.Add(Vector3.ProjectOnPlane(points[i] - c, n).magnitude);
            }
            sorted.Sort();
            float r = sorted[Mathf.Min(sorted.Count - 1, Mathf.FloorToInt(sorted.Count * 0.8f))];
            if (r < 0.01f) return null;

            var f = Vector3.ProjectOnPlane(front, n);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.forward, n);
            f.Normalize();
            var right = Vector3.Cross(f, n).normalized;

            float s1 = Random.Range(0f, 50f), s2 = Random.Range(0f, 50f);
            float[] ts = { 1f, 0.86f, 0.6f, 0.3f };
            var world = new List<Vector3> { c + n * (r * (0.05f + 0.03f * Random.value)) };
            var uvs = new List<Vector2> { new Vector2(0.5f, 0.5f) };
            var first = new int[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                first[i] = -1;
                if (!keep[i]) continue;
                var rel = points[i] - c;
                float h = Vector3.Dot(rel, n);
                var planar = rel - n * h;
                float a = Mathf.Atan2(Vector3.Dot(planar, f), Vector3.Dot(planar, right));
                float noise = Mathf.PerlinNoise(Mathf.Cos(a) * 1.7f + s1, Mathf.Sin(a) * 1.7f + s2);
                first[i] = world.Count;
                for (int k = 0; k < ts.Length; k++)
                {
                    float t = ts[k];
                    Vector3 p;
                    if (k == 0) p = points[i];
                    else if (k == 1) p = c + planar * t + n * (h * 0.9f - r * (0.03f + 0.02f * noise));
                    else p = c + planar * t + n * (h * t + r * (k == 2 ? 0.04f : 0.07f) + r * 0.07f * (noise - 0.4f));
                    world.Add(p);
                    uvs.Add(new Vector2(0.5f + 0.5f * t * Mathf.Cos(a), 0.5f + 0.5f * t * Mathf.Sin(a)));
                }
            }

            var triangles = new List<int>(pairs.Count * 21);
            void Tri(int x, int y, int z)
            {
                var normal = Vector3.Cross(world[y] - world[x], world[z] - world[x]);
                triangles.Add(x);
                if (Vector3.Dot(normal, n) >= 0f)
                {
                    triangles.Add(y);
                    triangles.Add(z);
                }
                else
                {
                    triangles.Add(z);
                    triangles.Add(y);
                }
            }
            int edgesUsed = 0;
            foreach (var (ia, ib) in pairs)
            {
                if (first[ia] < 0 || first[ib] < 0) continue;
                int va = first[ia], vb = first[ib];
                for (int k = 0; k < ts.Length - 1; k++)
                {
                    Tri(va + k, vb + k, vb + k + 1);
                    Tri(va + k, vb + k + 1, va + k + 1);
                }
                Tri(va + ts.Length - 1, vb + ts.Length - 1, 0);
                edgesUsed++;
            }
            if (edgesUsed < 6) return null;

            // Both faces: a ragged rim twists parts of the cap away from a side view, where one-sided they'd be culled into holes.
            int front1 = world.Count, faces = triangles.Count;
            for (int i = 0; i < front1; i++)
            {
                world.Add(world[i]);
                uvs.Add(uvs[i]);
            }
            for (int t = 0; t + 2 < faces; t += 3)
            {
                triangles.Add(triangles[t] + front1);
                triangles.Add(triangles[t + 2] + front1);
                triangles.Add(triangles[t + 1] + front1);
            }

            var local = new List<Vector3>(world.Count);
            foreach (var p in world) local.Add(parent.InverseTransformPoint(p));
            var mesh = new Mesh { name = "RealTargetsCutCap" };
            mesh.SetVertices(local);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            var go = new GameObject("RealTargetsStump") { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            Shade(renderer);
            go.AddComponent<OwnedMesh>().Mesh = mesh;

            // The bone end, standing out of the cut: a limb's long bone a little off centre, the neck's vertebra at the back
            // (where the texture draws them, so the stub sits on its own cross-section).
            var surface = c + n * (r * 0.06f);
            if (!stub)
            {
                // An inner layer sticking out past the main cap: closed, no second bone.
            }
            else if (kind == StumpKind.Neck)
            {
                BoneStub(go.transform, surface - f * (r * 0.38f), (n - f * 0.15f).normalized, r * 0.17f, r * Random.Range(0.22f, 0.4f), layer);
            }
            else
            {
                var tilt = Random.insideUnitSphere * 0.2f;
                BoneStub(go.transform, surface + right * (r * 0.05f) - f * (r * 0.02f), (n + tilt).normalized, r * 0.2f, r * Random.Range(0.35f, 0.75f), layer);
            }

            centre = c;
            radius = r;
            return go;
        }

        /// <summary>
        /// A broken bone end: a 7-sided tube of <paramref name="radius"/> (sides jittered) from below the cut surface up
        /// to a jagged top 55-100 % of <paramref name="length"/> above it, closed with a recessed (marrow) centre.
        /// Vertices in <paramref name="parent"/>'s space (the cap's, which is its bone's).
        /// </summary>
        internal static GameObject BoneStub(Transform parent, Vector3 at, Vector3 axis, float radius, float length, int layer)
        {
            var material = GibMaterial(true);
            if (material == null || parent == null) return null;
            const int sides = 7;
            axis = axis.normalized;
            var u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.forward).normalized;
            var v = Vector3.Cross(axis, u);
            var world = new List<Vector3>(sides * 2 + 1);
            var uvs = new List<Vector2>(sides * 2 + 1);
            for (int s = 0; s < sides; s++)
            {
                float a = s / (float)sides * Mathf.PI * 2f;
                var dir = u * Mathf.Cos(a) + v * Mathf.Sin(a);
                float rr = radius * Random.Range(0.85f, 1.15f);
                world.Add(at + dir * rr - axis * (length * 0.3f));
                uvs.Add(new Vector2(s / (float)sides, 0f));
                world.Add(at + dir * rr * Random.Range(0.8f, 1f) + axis * (length * Random.Range(0.55f, 1f)));
                uvs.Add(new Vector2(s / (float)sides, 1f));
            }
            world.Add(at + axis * (length * 0.45f));
            uvs.Add(new Vector2(0.5f, 0.5f));
            int top = world.Count - 1;

            var tris = new List<int>(sides * 9);
            void Tri(int x, int y, int z, Vector3 facing)
            {
                var normal = Vector3.Cross(world[y] - world[x], world[z] - world[x]);
                tris.Add(x);
                if (Vector3.Dot(normal, facing) >= 0f)
                {
                    tris.Add(y);
                    tris.Add(z);
                }
                else
                {
                    tris.Add(z);
                    tris.Add(y);
                }
            }
            for (int s = 0; s < sides; s++)
            {
                int n = (s + 1) % sides;
                int b0 = s * 2, t0 = s * 2 + 1, b1 = n * 2, t1 = n * 2 + 1;
                var side = (world[b0] + world[b1]) * 0.5f - at;
                side = Vector3.ProjectOnPlane(side, axis);
                Tri(b0, b1, t1, side);
                Tri(b0, t1, t0, side);
                Tri(t0, t1, top, axis);
            }

            var local = new List<Vector3>(world.Count);
            foreach (var p in world) local.Add(parent.InverseTransformPoint(p));
            var mesh = new Mesh { name = "RealTargetsBoneStub" };
            mesh.SetVertices(local);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("RealTargetsBoneStub") { layer = layer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            Shade(r);
            go.AddComponent<OwnedMesh>().Mesh = mesh;
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

        /// <summary>
        /// A cross-section through a neck or a limb (1.1.0 gore pass, replaces the 0.10.3 stump texture that read as one red disc from
        /// a distance). Texture circle = the cut's rim, +y = front. Muscle in bundles split by pale fascia, a ragged fat layer
        /// and a blood-soaked skin edge at the rim, clots and a blood film breaking up the red; dark, desaturated albedo
        /// (the lit shader adds the wet highlight). Neck: vertebral body and spinal cord at the back, trachea (cartilage ring,
        /// black airway) and oesophagus at the front, carotids and jugulars at the sides. Limb: the long bone just off
        /// centre (cortex, marrow) with its artery, veins and nerve beside it.
        /// </summary>
        private static Texture2D CutTexture(StumpKind kind)
        {
            const int size = 256;
            var pixels = new Color[size * size];
            var muscleA = new Color(0.17f, 0.024f, 0.021f);
            var muscleB = new Color(0.23f, 0.034f, 0.029f);
            var clot = new Color(0.05f, 0.007f, 0.007f);
            var fat = new Color(0.3f, 0.24f, 0.14f);
            var fascia = new Color(0.24f, 0.16f, 0.14f);
            var bone = new Color(0.44f, 0.39f, 0.32f);
            var marrow = new Color(0.2f, 0.045f, 0.03f);
            var skin = new Color(0.12f, 0.022f, 0.02f);
            var cartilage = new Color(0.4f, 0.36f, 0.32f);
            var hole = new Color(0.025f, 0.008f, 0.008f);
            var vessel = new Color(0.26f, 0.06f, 0.05f);
            var cord = new Color(0.38f, 0.31f, 0.28f);
            bool neck = kind == StumpKind.Neck;

            float Disc(float x, float y, float cx, float cy, float sx, float sy) =>
                Mathf.Sqrt((x - cx) * (x - cx) / (sx * sx) + (y - cy) * (y - cy) / (sy * sy));

            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    float x = (i + 0.5f) / size * 2f - 1f, y = (j + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(x * x + y * y), a = Mathf.Atan2(y, x);
                    float n = Mathf.PerlinNoise(x * 3f + 3f, y * 3f + 1f);
                    float m = Mathf.PerlinNoise(x * 7f + 11f, y * 7f + 5f);
                    float fine = Mathf.PerlinNoise(x * 22f + 2f, y * 22f + 9f);

                    // Muscle bundles cut across: stippled fibres, pale fascia between the bundles.
                    float wx = x + 0.3f * (n - 0.5f), wy = y + 0.3f * (m - 0.5f);
                    float cell = Mathf.Abs(Mathf.Sin(wx * 8.5f + Mathf.Sin(wy * 6.5f) * 1.4f)) * Mathf.Abs(Mathf.Sin(wy * 7.5f + Mathf.Sin(wx * 5.5f) * 1.3f));
                    var c = Color.Lerp(muscleA, muscleB, fine) * (0.82f + 0.3f * n);
                    if (cell < 0.07f) c = Color.Lerp(c, fascia, 0.55f * (1f - cell / 0.07f));

                    if (neck)
                    {
                        // Vertebral body (spongy bone), spinal canal with the cord, the spinous process behind.
                        float vb = Disc(x, y, 0f, -0.38f, 0.21f, 0.17f);
                        if (vb < 1f) c = Color.Lerp(bone, bone * 0.72f, Mathf.PerlinNoise(x * 30f, y * 30f)) * (vb > 0.8f ? 0.85f : 1f);
                        float canal = Disc(x, y, 0f, -0.62f, 0.09f, 0.075f);
                        if (canal < 1f) c = canal < 0.7f ? Color.Lerp(cord, cord * 0.75f, fine) : hole * 2f;
                        float spine = Disc(x, y, 0f, -0.8f, 0.07f, 0.1f);
                        if (spine < 1f) c = bone * 0.85f;
                        // Trachea: cartilage ring around a black airway; oesophagus flattened behind it.
                        float tr = Disc(x, y, 0f, 0.55f, 0.2f, 0.17f);
                        if (tr < 1f) c = tr < 0.66f ? hole : Color.Lerp(cartilage, cartilage * 0.8f, fine);
                        float oe = Disc(x, y, 0f, 0.27f, 0.11f, 0.05f);
                        if (oe < 1f) c = oe < 0.45f ? hole * 2f : new Color(0.3f, 0.08f, 0.07f);
                        // Carotids (thick walls, open) and jugulars (collapsed), both sides.
                        for (int side = -1; side <= 1; side += 2)
                        {
                            float ca = Disc(x, y, side * 0.42f, 0.24f, 0.065f, 0.065f);
                            if (ca < 1f) c = ca < 0.55f ? hole * 3f : vessel;
                            float ju = Disc(x, y, side * 0.56f, 0.13f, 0.09f, 0.055f);
                            if (ju < 1f) c = hole * 3.5f;
                        }
                    }
                    else
                    {
                        // Long bone (cortex ring, marrow) just off centre; artery, veins, nerve beside it.
                        float b = Disc(x, y, 0.05f, -0.03f, 0.23f, 0.21f);
                        if (b < 1f) c = b < 0.6f ? Color.Lerp(marrow, marrow * 0.6f, fine) : Color.Lerp(bone, bone * 0.75f, Mathf.PerlinNoise(x * 30f, y * 30f));
                        float art = Disc(x, y, -0.27f, 0.2f, 0.05f, 0.05f);
                        if (art < 1f) c = art < 0.55f ? hole * 3f : vessel;
                        float vein = Disc(x, y, -0.36f, 0.12f, 0.065f, 0.045f);
                        if (vein < 1f) c = hole * 3.5f;
                        float nerve = Disc(x, y, -0.2f, 0.3f, 0.035f, 0.035f);
                        if (nerve < 1f) c = cord * 0.9f;
                    }

                    // Fat under a ragged skin edge at the rim.
                    float rim = 0.95f - 0.035f * Mathf.PerlinNoise(Mathf.Cos(a) * 3f + 7f, Mathf.Sin(a) * 3f + 7f);
                    if (r > rim - 0.08f) c = Color.Lerp(c, fat * (0.8f + 0.3f * fine), 0.8f);
                    if (r > rim) c = skin * (0.85f + 0.3f * n);

                    // Clots and a film of blood over the whole surface: no clean paint-red anywhere.
                    float blood = Mathf.PerlinNoise(x * 2.6f + 40f, y * 2.6f + 40f);
                    float blot = Mathf.PerlinNoise(x * 9f + 60f, y * 9f + 20f);
                    if (blood > 0.5f) c = Color.Lerp(c, clot, Mathf.InverseLerp(0.5f, 0.68f, blood) * 0.9f);
                    if (blot > 0.68f) c = Color.Lerp(c, clot, 0.7f);
                    c = Color.Lerp(c, muscleA * 0.7f, 0.18f);
                    c.a = 1f;
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
            var boneCol = new Color(0.42f, 0.37f, 0.3f);
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
        internal static List<SkinnedMeshRenderer> Lod0Skins(Component player)
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
        // Gore-only mode (Visuals > Enabled off): the piece flies, but leaves no blood marks.
        private bool Marks => Bloody && Plugin.VisualsEnabled.Value;
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

        // A severed limb keeps bleeding out of its cut end (gore retest, Stompa 2026-10-18: "it should bleed and pool a
        // little after landing"): a thin trail of drops in flight, then once it lies still it drains what the limb holds
        // (half-life 10 s, slower with the cut facing up) into a small pool under the cut end, with drops falling into it.
        private bool _bleeds;
        private Vector3 _cutLocal, _cutOutwardLocal;
        private float _bloodMl, _drainedMl, _nextDrop, _nextPool;
        private const float DrainHalfLife = 10f;

        /// <summary>Makes this piece bleed from its cut end (local to the piece) with <paramref name="ml"/> to lose.</summary>
        internal void BleedFromCut(Vector3 localPoint, Vector3 localOutward, float ml)
        {
            _bleeds = ml > 0f;
            _cutLocal = localPoint;
            _cutOutwardLocal = localOutward.normalized;
            _bloodMl = ml;
        }

        private void Bleed()
        {
            if (!_bleeds || !Marks || !Plugin.VisualsDrips.Value) return;
            float now = Time.time;
            if (now < _nextDrop) return;
            var cut = transform.TransformPoint(_cutLocal);
            var outward = transform.TransformDirection(_cutOutwardLocal);
            var blood = BloodParticles.Instance;

            if (!_frozen)
            {
                // In flight: a short trail while the cut still pours (first 1.5 s), nothing after.
                if (now - _since > 1.5f || _landedAt >= 0f) return;
                _nextDrop = now + 0.07f;
                var v = _body != null ? _body.velocity * 0.6f : Vector3.zero;
                blood?.Emit(cut + outward * 0.01f, v + outward * 1.2f, 25f, 2, 0.01f);
                return;
            }

            // Lying still: drain by gravity. A cut facing down pours, one facing up only seeps over its rim.
            float left = _bloodMl - _drainedMl;
            if (left < Mathf.Max(1f, _bloodMl * 0.05f))
            {
                _bleeds = false;
                if (!string.IsNullOrEmpty(Label) && Plugin.LogBlood.Value)
                {
                    Plugin.Log.LogInfo($"[Gore] {Label}: piece drained {_drainedMl:F0} ml from its cut end");
                }
                return;
            }
            const float step = 0.25f;
            _nextDrop = now + step * Random.Range(0.7f, 1.3f);
            float dependent = Mathf.Clamp(0.4f - 0.6f * outward.y, 0.15f, 1f);
            float ml = Mathf.Min(left, left * (1f - Mathf.Pow(0.5f, step / DrainHalfLife)) * (dependent / 0.7f));
            _drainedMl += ml;
            int count = ml > 2f ? 2 : 1;
            blood?.Emit(cut + outward * 0.015f, outward * 0.12f + Random.insideUnitSphere * 0.04f, 0f, count, ml > 2f ? 0.012f : 0.009f);

            // The pool: the drained volume as a ~2.5 mm film (as the body's pools), under the cut end.
            if (Plugin.VisualsPools.Value && now >= _nextPool)
            {
                _nextPool = now + 0.4f;
                float radius = Mathf.Sqrt(_drainedMl * 1e-6f / (Mathf.PI * 0.0025f));
                if (radius >= 0.03f) GroundBlood.Instance?.Pool(this, Label ?? "piece", cut + outward * 0.03f, radius);
            }
        }

        private void Update()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Gore);
            Bleed();
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
            if (_splatted || !Marks) return;
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
            if (Marks) GroundBlood.Instance?.Splat(point, _stuckNormal, Vector3.zero);
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
                    if (Marks && _smears < MaxSmears && (hit.point - _lastSmear).sqrMagnitude > 0.02f * 0.02f)
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
