using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// One skinned mesh cut where a part comes off (1.1.0 gore pass). Before it a removed part was only collapsed: its
    /// bone scaled to 1 %, so every vertex weighted partly to it was pulled part of the way into the joint and the skin
    /// ran into a cone there, under a round cap placed at the bone's pivot. On a neck that pivot is the top of the neck,
    /// above where the skin visibly ended inside a high collar, so the cap hovered (Zabralo, 2026-10-17), and a dome cap
    /// over a cone read as a red ball. Now the skin itself is cut: triangles of the removed part (all three vertices
    /// weighted >= a ragged threshold to it) are left out of a copy of the mesh, and every kept vertex's weight on the
    /// removed bones moves to the nearest kept parent bone, so the kept skin stays where it was and ends in a real open
    /// edge (the ring) that the cap is built on. The bones are still collapsed for rigid gear and hit colliders.
    /// </summary>
    internal sealed class SkinCut
    {
        public SkinnedMeshRenderer Renderer;
        /// <summary>The mesh on the renderer before the cut (EFT's, or our earlier copy).</summary>
        public Mesh Mesh;
        public bool Lod0;
        /// <summary>Renderer.bones index is one of the removed bones.</summary>
        public bool[] InCut;
        /// <summary>Share of each vertex's weight on the removed bones.</summary>
        public float[] Share;
        /// <summary>Each vertex in world space, current pose, before the cut.</summary>
        public Vector3[] World;
        public Vector3[] WorldNormal;
        public Vector2[] Uv;
        public int[][] Tris;
        /// <summary>Per submesh, per triangle (index / 3): goes with the removed part.</summary>
        public bool[][] Removed;
        public int RemovedCount;
        /// <summary>Index in Renderer.bones that takes over the removed bones' weights; -1 = none (left collapsed).</summary>
        public int Keep = -1;
        public Mesh Copy;
        /// <summary>The cut's open edge on the kept side, world space (read after the copy is on).</summary>
        public readonly List<(Vector3 a, Vector3 b)> Ring = new List<(Vector3, Vector3)>();
        /// <summary>The same edge on the removed side (as the piece is cut from the pose before), world space.</summary>
        public readonly List<(Vector3 a, Vector3 b)> PieceRing = new List<(Vector3, Vector3)>();

        /// <summary>
        /// Plans the cut of every readable skin of <paramref name="player"/> weighted to <paramref name="roots"/> (or bones
        /// below them). A triangle goes with the removed part when all three vertices have share >= threshold(world point).
        /// Nothing is changed yet (see Apply). Unreadable meshes are skipped (they stay collapsed only).
        /// </summary>
        /// <summary>
        /// Skins bound to the cut bones that the last Plan could not use, with why (gore-4 test: a PMC T0's bare calf
        /// logged "0/0 skin(s) cut" and fell back to the collapse and a round cap at the knee).
        /// </summary>
        internal static readonly List<string> LastSkipped = new List<string>();

        internal static List<SkinCut> Plan(Component player, IList<Transform> roots, System.Func<Vector3, float> threshold)
        {
            var lod0 = new HashSet<SkinnedMeshRenderer>(GoreMeshes.Lod0Skins(player));
            var list = new List<SkinCut>();
            LastSkipped.Clear();
            foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr == null || smr.sharedMesh == null || smr.name.StartsWith("RealTargets")) continue;
                var bones = smr.bones;
                if (bones == null) continue;
                var inCut = new bool[bones.Length];
                bool anyBone = false;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null) continue;
                    foreach (var root in roots)
                    {
                        if (root != null && (bones[i] == root || bones[i].IsChildOf(root)))
                        {
                            inCut[i] = true;
                            anyBone = true;
                            break;
                        }
                    }
                }
                if (!anyBone) continue;
                string skinName = $"{smr.name}{(lod0.Contains(smr) ? "" : " (LOD1+)")}";
                if (!smr.sharedMesh.isReadable)
                {
                    LastSkipped.Add($"{skinName} not readable");
                    continue;
                }

                var mesh = smr.sharedMesh;
                var weights = mesh.boneWeights;
                if (weights == null || weights.Length != mesh.vertexCount)
                {
                    LastSkipped.Add($"{skinName} no bone weights");
                    continue;
                }
                var share = new float[weights.Length];
                bool anyVertex = false;
                for (int v = 0; v < weights.Length; v++)
                {
                    share[v] = ShareOf(weights[v], inCut);
                    if (share[v] > 0.001f) anyVertex = true;
                }
                if (!anyVertex)
                {
                    LastSkipped.Add($"{skinName} no vertex on the part");
                    continue;
                }

                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                var local = baked.vertices;
                var localN = baked.normals;
                Object.Destroy(baked);
                if (local.Length != share.Length)
                {
                    LastSkipped.Add($"{skinName} bake size differs");
                    continue;
                }
                var world = new Vector3[local.Length];
                var worldN = new Vector3[local.Length];
                var t = smr.transform;
                for (int v = 0; v < local.Length; v++)
                {
                    world[v] = t.TransformPoint(local[v]);
                    worldN[v] = v < localN.Length ? t.TransformDirection(localN[v]) : Vector3.up;
                }

                var cut = new SkinCut
                {
                    Renderer = smr, Mesh = mesh, Lod0 = lod0.Contains(smr), InCut = inCut, Share = share, World = world,
                    WorldNormal = worldN, Uv = mesh.uv, Tris = new int[mesh.subMeshCount][], Removed = new bool[mesh.subMeshCount][],
                    Keep = KeepIndex(bones, inCut, roots),
                };
                var thr = new float[share.Length];
                for (int v = 0; v < share.Length; v++) thr[v] = share[v] > 0.2f ? threshold(world[v]) : 2f;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    var tris = mesh.GetTriangles(sub);
                    cut.Tris[sub] = tris;
                    var removed = new bool[tris.Length / 3];
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                    {
                        int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                        if (share[a] >= thr[a] && share[b] >= thr[b] && share[c] >= thr[c])
                        {
                            removed[i / 3] = true;
                            cut.RemovedCount++;
                        }
                    }
                    cut.Removed[sub] = removed;
                }
                if (cut.RemovedCount > 0) cut.Connect();
                // A skin only lightly weighted to the removed bones (a torso mesh at the neck seam) loses no triangles
                // but is re-bound too, or the collapse would still drag its seam toward the joint.
                if (cut.Lod0 && cut.RemovedCount > 0) cut.FindBoundary();
                list.Add(cut);
            }
            return list;
        }

        /// <summary>
        /// Makes the cut one clean line (gore build test, 2026-10-18: a forearm stump and its piece had holes and the cap
        /// came out as a star with gaps). The ragged threshold alone left islands: kept triangles inside the removed part
        /// (holes in the piece, small extra edge loops) and removed ones inside the kept skin (holes in the sleeve). Now
        /// the removed side is what can be reached, through triangles the threshold gives to the part, from triangles
        /// fully on the part (all three vertices >= 0.9); the kept side is what can be reached through the rest from
        /// triangles fully off it (all <= 0.1). Whatever the kept side can't reach (an island inside the part, a strap with
        /// no fully kept triangle) goes with the part. Neighbours = triangles sharing an edge, welded by rest position.
        /// </summary>
        private void Connect()
        {
            var rest = Mesh.vertices;
            int count = 0;
            var start = new int[Tris.Length];
            for (int sub = 0; sub < Tris.Length; sub++)
            {
                start[sub] = count;
                count += Tris[sub].Length / 3;
            }
            var initial = new bool[count];
            var lows = new float[count];
            var highs = new float[count];
            var edgeTris = new Dictionary<(long, long), List<int>>(count * 2);
            var triEdges = new (long, long)[count * 3];
            for (int sub = 0; sub < Tris.Length; sub++)
            {
                var tris = Tris[sub];
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int t = start[sub] + i / 3;
                    initial[t] = Removed[sub][i / 3];
                    float lo = Mathf.Min(Share[tris[i]], Mathf.Min(Share[tris[i + 1]], Share[tris[i + 2]]));
                    float hi = Mathf.Max(Share[tris[i]], Mathf.Max(Share[tris[i + 1]], Share[tris[i + 2]]));
                    lows[t] = lo;
                    highs[t] = hi;
                    for (int e = 0; e < 3; e++)
                    {
                        long ka = Key(rest[tris[i + e]]), kb = Key(rest[tris[i + (e + 1) % 3]]);
                        var k = ka < kb ? (ka, kb) : (kb, ka);
                        triEdges[t * 3 + e] = k;
                        if (ka == kb) continue;
                        if (!edgeTris.TryGetValue(k, out var list)) edgeTris[k] = list = new List<int>(2);
                        list.Add(t);
                    }
                }
            }

            bool[] Flood(bool[] seeds, System.Func<int, bool> may)
            {
                var reached = new bool[count];
                var queue = new Queue<int>();
                for (int t = 0; t < count; t++)
                {
                    if (!seeds[t]) continue;
                    reached[t] = true;
                    queue.Enqueue(t);
                }
                while (queue.Count > 0)
                {
                    int t = queue.Dequeue();
                    for (int e = 0; e < 3; e++)
                    {
                        if (!edgeTris.TryGetValue(triEdges[t * 3 + e], out var next)) continue;
                        foreach (int u in next)
                        {
                            if (reached[u] || !may(u)) continue;
                            reached[u] = true;
                            queue.Enqueue(u);
                        }
                    }
                }
                return reached;
            }

            // Seeds: fully on the part (>= 0.9) and fully off it (<= 0.1); a skin with neither (weights blended all over)
            // takes its most part-weighted / least part-weighted triangles instead, if they are clearly one side.
            float maxLo = 0f, minHi = 1f;
            for (int t = 0; t < count; t++)
            {
                if (initial[t]) maxLo = Mathf.Max(maxLo, lows[t]);
                minHi = Mathf.Min(minHi, highs[t]);
            }
            float partSeed = Mathf.Min(0.9f, maxLo - 0.05f), keptSeed = Mathf.Max(0.1f, minHi + 0.05f);
            var fullPart = new bool[count];
            var fullKept = new bool[count];
            for (int t = 0; t < count; t++)
            {
                fullPart[t] = initial[t] && lows[t] >= partSeed && maxLo >= 0.5f;
                fullKept[t] = highs[t] <= keptSeed && minHi < 0.5f;
            }
            var part = Flood(fullPart, u => initial[u]);
            var kept = Flood(fullKept, u => !part[u]);
            RemovedCount = 0;
            for (int sub = 0; sub < Tris.Length; sub++)
            {
                var removed = Removed[sub];
                for (int i = 0; i < removed.Length; i++)
                {
                    int t = start[sub] + i;
                    removed[i] = part[t] || !kept[t];
                    if (removed[i]) RemovedCount++;
                }
            }
        }

        private static float ShareOf(BoneWeight w, bool[] inCut)
        {
            float s = 0f;
            if (w.boneIndex0 < inCut.Length && inCut[w.boneIndex0]) s += w.weight0;
            if (w.boneIndex1 < inCut.Length && inCut[w.boneIndex1]) s += w.weight1;
            if (w.boneIndex2 < inCut.Length && inCut[w.boneIndex2]) s += w.weight2;
            if (w.boneIndex3 < inCut.Length && inCut[w.boneIndex3]) s += w.weight3;
            return s;
        }

        /// <summary>The first parent of the removed bones (outside them) that this skin is bound to.</summary>
        private static int KeepIndex(Transform[] bones, bool[] inCut, IList<Transform> roots)
        {
            var index = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null && !inCut[i] && !index.ContainsKey(bones[i])) index[bones[i]] = i;
            }
            foreach (var root in roots)
            {
                for (var p = root != null ? root.parent : null; p != null; p = p.parent)
                {
                    if (index.TryGetValue(p, out int i)) return i;
                }
            }
            return -1;
        }

        /// <summary>The bone the cut skin's kept rim now hangs on (Renderer.bones[Keep]); null = none.</summary>
        public Transform KeepBone => Keep >= 0 && Renderer != null && Keep < Renderer.bones.Length ? Renderer.bones[Keep] : null;

        /// <summary>
        /// Puts the cut copy on the renderer: removed triangles left out, removed bones' weights moved to Keep. For LOD0
        /// skins the rings are read too (open edges between kept and removed triangles, welded by rest position so UV
        /// seams don't count): the kept side from the copy in its current pose, the removed side from the plan's pose.
        /// False = nothing could be cut (no keep bone): the skin stays as it was (collapsed only).
        /// </summary>
        internal bool Apply()
        {
            if (Keep < 0) return false;
            var copy = Object.Instantiate(Mesh);
            copy.name = Mesh.name + " (RealTargets cut)";

            var weights = Mesh.boneWeights;
            for (int v = 0; v < weights.Length; v++)
            {
                if (Share[v] > 0f) weights[v] = Reweight(weights[v], InCut, Keep);
            }
            for (int sub = 0; sub < Tris.Length; sub++)
            {
                var tris = Tris[sub];
                var removed = Removed[sub];
                var kept = new List<int>(tris.Length);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    if (removed[i / 3]) continue;
                    kept.Add(tris[i]);
                    kept.Add(tris[i + 1]);
                    kept.Add(tris[i + 2]);
                }
                copy.SetTriangles(kept, sub, false);
            }
            copy.boneWeights = weights;
            Renderer.sharedMesh = copy;
            Copy = copy;

            if (Lod0) ReadRing();
            return true;
        }

        private static readonly int[] Idx = new int[4];
        private static readonly float[] Wt = new float[4];

        /// <summary>Weight on removed bones goes to <paramref name="keep"/>, merged with it if already there; sorted heaviest first.</summary>
        private static BoneWeight Reweight(BoneWeight w, bool[] inCut, int keep)
        {
            Idx[0] = w.boneIndex0; Idx[1] = w.boneIndex1; Idx[2] = w.boneIndex2; Idx[3] = w.boneIndex3;
            Wt[0] = w.weight0; Wt[1] = w.weight1; Wt[2] = w.weight2; Wt[3] = w.weight3;
            for (int i = 0; i < 4; i++)
            {
                if (Wt[i] > 0f && Idx[i] < inCut.Length && inCut[Idx[i]]) Idx[i] = keep;
            }
            for (int i = 0; i < 4; i++)
            {
                for (int j = i + 1; j < 4; j++)
                {
                    if (Wt[j] > 0f && Idx[j] == Idx[i])
                    {
                        Wt[i] += Wt[j];
                        Wt[j] = 0f;
                    }
                }
            }
            for (int i = 1; i < 4; i++)
            {
                for (int j = i; j > 0 && Wt[j] > Wt[j - 1]; j--)
                {
                    (Wt[j], Wt[j - 1]) = (Wt[j - 1], Wt[j]);
                    (Idx[j], Idx[j - 1]) = (Idx[j - 1], Idx[j]);
                }
            }
            for (int i = 0; i < 4; i++)
            {
                if (Wt[i] <= 0f)
                {
                    Wt[i] = 0f;
                    Idx[i] = 0;
                }
            }
            return new BoneWeight
            {
                boneIndex0 = Idx[0], weight0 = Wt[0], boneIndex1 = Idx[1], weight1 = Wt[1],
                boneIndex2 = Idx[2], weight2 = Wt[2], boneIndex3 = Idx[3], weight3 = Wt[3],
            };
        }

        private static long Key(Vector3 p) =>
            ((long)(Mathf.RoundToInt(p.x * 2000f) & 0xFFFFF) << 40) | ((long)(Mathf.RoundToInt(p.y * 2000f) & 0xFFFFF) << 20) |
            (long)(Mathf.RoundToInt(p.z * 2000f) & 0xFFFFF);

        /// <summary>The cut's open edge as vertex pairs (kept side), LOD0 skins only; filled by Plan.</summary>
        public readonly List<(int a, int b)> Boundary = new List<(int, int)>();

        /// <summary>
        /// Edges shared by a kept and a removed triangle, welded by rest position (UV seams aren't edges of the cut); the
        /// removed side's ring (PieceRing) from the plan's pose.
        /// </summary>
        private void FindBoundary()
        {
            Boundary.Clear();
            PieceRing.Clear();
            var rest = Mesh.vertices;
            var removedEdges = new HashSet<(long, long)>();
            var keptEdges = new Dictionary<(long, long), (int, int)>();
            for (int sub = 0; sub < Tris.Length; sub++)
            {
                var tris = Tris[sub];
                var removed = Removed[sub];
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    bool gone = removed[i / 3];
                    // Kept triangles only matter next to the cut.
                    if (!gone && Share[tris[i]] <= 0f && Share[tris[i + 1]] <= 0f && Share[tris[i + 2]] <= 0f) continue;
                    for (int e = 0; e < 3; e++)
                    {
                        int a = tris[i + e], b = tris[i + (e + 1) % 3];
                        long ka = Key(rest[a]), kb = Key(rest[b]);
                        if (ka == kb) continue;
                        var k = ka < kb ? (ka, kb) : (kb, ka);
                        if (gone) removedEdges.Add(k);
                        else keptEdges[k] = (a, b);
                    }
                }
            }
            foreach (var pair in keptEdges)
            {
                if (!removedEdges.Contains(pair.Key)) continue;
                Boundary.Add(pair.Value);
                PieceRing.Add((World[pair.Value.Item1], World[pair.Value.Item2]));
            }
        }

        /// <summary>The kept side's ring from the copy as it is posed now (its rim no longer follows the removed bones).</summary>
        private void ReadRing()
        {
            Ring.Clear();
            if (Boundary.Count == 0) return;
            var baked = new Mesh();
            Renderer.BakeMesh(baked, true);
            var now = baked.vertices;
            Object.Destroy(baked);
            var t = Renderer.transform;
            foreach (var (a, b) in Boundary)
            {
                if (a < now.Length && b < now.Length) Ring.Add((t.TransformPoint(now[a]), t.TransformPoint(now[b])));
            }
        }

        /// <summary>Gives the renderer its mesh from before this cut (only while our copy is still on), and frees the copy.</summary>
        internal void Undo()
        {
            if (Renderer != null && Copy != null && Renderer.sharedMesh == Copy) Renderer.sharedMesh = Mesh;
            if (Copy != null) Object.Destroy(Copy);
            Copy = null;
        }
    }
}
