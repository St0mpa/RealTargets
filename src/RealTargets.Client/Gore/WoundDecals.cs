using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using EFT;
using RealTargets.Hits;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Visible entry and exit wounds (0.9.0, Stompa: "confirm where shots happened without the debug
    /// windows"). EFT's body decal painter works in a 256x256 texture per clothing piece (~8 mm per
    /// texel), too coarse for a 1 cm bullet hole, so wounds are small textured patches instead:
    /// - per hit the bot's skinned meshes (clothing, skin and worn gear, LOD0 only) are baked into
    ///   temporary mesh colliders (cached for the frame, so a shotgun shell bakes once) and the shot
    ///   line is raycast against them, which finds the surface the bullet crossed;
    /// - 0.9.1: each patch is a small 5x5 grid laid onto that surface by raycasts and SKINNED to the
    ///   same bones with the same weights as the cloth under each grid point (the technique of EFT's
    ///   own AbsolutDecals skinned decals), so it bends and moves exactly with the clothing. 0.9.0
    ///   used a flat quad on one bone: it sank into the cloth as it deformed (Stompa, 0.9.0 test);
    /// - drawn with a multiply shader: the wound darkens/tints the cloth as the map lit it. The 0.9.0
    ///   lit-transparent quads ignored shadows and glowed red in dark rooms;
    /// - entry: round hole ~ calibre with a margin of abrasion, stretched along an oblique shot; exit: larger,
    ///   irregular, shape by energy, sized from the temporary cavity; fabric gets its own puncture and exit tear;
    ///   textures are generated here (no assets), four shapes per kind in a 2x2 atlas (1.2.0, WoundTextures).
    /// Works on living bots and corpses; plate-stopped rounds leave no hole. Unreadable meshes fall
    /// back to the 0.9.0 flat quad on the hit bone.
    /// 0.10.0: the same skinned patches also draw blood running down the clothing from a bleeding
    /// wound (Stain, driven by BleedVisuals), with their own caps so stains never evict wounds.
    /// 0.10.10: a body's patches of one kind (entry, exit, stain) share ONE skinned renderer (PatchBatch)
    /// instead of one renderer each; same skinning per vertex, so they still move and collapse with the body.
    /// </summary>
    internal enum PatchKind { Entry, Exit, Stain, Puncture }

    internal sealed class WoundDecals : MonoBehaviour
    {
        private const int MaxPerBody = 48;
        private const int MaxTotal = 600;
        // blood-5: two runs per wound plus the feeding of finished runs; at 120 a body shot six times dropped its oldest patches
        // (the top of each run, at the wound) first.
        private const int MaxStainsPerBody = 200;
        private const int MaxStainsTotal = 1200;
        private const int Grid = 5;
        private const float Lift = 0.003f;

        /// <summary>
        /// Height above the cloth (0.10.3): the lit cutout pass is depth-tested, so overlapping patches
        /// must not share a plane (z-fighting). Stains lowest (each a little apart), holes above them.
        /// </summary>
        private static float LiftFor(PatchKind kind) => kind == PatchKind.Stain ? Lift + Random.Range(0f, 0.0009f) : Lift + 0.0016f;
        private const string Prefix = "RealTargetsWound";

        /// <summary>
        /// One skin baked at the bake's pose. perf-6 (perf-5 raid: cooking was 97 % of a bake, 1-1.7 ms per mesh, and a
        /// cached bake's colliders cost the same again when switched back on): no MeshCollider any more; the baked vertices
        /// (renderer space, scale divided out) are ray-tested here (<see cref="Test"/>), so a bake is the skinning plus a
        /// copy, ~0.1 ms per mesh.
        /// </summary>
        private sealed class BakedPart
        {
            public SkinnedMeshRenderer Source;
            public Mesh SourceMesh;
            public Vector3 SourcePosition;
            public List<Vector3> Vertices;
            public int[] Triangles;
            public Matrix4x4 ToWorld, ToLocal;
            // Mirrored renderer (negative scale product): the winding, so the facing test, flips.
            public bool Mirrored;
            public Vector3 Min, Max;
            public Topology Topo;
        }

        /// <summary>
        /// A source mesh's fixed topology (perf-7): its triangles, each vertex's triangles (CSR) and its longest edge. Every
        /// triangle that reaches into a box has a vertex within the box grown by the longest edge, so a query scans the
        /// vertices (one read each) instead of the triangles (three), then tests only their triangles.
        /// </summary>
        private sealed class Topology
        {
            public int[] Triangles;
            public int[] VertexStart;
            public int[] VertexTriangles;
            public float MaxEdge;
            public int[] Stamp;
            public int StampId;
        }

        // Baked skins: one scratch mesh, the topology per source mesh, vertex lists reused.
        private static Mesh _scratch;
        private static readonly Dictionary<Mesh, Topology> Topologies = new Dictionary<Mesh, Topology>();
        private static readonly Stack<List<Vector3>> VertexLists = new Stack<List<Vector3>>();
        private static readonly List<int> Candidates = new List<int>();

        /// <summary>
        /// A body's meshes baked for surface rays. 0.10.10 (4.1.6 raid: `Bleed` 8-12 ms and `Hits` 6-11 ms worst
        /// frames): kept while the skeleton stays where it was (every bone within <see cref="PoseTolerance"/>, same
        /// meshes), so the stains running down a still corpse don't re-bake its clothing every time; destroyed after
        /// <see cref="BakeLifetime"/> s unused. perf-6: no physics objects (see <see cref="BakedPart"/>), so bullets,
        /// grenades and droplets never meet them.
        /// </summary>
        private sealed class Baked
        {
            public int Frame;
            public float LastUsed;
            public Player Player;
            public readonly List<BakedPart> Parts = new List<BakedPart>();
            // Meshes not baked yet: baked on demand when a ray passes their bounds.
            public readonly List<SkinnedMeshRenderer> Pending = new List<SkinnedMeshRenderer>();
            public Transform[] PoseBones;
            public Vector3[] Pose;
        }

        private const float PoseTolerance = 0.003f;
        // perf-5 (perf-4 raid: every stain on a still corpse re-baked 3-4 meshes, 3.5-7 ms): 5 s was shorter than a draining
        // corpse's stain cadence (up to 4.8 s per site, contact soaks every 2-3 s until 8), so most stains found the bake gone.
        // Unused bakes only cost memory (colliders off), and MaxBakes caps them.
        private const float BakeLifetime = 20f;
        private const int MaxBakes = 12;
        // Why a body's bake went away (perf-5, dev log): the next bake of that body names it.
        private readonly Dictionary<int, string> _dropped = new Dictionary<int, string>();

        private struct SurfaceHit
        {
            public Vector3 Point, Normal, Bary;
            public int Part, Triangle;
        }

        /// <summary>
        /// One wound or stain (0.10.10). Skinned patches keep their geometry so their body's batch can be
        /// rebuilt without one of them; the flat fallback keeps its own object.
        /// </summary>
        private sealed class Patch
        {
            public PatchBatch Batch;
            public GameObject Flat;
            public Transform Anchor;
            public Vector3 LocalCentre;
            // Mesh space = world space when the patch was laid; each bone's bind pose is its pose then.
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] Uvs;
            public Color32[] Colors;
            public int[] Triangles;
            public BoneWeight[] Weights;
            public Transform[] Bones;
            public Matrix4x4[] Bindposes;
            public bool Removed;

            public bool Alive => !Removed && (Flat != null || (Batch != null && Batch.Go != null));

            /// <summary>Where the patch is now (it moves with its bone).</summary>
            public bool Centre(out Vector3 at)
            {
                at = default;
                if (Flat != null) at = Flat.transform.position;
                else if (Anchor != null) at = Anchor.TransformPoint(LocalCentre);
                else return false;
                return true;
            }
        }

        /// <summary>
        /// All skinned patches of one kind on one body in ONE skinned renderer (0.10.10). The 0.10.9 benchmark
        /// had 214 patches = 214 renderers on 9 bodies: at least 9 fps (1 % low 33 fps) went to drawing them.
        /// Each patch adds its own copy of its bones with its own bind poses (the body moved between patches),
        /// shared when bone and pose are the same (a still corpse). Parented to the pelvis, which no wound
        /// rule collapses, so the batch dies with the body like the patches did.
        /// </summary>
        private sealed class PatchBatch
        {
            public GameObject Go;
            public SkinnedMeshRenderer Renderer;
            public Mesh Mesh;
            public PatchKind Kind;
            public readonly List<Patch> Patches = new List<Patch>();
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<Color32> Colors = new List<Color32>();
            public readonly List<BoneWeight> Weights = new List<BoneWeight>();
            public readonly List<int> Triangles = new List<int>();
            public readonly List<Transform> Bones = new List<Transform>();
            public readonly List<Matrix4x4> Bindposes = new List<Matrix4x4>();
            public readonly Dictionary<Transform, List<int>> BoneSlots = new Dictionary<Transform, List<int>>();
            public bool Rebuild;
        }

        private readonly Dictionary<int, Baked> _baked = new Dictionary<int, Baked>();
        private readonly Dictionary<int, Queue<Patch>> _perBody = new Dictionary<int, Queue<Patch>>();
        private readonly Queue<Patch> _all = new Queue<Patch>();
        private readonly Dictionary<int, Queue<Patch>> _stainsPerBody = new Dictionary<int, Queue<Patch>>();
        private readonly Queue<Patch> _allStains = new Queue<Patch>();
        private readonly Dictionary<long, PatchBatch> _batches = new Dictionary<long, PatchBatch>();
        private readonly HashSet<PatchBatch> _dirty = new HashSet<PatchBatch>();
        private readonly Dictionary<Mesh, BoneWeight[]> _weights = new Dictionary<Mesh, BoneWeight[]>();
        // Multiply (darken) and alpha-blended (colour, light-scaled) materials per wound type.
        private Material _entry, _exit, _entryColour, _exitColour, _stain, _stainColour;
        // Lit colour pass (0.10.3, `Decal shader = Lit`): the map's light decides the brightness.
        private Material _entryLit, _exitLit, _stainLit;
        // Fabric/gear puncture (1.1.0 retest 4): multiply, colour and lit passes like the others.
        private Material _punct, _punctColour, _punctLit;
        private Mesh _quad;
        private int[] _gridTriangles;
        private bool _cleanupQueued;
        private bool _loggedMode;

        internal static string ShaderName { get; private set; } = "-";
        internal static WoundDecals Instance { get; private set; }
        /// <summary>Live wound and stain patches (for the [Perf] line).</summary>
        internal int PatchCount
        {
            get
            {
                int n = 0;
                foreach (var p in _all) if (p.Alive) n++;
                foreach (var p in _allStains) if (p.Alive) n++;
                return n;
            }
        }

        /// <summary>Renderers drawing them: one per batch with patches, plus flat fallbacks.</summary>
        internal int RendererCount
        {
            get
            {
                int n = 0;
                foreach (var b in _batches.Values) if (b.Go != null && b.Patches.Count > 0) n++;
                foreach (var p in _all) if (p.Alive && p.Flat != null) n++;
                return n;
            }
        }

        /// <summary>perf-3 (dev split benchmark): every renderer drawing a wound or stain patch (batches and flat fallbacks).</summary>
        internal void CollectRenderers(List<Renderer> into)
        {
            foreach (var b in _batches.Values)
            {
                if (b.Go != null && b.Renderer != null) into.Add(b.Renderer);
            }
            foreach (var p in _all)
            {
                if (p.Alive && p.Flat != null) into.Add(p.Flat.GetComponent<Renderer>());
            }
        }

        private void Awake()
        {
            Instance = this;
            HitCollector.Published += OnHit;
        }

        private void OnDestroy()
        {
            HitCollector.Published -= OnHit;
            ClearBakes();
            if (_scratch != null) Destroy(_scratch);
            _scratch = null;
        }

        internal void ResetRaid()
        {
            ClearBakes();
            _perBody.Clear();
            _all.Clear(); // the patches die with their bots
            _stainsPerBody.Clear();
            _allStains.Clear();
            _weights.Clear();
            Topologies.Clear();
            foreach (var b in _batches.Values)
            {
                if (b.Go != null) Destroy(b.Go);
            }
            _batches.Clear();
            _dirty.Clear();
            _clusters.Clear();
        }

        /// <summary>Uploads the batches that got or lost patches this frame (once per batch per frame).</summary>
        private void LateUpdate()
        {
            if (_dirty.Count == 0) return;
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Wounds);
            foreach (var batch in _dirty)
            {
                if (batch.Go == null) continue;
                try
                {
                    Upload(batch);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"Wound decals: batch upload: {ex.Message}");
                }
            }
            _dirty.Clear();
        }

        private void OnHit(HitEvent hit)
        {
            if (!Plugin.VisualsWounds.Value || !Plugin.VisualsEnabled.Value || hit.Target == null || hit.IsLocalPlayer || hit.Segments.Count == 0)
            {
                return;
            }

            try
            {
                Place(hit);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Wound decals: {ex.Message}");
            }
        }

        private void Place(HitEvent hit)
        {
            LitBlood.DumpEftShaders();
            if (hit.Target != null) LitBlood.DumpBody(hit.Target.GetComponentInChildren<SkinnedMeshRenderer>());
            // blood-5b: the map is loaded by now: the EFT ground materials take the map's cubemap and a map material's values.
            if (hit.Target != null) LitBlood.LateResolve(hit.Target);
            int first = hit.Segments.FindIndex(s => !s.HasPlate);
            if (first < 0)
            {
                Debug(hit, "none: plate stopped it");
                return;
            }
            var entry = hit.Segments[first];
            // Soft armour or a helmet stopped it: no hole in the body.
            if (entry.StoppedByArmour(Plugin.ArmourStopFraction.Value))
            {
                Debug(hit, $"none: armour stopped it ({entry.DamageOut:F0}/{entry.DamageIn:F0} dmg through)");
                return;
            }

            var player = hit.Target;
            var colliders = player.PlayerBones?.BodyPartCollidersDictionary;
            if (colliders == null || !colliders.TryGetValue(entry.Collider, out var bpc) || bpc == null)
            {
                Debug(hit, $"none: no collider {entry.Collider}");
                return;
            }

            EnsureResources();
            if (_entry == null && _entryColour == null)
            {
                Debug(hit, "no shader");
                return;
            }
            float hole = HoleDiameter(hit);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var baked = Bake(player);
            double bakeMs = watch.Elapsed.TotalMilliseconds;

            // A hole in and out for every stretch through flesh: a round through a forearm into the chest also
            // goes into the chest (1.1.0 test: only the forearm got holes). WoundPath decides the stretches, so
            // the bleeding and these holes always match.
            WoundPath.ResolveAll(hit, Paths);
            foreach (var path in Paths)
            {
                PlaceLeg(hit, player, baked, colliders, path, hole);
            }
            // Retest 4: the raid's first hit took 17 ms here, later ones under 5: say where it goes.
            double total = watch.Elapsed.TotalMilliseconds;
            if (total > 8.0)
            {
                Plugin.Log.LogInfo($"[Perf] wound decals {total:F1} ms: bake {bakeMs:F1} ms (frame of the bake, cached later), " +
                                   $"surfaces and patches {total - bakeMs:F1} ms, {Paths.Count} stretch(es)");
            }
        }

        private static readonly List<WoundPath> Paths = new List<WoundPath>(2);

        private void PlaceLeg(HitEvent hit, Player player, Baked baked, IReadOnlyDictionary<EBodyPartColliderType, BodyPartCollider> colliders,
            WoundPath path, float hole)
        {
            string leg = path.Leg > 0 ? $"leg {path.Leg + 1} ({path.Entry.Collider}): " : string.Empty;
            if (!colliders.TryGetValue(path.Entry.Collider, out var bpc) || bpc == null)
            {
                Debug(hit, $"{leg}none: no collider {path.Entry.Collider}");
                return;
            }
            var bone = bpc.TransformCached;
            var dir = path.Direction;
            SurfaceHit at;

            // Entry: from outside along the shot (none where it goes on from inside the body).
            if (path.FromInside)
            {
                Debug(hit, $"{leg}no entry: goes on from inside (same round, earlier frame)");
            }
            else if (Surface(baked, path.Start - dir * 0.3f, dir, 0.6f, -1, out at) || SurfaceTowardBone(baked, path.Start, bone, out at))
            {
                var look = EntryLookOf(hit, path.Entry, baked, at, out string surface);
                if (look == EntryLook.LeftToGame)
                {
                    Debug(hit, $"{leg}entry left to the game's hole decal on {surface}");
                }
                else
                {
                    var kind = look == EntryLook.Skin ? PatchKind.Entry : PatchKind.Puncture;
                    // Oblique: stretched along the shot (+U = the way it travelled), margin wider on the side it came from.
                    float aspect = WoundTextures.EntryAspect(dir, at.Normal);
                    int cell = kind == PatchKind.Entry ? WoundTextures.EntryCell(hit.IsFragment, Calibre(hit.Caliber), aspect) : WoundTextures.PunctureCell(false);
                    float size = kind == PatchKind.Entry ? hole / (2f * WoundTextures.EntryOuter) : hole / (2f * WoundTextures.PunctureHole);
                    if (SpawnSkinned(player, baked, at, size, kind, dir, cell, aspect))
                    {
                        Debug(hit, $"{leg}entry ({look}, shape {cell}, x{aspect:F2} along the shot) skinned on {surface}");
                        if (hit.ProjectileCount > 1 && !hit.IsFragment) PelletCluster(hit, player, baked, at.Point, dir, kind, hole);
                    }
                    else
                    {
                        Spawn(player, bone, at.Point, at.Normal, size, kind, cell);
                        Debug(hit, $"{leg}entry ({look}) flat on {bone.name} (no bone weights)");
                    }
                }
            }
            else if (path.Entry.Armour.Any && GamePaintsHole(hit))
            {
                Debug(hit, $"{leg}entry left to the game's hole decal on {path.Entry.Armour.Name} (no surface found along the shot)");
            }
            else
            {
                // rc-1 test: a flat patch at the collider point floated off the leg ("entry wound decals floating in air";
                // the capsule is fatter than the mesh). No surface along the shot or toward the bone: no patch.
                Debug(hit, $"{leg}no entry patch: no surface along the shot or toward {bone.name}");
            }

            // Exit: seen from the far side.
            if (!path.Exited) return;
            float energy = hit.ImpactEnergy > 0f ? path.Energy : 500f;
            float cavity = Mathf.Clamp(0.008f + Plugin.CavityScale.Value * 0.0005f * Mathf.Sqrt(energy), 0.008f, 0.06f);
            float exitSize = Mathf.Max(hole * 1.5f, cavity * 1.1f);
            if (colliders.TryGetValue(path.ExitCollider, out var exitBpc) && exitBpc != null) bone = exitBpc.TransformCached;
            if (Surface(baked, path.End + dir * 0.3f, -dir, 0.6f, -1, out at) || SurfaceTowardBone(baked, path.End, bone, out at))
            {
                // Out through skin: a torn exit by energy; through clothing or gear: a fabric exit tear.
                var exitPart = baked.Parts[at.Part];
                string exitMesh = exitPart.Source != null ? exitPart.Source.name : "?";
                bool skin = SurfaceOf(exitMesh, MaterialAt(exitPart, at.Triangle)) == SurfaceKind.Skin;
                var kind = skin ? PatchKind.Exit : PatchKind.Puncture;
                int cell = skin ? WoundTextures.ExitCell(energy, false) : WoundTextures.PunctureCell(true);
                // blood-1: fabric exits were too small to see at the top of their run: 1.35x.
                // blood-3: a 12.7 mm exit through cloth was a small tear: very high energy tears more.
                float size = skin ? exitSize / (2f * WoundTextures.ExitReach) : exitSize * 1.35f * (energy > 5000f ? 1.5f : 1f) / (2f * WoundTextures.PunctureTear);
                bool mirror = Random.value < 0.5f;
                if (!SpawnSkinned(player, baked, at, size, kind, null, cell, 1f, mirror))
                {
                    Spawn(player, bone, at.Point, at.Normal, size, kind, cell);
                }
                Debug(hit, $"{leg}exit placed ({(skin ? "skin" : "fabric")}, shape {cell}){(InferredExit(hit, path.Leg) ? " (inferred)" : "")}");
            }
            else
            {
                Debug(hit, $"{leg}no exit patch: no surface along the shot or toward {bone.name}");
            }
        }

        private sealed class Cluster
        {
            public readonly List<Vector3> Points = new List<Vector3>(8);
            public float Time;
            public bool Torn;
        }

        private readonly Dictionary<(int body, string source), Cluster> _clusters = new Dictionary<(int, string), Cluster>();

        /// <summary>
        /// blood-5 (blood-4 test: buckshot at 1.4 m read as "a stain with dots"): at close range a shell's charge is still nearly
        /// one mass and tears one ragged wound with the pellet holes tight around it. Once three pellets of one shell have
        /// entered within 6 cm of their centre, one torn patch (fabric: the ragged tear; skin: the jagged entry) covers the
        /// middle, sized to the pattern. The pellet holes stay round it.
        /// </summary>
        private void PelletCluster(HitEvent hit, Player player, Baked baked, Vector3 point, Vector3 dir, PatchKind kind, float hole)
        {
            if (string.IsNullOrEmpty(hit.SourceId)) return;
            var key = (player.Id, hit.SourceId);
            if (!_clusters.TryGetValue(key, out var cluster) || UnityEngine.Time.time - cluster.Time > 1f)
            {
                if (_clusters.Count > 64) _clusters.Clear();
                _clusters[key] = cluster = new Cluster { Time = UnityEngine.Time.time };
            }
            cluster.Points.Add(point);
            if (cluster.Torn || cluster.Points.Count < 3) return;

            var centre = Vector3.zero;
            foreach (var p in cluster.Points) centre += p;
            centre /= cluster.Points.Count;
            float spread = 0f;
            int near = 0;
            foreach (var p in cluster.Points)
            {
                float d = (p - centre).magnitude;
                if (d < 0.06f)
                {
                    near++;
                    spread = Mathf.Max(spread, d);
                }
            }
            if (near < 3) return;
            if (!Surface(baked, centre - dir * 0.3f, dir, 0.6f, -1, out var at)) return;
            cluster.Torn = true;
            float diameter = 2f * spread + hole * 2.5f;
            bool fabric = kind == PatchKind.Puncture;
            float size = fabric ? diameter / (2f * WoundTextures.PunctureTear) : diameter / (2f * WoundTextures.EntryOuter);
            int cell = fabric ? 2 : 3;
            if (SpawnSkinned(player, baked, at, size, kind, dir, cell, 1f, Random.value < 0.5f))
            {
                Debug(hit, $"pellet cluster: {near} pellets within {spread * 100f:F1} cm, one torn {(fabric ? "fabric" : "skin")} wound {diameter * 100f:F1} cm");
            }
        }

        /// <summary>
        /// The body's surface nearest a collider point when the shot's own ray met none (rc-1: corpse calves): a ray from
        /// just outside the point toward the bone, so the patch lands on the cloth instead of floating on the capsule.
        /// </summary>
        private static bool SurfaceTowardBone(Baked baked, Vector3 point, Transform bone, out SurfaceHit at)
        {
            at = default;
            if (bone == null) return false;
            var inward = bone.position - point;
            float depth = inward.magnitude;
            if (depth < 0.005f) return false;
            inward /= depth;
            return Surface(baked, point - inward * 0.05f, inward, depth + 0.05f, -1, out at);
        }

        private static bool InferredExit(HitEvent hit, int leg) => leg < hit.Legs.Count && hit.Legs[leg].InferredExit;

        private enum EntryLook { Skin, Cloth, Gear, LeftToGame }

        /// <summary>
        /// What the entry goes into (1.1.0 gore retest 4, Stompa: "we show the same bloody hole no matter where we hit";
        /// HollywoodFX's black holes on a vest "look better as they are different when you hit armour"):
        /// skin gets our wound; clothing a dark fabric puncture (the blood comes from the bleeding's stains); worn armour
        /// or gear gets nothing where EFT already paints its hole (a living bot, or a corpse with HollywoodFX's body
        /// decals), else the puncture. Skin = a head/hand/body mesh that isn't worn gear, or a skin material (see SurfaceOf).
        /// </summary>
        private static EntryLook EntryLookOf(HitEvent hit, HitSegment segment, Baked baked, SurfaceHit at, out string surface)
        {
            var part = baked.Parts[at.Part];
            string mesh = part.Source != null ? part.Source.name : "?";
            string material = MaterialAt(part, at.Triangle);
            surface = $"{mesh} [{material}]";
            // Worn armour over this collider: the round went in through it, whatever the ray met first.
            if (segment.Armour.Any) return GamePaintsHole(hit) ? EntryLook.LeftToGame : EntryLook.Gear;
            switch (SurfaceOf(mesh, material))
            {
                case SurfaceKind.Skin: return EntryLook.Skin;
                case SurfaceKind.Gear: return GamePaintsHole(hit) ? EntryLook.LeftToGame : EntryLook.Gear;
                default: return EntryLook.Cloth;
            }
        }

        /// <summary>
        /// The entry's patch kind on a body nothing else paints (the death screen's preview): skin = wound, anything else
        /// (cloth, armour, gear) = puncture. Same name rules as <see cref="EntryLookOf"/>.
        /// </summary>
        internal static PatchKind EntryKindFor(string mesh, string material, bool armour)
        {
            if (armour) return PatchKind.Puncture;
            return SurfaceOf(mesh, material) == SurfaceKind.Skin ? PatchKind.Entry : PatchKind.Puncture;
        }

        private enum SurfaceKind { Skin, Cloth, Gear }

        /// <summary>What a body mesh is, from its name and the hit triangle's material.</summary>
        private static SurfaceKind SurfaceOf(string mesh, string material)
        {
            // Suits (tops, trousers, shirts) are never gear by name (rc-1: `Top_wild_vest2` read as gear and bare
            // forearms got no hole); skin only by a skin material, which scav tops don't have (see SkinMaterialWords).
            if (ClothingMesh(mesh)) return HasWord(material, SkinMaterialWords) ? SurfaceKind.Skin : SurfaceKind.Cloth;
            // Worn items before the skin words (release check 3: chest rigs `CR_blackRock_body`, backpacks
            // `BP_WarTech_Body` and a balaclava with material `item_equipment_facecover_...` drew red skin wounds through
            // "body" and "face"; a "headset" has "head").
            if (GearItem(mesh, material)) return SurfaceKind.Gear;
            if (HasWord(mesh, SkinMeshWords) || HasWord(material, SkinMaterialWords)) return SurfaceKind.Skin;
            return SurfaceKind.Cloth;
        }

        private static bool GearItem(string mesh, string material) =>
            GearName(mesh) || GearName(material) ||
            mesh.StartsWith("BP_", System.StringComparison.OrdinalIgnoreCase) ||
            mesh.StartsWith("CR_", System.StringComparison.OrdinalIgnoreCase) ||
            (material != null && material.StartsWith("item_equipment", System.StringComparison.OrdinalIgnoreCase));

        /// <summary>Materials for a patch drawn outside this class (the death screen), resources made on first use.</summary>
        internal Material[] MaterialsFor(PatchKind kind)
        {
            EnsureResources();
            return Materials(kind);
        }

        private static bool ClothingMesh(string mesh) =>
            mesh.StartsWith("Top_", System.StringComparison.OrdinalIgnoreCase) ||
            mesh.StartsWith("Pants_", System.StringComparison.OrdinalIgnoreCase) ||
            mesh.StartsWith("Tshirt_", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>EFT paints its texture hole decal on living bots; on corpses only with HollywoodFX's body decals.</summary>
        private static bool GamePaintsHole(HitEvent hit) =>
            hit.Status != HitStatus.Corpse || RealTargets.Compat.HollywoodFx.PaintsCorpseWounds;

        private static readonly string[] GearWords = { "armor", "armour", "helmet", "rig_", "_rig", "vest", "plate", "backpack", "bag", "mask", "goggles", "visor", "headset", "pouch", "razgruz" };
        // Mesh names: EFT bodies are <side>_head_*, *_hands_*, *_body_*. Materials: a suit's cloth is named after its mesh
        // (`Top_usec_Tier2`, `pants_usec_UrbanResponder`). No "body" for materials (release check 2): a scav top is ONE
        // material `Wild_Body_N` for cloth and bare arms alike, so "body" made every raincoat torso hit a skin wound.
        // Such tops get the cloth puncture everywhere; one material can't tell bare arms from cloth.
        private static readonly string[] SkinMeshWords = { "head", "hand", "body" };
        private static readonly string[] SkinMaterialWords = { "skin", "head", "hand", "face", "neck" };

        private static bool GearName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("AR_", System.StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var w in GearWords)
            {
                if (name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool HasWord(string name, string[] words)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var w in words)
            {
                if (name.IndexOf(w, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>The material of the submesh the hit triangle belongs to (baked meshes keep the source's submesh order).</summary>
        private static string MaterialAt(BakedPart part, int triangle)
        {
            try
            {
                var materials = part.Source != null ? part.Source.sharedMaterials : null;
                if (materials == null || materials.Length == 0 || part.SourceMesh == null) return "?";
                int sub = 0, first = 0;
                for (int s = 0; s < part.SourceMesh.subMeshCount; s++)
                {
                    int count = (int)part.SourceMesh.GetIndexCount(s) / 3;
                    if (triangle < first + count) { sub = s; break; }
                    first += count;
                    sub = s;
                }
                var m = materials[Mathf.Min(sub, materials.Length - 1)];
                return m != null ? m.name : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static EBodyPartColliderType ExitCollider(HitEvent hit, Vector3 point)
        {
            foreach (var e in hit.Exits)
            {
                if ((e.point - point).sqrMagnitude < 1e-6f) return e.collider;
            }
            return hit.Entry.Collider;
        }

        /// <summary>Entry hole diameter (m) from the calibre: bullet + abrasion collar.</summary>
        internal static float HoleDiameter(HitEvent hit)
        {
            if (hit.IsFragment) return Random.Range(0.006f, 0.014f);
            if (hit.ProjectileCount > 1) return 0.008f;
            float mm = Calibre(hit.Caliber);
            return Mathf.Clamp(mm / 1000f * 1.1f + 0.003f, 0.007f, 0.02f);
        }

        /// <summary>
        /// Bore in mm from EFT's calibre code: "9x19PARA" -> 9, "556x45NATO" -> 5.56, "762x51" -> 7.62,
        /// "127x33" -> 12.7, "86x70" -> 8.6, "57x28" -> 5.7, "23x75" -> 23, "12g" -> 18.5, "366TKM" -> 9.55.
        /// 0.10.5 test: two-digit codes were read as mm (.338 LM "86 mm", 6.8x51 "68 mm"), so they counted
        /// as >= 12 mm for the slug head rule and got the largest entry hole.
        /// </summary>
        internal static float Calibre(string caliber)
        {
            if (string.IsNullOrEmpty(caliber)) return 8f;
            if (caliber.StartsWith("12g")) return 18.5f;
            if (caliber.StartsWith("20g")) return 15.6f;
            if (caliber.StartsWith("366")) return 9.55f;
            int i = 0;
            while (i < caliber.Length && char.IsDigit(caliber[i])) i++;
            if (i == 0 || !float.TryParse(caliber.Substring(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out float v)) return 8f;
            if (i == 1) return v;
            // Two digits: 20-40 are whole mm (20x1mm, 23x75, 26x75, 30x29, 40x46, 40mmRU); else one decimal (86, 68, 57, 46, 93).
            if (i == 2) return v >= 20f && v <= 40f ? v : v / 10f;
            return v / 100f >= 3f ? v / 100f : v / 10f; // 556 -> 5.56, 127 -> 12.7, 1143 -> 11.43, 725 -> 7.25
        }

        // ---- Surface finding ---------------------------------------------------------------------

        /// <summary>
        /// Nearest surface along the ray (only <paramref name="onlyPart"/> if >= 0); normal faces back along the ray.
        /// 0.10.15 (0.10.13 raid: a 68 ms frame in a close fight): unbaked meshes are baked nearest-first and only while
        /// one could still be closer than the best hit so far (0.10.14 baked every mesh whose bounds the ray crossed,
        /// most of an outfit for a torso shot); the result is the same. <paramref name="bake"/> false = baked meshes only.
        /// </summary>
        private static bool Surface(Baked baked, Vector3 from, Vector3 dir, float distance, int onlyPart, out SurfaceHit result, bool bake = true)
        {
            result = default;
            if (baked == null) return false;

            var ray = new Ray(from, dir);
            float bestDist = float.MaxValue;
            for (int i = 0; i < baked.Parts.Count; i++)
            {
                if (onlyPart >= 0 && i != onlyPart) continue;
                Test(baked, i, ray, distance, ref bestDist, ref result);
            }
            if (onlyPart < 0 && bake)
            {
                while (true)
                {
                    int next = -1;
                    float nextEnter = float.MaxValue;
                    for (int i = baked.Pending.Count - 1; i >= 0; i--)
                    {
                        var smr = baked.Pending[i];
                        if (smr == null)
                        {
                            baked.Pending.RemoveAt(i);
                            if (next > i) next--;
                            continue;
                        }
                        var bounds = smr.bounds;
                        bounds.Expand(0.1f);
                        float enter;
                        if (bounds.Contains(from)) enter = 0f;
                        else if (!bounds.IntersectRay(ray, out enter)) continue;
                        if (enter > distance || enter >= nextEnter) continue;
                        nextEnter = enter;
                        next = i;
                    }
                    if (next < 0 || nextEnter > bestDist) break;
                    Test(baked, BakePending(baked, next), ray, distance, ref bestDist, ref result);
                }
            }

            if (bestDist == float.MaxValue) return false;
            // The mesh's normal can face inward on thin cloth: the wound faces where the shot came from.
            if (Vector3.Dot(result.Normal, dir) > 0f) result.Normal = -result.Normal;
            return true;
        }

        private Baked Bake(Player player)
        {
            string why = null;
            if (_baked.TryGetValue(player.Id, out var cached))
            {
                if (cached.Frame == Time.frameCount || (why = Invalid(cached)) == null)
                {
                    cached.Frame = Time.frameCount;
                    cached.LastUsed = Time.time;
                    QueueCleanup();
                    return cached;
                }
                DestroyBake(cached);
            }
            else if (_dropped.TryGetValue(player.Id, out why))
            {
                _dropped.Remove(player.Id);
            }
            // Corpses only: a living body moves between almost every two bakes.
            if (why != null && Plugin.PerfLogging && (player.HealthController == null || !player.HealthController.IsAlive))
            {
                Plugin.Log.LogInfo($"[Perf] corpse re-bake {player.Profile?.Nickname}: {why}");
            }

            using var timing = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.BakeSetup);
            // Lower LODs are enabled renderers too (the LODGroup only hides them): bake LOD0 only.
            var lowLods = new HashSet<Renderer>();
            foreach (var group in player.GetComponentsInChildren<LODGroup>())
            {
                var lods = group.GetLODs();
                for (int l = 1; l < lods.Length; l++)
                {
                    foreach (var r in lods[l].renderers)
                    {
                        if (r != null) lowLods.Add(r);
                    }
                }
            }

            var baked = new Baked { Frame = Time.frameCount, LastUsed = Time.time, Player = player };
            var poseBones = new HashSet<Transform>();
            foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr == null || !smr.enabled || smr.sharedMesh == null || lowLods.Contains(smr) || smr.name.StartsWith(Prefix)) continue;
                baked.Pending.Add(smr);
                var bones = smr.bones;
                if (bones == null) continue;
                foreach (var b in bones)
                {
                    if (b != null) poseBones.Add(b);
                }
            }
            baked.PoseBones = new Transform[poseBones.Count];
            poseBones.CopyTo(baked.PoseBones);
            baked.Pose = new Vector3[baked.PoseBones.Length];
            for (int i = 0; i < baked.PoseBones.Length; i++) baked.Pose[i] = baked.PoseBones[i].position;

            _baked[player.Id] = baked;
            QueueCleanup();
            return baked;
        }

        /// <summary>
        /// Null while the body has not moved since the bake (every bone where it was, every baked mesh unchanged), otherwise
        /// why not (perf-5: logged with the re-bake).
        /// </summary>
        private static string Invalid(Baked baked)
        {
            if (baked.Player == null || baked.PoseBones == null) return "body gone";
            const float tol = PoseTolerance * PoseTolerance;
            for (int i = 0; i < baked.PoseBones.Length; i++)
            {
                var b = baked.PoseBones[i];
                if (b == null) return "a bone gone";
                float moved = (b.position - baked.Pose[i]).sqrMagnitude;
                if (moved > tol) return $"bone {b.name} moved {Mathf.Sqrt(moved) * 1000f:F1} mm in {Time.time - baked.LastUsed:F1} s";
            }
            foreach (var part in baked.Parts)
            {
                // A swapped mesh (skull burst cut, limb removed), a hidden renderer or a moved renderer: bake again.
                if (part.Source == null || !part.Source.enabled) return "a baked renderer hidden or gone";
                if (part.Source.sharedMesh != part.SourceMesh) return $"mesh of {part.Source.name} swapped";
                if ((part.Source.transform.position - part.SourcePosition).sqrMagnitude > tol) return $"renderer {part.Source.name} moved";
            }
            return null;
        }

        private void QueueCleanup()
        {
            if (_cleanupQueued) return;
            _cleanupQueued = true;
            StartCoroutine(CleanupAtEndOfFrame());
        }

        /// <summary>
        /// The ray against one baked skin (perf-6, replaces MeshCollider.Raycast): the nearest triangle within
        /// <paramref name="distance"/> and nearer than <paramref name="bestDist"/>, front faces only unless
        /// Physics.queriesHitBackfaces (as PhysX did). Point, face normal (toward the ray), triangle index into the mesh's
        /// triangles and barycentrics (v0, v1, v2) as a RaycastHit gives them.
        /// </summary>
        private static void Test(Baked baked, int i, Ray ray, float distance, ref float bestDist, ref SurfaceHit result)
        {
            var part = baked.Parts[i];
            var verts = part.Vertices;
            var tris = part.Triangles;
            if (verts == null || tris == null || distance <= 0f) return;

            // The segment in the skin's own space: an affine map keeps the fraction along it, so t is shared.
            var o = part.ToLocal.MultiplyPoint3x4(ray.origin);
            var d = part.ToLocal.MultiplyVector(ray.direction * distance);
            float limit = Mathf.Min(1f, bestDist / distance), t0 = 0f, t1 = limit;
            if (!Clip(part.Min, part.Max, o, d, ref t0, ref t1)) return;
            var s0 = o + d * t0;
            var s1 = o + d * t1;
            var lo = Vector3.Min(s0, s1) - Vector3.one * 1e-4f;
            var hi = Vector3.Max(s0, s1) + Vector3.one * 1e-4f;

            bool backfaces = Physics.queriesHitBackfaces;
            float facing = part.Mirrored ? -1f : 1f;
            int best = -1;
            float bestT = limit, bestU = 0f, bestV = 0f;
            Gather(part, lo, hi, Candidates);
            for (int n = 0; n < Candidates.Count; n++)
            {
                int tri = Candidates[n];
                int k = tri * 3;
                var a = verts[tris[k]];
                var b = verts[tris[k + 1]];
                var c = verts[tris[k + 2]];
                if ((a.x < lo.x && b.x < lo.x && c.x < lo.x) || (a.x > hi.x && b.x > hi.x && c.x > hi.x) ||
                    (a.y < lo.y && b.y < lo.y && c.y < lo.y) || (a.y > hi.y && b.y > hi.y && c.y > hi.y) ||
                    (a.z < lo.z && b.z < lo.z && c.z < lo.z) || (a.z > hi.z && b.z > hi.z && c.z > hi.z)) continue;

                // Moller-Trumbore. det = -dot(d, cross(e1, e2)); Unity's front face normal is cross(e1, e2).
                float e1x = b.x - a.x, e1y = b.y - a.y, e1z = b.z - a.z;
                float e2x = c.x - a.x, e2y = c.y - a.y, e2z = c.z - a.z;
                float px = d.y * e2z - d.z * e2y, py = d.z * e2x - d.x * e2z, pz = d.x * e2y - d.y * e2x;
                float det = e1x * px + e1y * py + e1z * pz;
                if (det > -1e-12f && det < 1e-12f) continue;
                if (!backfaces && det * facing < 0f) continue;
                float inv = 1f / det;
                float sx = o.x - a.x, sy = o.y - a.y, sz = o.z - a.z;
                float u = (sx * px + sy * py + sz * pz) * inv;
                if (u < 0f || u > 1f) continue;
                float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                float v = (d.x * qx + d.y * qy + d.z * qz) * inv;
                if (v < 0f || u + v > 1f) continue;
                float t = (e2x * qx + e2y * qy + e2z * qz) * inv;
                if (t < 0f || t > bestT) continue;
                best = tri;
                bestT = t;
                bestU = u;
                bestV = v;
            }
            if (best < 0) return;

            int at = best * 3;
            var wa = part.ToWorld.MultiplyPoint3x4(verts[tris[at]]);
            var wb = part.ToWorld.MultiplyPoint3x4(verts[tris[at + 1]]);
            var wc = part.ToWorld.MultiplyPoint3x4(verts[tris[at + 2]]);
            var normal = Vector3.Cross(wb - wa, wc - wa).normalized;
            if (Vector3.Dot(normal, ray.direction) > 0f) normal = -normal;
            bestDist = bestT * distance;
            result.Point = ray.origin + ray.direction * bestDist;
            result.Normal = normal;
            result.Part = i;
            result.Triangle = best;
            result.Bary = new Vector3(1f - bestU - bestV, bestU, bestV);
        }

        /// <summary>Clips the segment fraction [t0, t1] of o + d * t to a box; false when it misses.</summary>
        private static bool Clip(Vector3 min, Vector3 max, Vector3 o, Vector3 d, ref float t0, ref float t1)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = o[axis], dir = d[axis], lo = min[axis], hi = max[axis];
                if (Mathf.Abs(dir) < 1e-9f)
                {
                    if (origin < lo || origin > hi) return false;
                    continue;
                }
                float a = (lo - origin) / dir, b = (hi - origin) / dir;
                if (a > b) (a, b) = (b, a);
                if (a > t0) t0 = a;
                if (b < t1) t1 = b;
                if (t0 > t1) return false;
            }
            return true;
        }

        /// <summary>
        /// The skin's triangles that may reach into the local box [lo, hi] (perf-7): the vertices inside it grown by the
        /// longest edge, then their triangles, each once. A superset; callers test each one.
        /// </summary>
        private static void Gather(BakedPart part, Vector3 lo, Vector3 hi, List<int> into)
        {
            into.Clear();
            var topo = part.Topo;
            var verts = part.Vertices;
            if (topo == null || verts == null || verts.Count + 1 != topo.VertexStart.Length) return;
            float e = topo.MaxEdge;
            float x0 = lo.x - e, y0 = lo.y - e, z0 = lo.z - e, x1 = hi.x + e, y1 = hi.y + e, z1 = hi.z + e;
            if (++topo.StampId == int.MaxValue)
            {
                System.Array.Clear(topo.Stamp, 0, topo.Stamp.Length);
                topo.StampId = 1;
            }
            int stamp = topo.StampId;
            var start = topo.VertexStart;
            var vertexTris = topo.VertexTriangles;
            var marks = topo.Stamp;
            for (int v = 0; v < verts.Count; v++)
            {
                var p = verts[v];
                if (p.x < x0 || p.x > x1 || p.y < y0 || p.y > y1 || p.z < z0 || p.z > z1) continue;
                for (int s = start[v], end = start[v + 1]; s < end; s++)
                {
                    int tri = vertexTris[s];
                    if (marks[tri] == stamp) continue;
                    marks[tri] = stamp;
                    into.Add(tri);
                }
            }
        }

        /// <summary>The source mesh's topology, built on its first bake (triangles from the baked copy, same order).</summary>
        private static Topology TopologyOf(Mesh source, Mesh baked, List<Vector3> vertices)
        {
            if (Topologies.TryGetValue(source, out var topo)) return topo;
            var tris = baked.triangles;
            int vertexCount = vertices.Count;
            var start = new int[vertexCount + 1];
            for (int k = 0; k < tris.Length; k++)
            {
                if (tris[k] >= 0 && tris[k] < vertexCount) start[tris[k] + 1]++;
            }
            for (int v = 0; v < vertexCount; v++) start[v + 1] += start[v];
            var fill = new int[vertexCount];
            var vertexTris = new int[start[vertexCount]];
            float maxEdge = 0f;
            for (int k = 0; k + 2 < tris.Length; k += 3)
            {
                int tri = k / 3;
                for (int c = 0; c < 3; c++)
                {
                    int v = tris[k + c];
                    if (v < 0 || v >= vertexCount) continue;
                    vertexTris[start[v] + fill[v]++] = tri;
                    int w = tris[k + (c + 1) % 3];
                    if (w >= 0 && w < vertexCount) maxEdge = Mathf.Max(maxEdge, (vertices[v] - vertices[w]).sqrMagnitude);
                }
            }
            // The longest edge at this pose; cloth stretches a little between poses, so a margin.
            topo = new Topology
            {
                Triangles = tris, VertexStart = start, VertexTriangles = vertexTris, MaxEdge = Mathf.Sqrt(maxEdge) * 1.25f + 0.001f,
                Stamp = new int[tris.Length / 3],
            };
            Topologies[source] = topo;
            return topo;
        }

        // A patch's grid results (perf-7), reused.
        private static readonly SurfaceHit[] GridHits = new SurfaceHit[Grid * Grid];
        private static readonly SurfaceHit[] GridOther = new SurfaceHit[Grid * Grid];
        private static readonly float[] GridDepth = new float[Grid * Grid];
        private static readonly float[] GridOtherDepth = new float[Grid * Grid];
        private static readonly bool[] GridFound = new bool[Grid * Grid];

        /// <summary>
        /// A patch's 25 surface points in one pass (perf-7; perf-6 cast 25-50 rays, 2-3 ms a stain): the grid's rays are
        /// parallel (-<paramref name="n"/>, from 4 cm above the patch plane to 4 cm below), so each nearby triangle is
        /// projected onto the plane once and the grid points inside it read their depth from its barycentrics. Per point the
        /// same answer as before: the nearest front face on the centre's skin, else the nearest on any baked skin.
        /// <paramref name="found"/>[k] says whether point k (j * Grid + i) has one in <see cref="GridHits"/>.
        /// </summary>
        private static void LayGrid(Baked baked, int centrePart, Vector3 c, Vector3 t, Vector3 b, Vector3 n, float halfU, float half,
            bool[] found)
        {
            const float reach = 0.04f;
            int count = Grid * Grid;
            for (int k = 0; k < count; k++)
            {
                GridDepth[k] = float.NegativeInfinity;
                GridOtherDepth[k] = float.NegativeInfinity;
                found[k] = false;
            }
            if (halfU <= 0f || half <= 0f) return;
            float stepU = 2f * halfU / (Grid - 1), stepV = 2f * half / (Grid - 1);
            bool backfaces = Physics.queriesHitBackfaces;
            // The patch volume's world box: half extents along t, b, n.
            var extent = new Vector3(
                Mathf.Abs(t.x) * halfU + Mathf.Abs(b.x) * half + Mathf.Abs(n.x) * reach,
                Mathf.Abs(t.y) * halfU + Mathf.Abs(b.y) * half + Mathf.Abs(n.y) * reach,
                Mathf.Abs(t.z) * halfU + Mathf.Abs(b.z) * half + Mathf.Abs(n.z) * reach);

            for (int pi = 0; pi < baked.Parts.Count; pi++)
            {
                var part = baked.Parts[pi];
                var verts = part.Vertices;
                var tris = part.Triangles;
                if (verts == null || tris == null) continue;
                var m = part.ToLocal;
                var lc = m.MultiplyPoint3x4(c);
                var le = new Vector3(
                    Mathf.Abs(m.m00) * extent.x + Mathf.Abs(m.m01) * extent.y + Mathf.Abs(m.m02) * extent.z,
                    Mathf.Abs(m.m10) * extent.x + Mathf.Abs(m.m11) * extent.y + Mathf.Abs(m.m12) * extent.z,
                    Mathf.Abs(m.m20) * extent.x + Mathf.Abs(m.m21) * extent.y + Mathf.Abs(m.m22) * extent.z);
                var lo = lc - le;
                var hi = lc + le;
                if (lo.x > part.Max.x || hi.x < part.Min.x || lo.y > part.Max.y || hi.y < part.Min.y || lo.z > part.Max.z || hi.z < part.Min.z) continue;
                Gather(part, lo, hi, Candidates);
                if (Candidates.Count == 0) continue;

                // Skin space -> patch coordinates (u along t, v along b, w along n, from c): three rows of t/b/n . ToWorld.
                var w2 = part.ToWorld;
                float ux = t.x * w2.m00 + t.y * w2.m10 + t.z * w2.m20, uy = t.x * w2.m01 + t.y * w2.m11 + t.z * w2.m21;
                float uz = t.x * w2.m02 + t.y * w2.m12 + t.z * w2.m22, uo = t.x * w2.m03 + t.y * w2.m13 + t.z * w2.m23 - Vector3.Dot(t, c);
                float vx = b.x * w2.m00 + b.y * w2.m10 + b.z * w2.m20, vy = b.x * w2.m01 + b.y * w2.m11 + b.z * w2.m21;
                float vz = b.x * w2.m02 + b.y * w2.m12 + b.z * w2.m22, vo = b.x * w2.m03 + b.y * w2.m13 + b.z * w2.m23 - Vector3.Dot(b, c);
                float wx = n.x * w2.m00 + n.y * w2.m10 + n.z * w2.m20, wy = n.x * w2.m01 + n.y * w2.m11 + n.z * w2.m21;
                float wz = n.x * w2.m02 + n.y * w2.m12 + n.z * w2.m22, wo = n.x * w2.m03 + n.y * w2.m13 + n.z * w2.m23 - Vector3.Dot(n, c);
                bool centre = pi == centrePart;
                var depth = centre ? GridDepth : GridOtherDepth;
                var hits = centre ? GridHits : GridOther;

                for (int q = 0; q < Candidates.Count; q++)
                {
                    int tri = Candidates[q];
                    int k3 = tri * 3;
                    var pa = verts[tris[k3]];
                    var pb = verts[tris[k3 + 1]];
                    var pc = verts[tris[k3 + 2]];
                    float w0 = wx * pa.x + wy * pa.y + wz * pa.z + wo;
                    float w1 = wx * pb.x + wy * pb.y + wz * pb.z + wo;
                    float w3 = wx * pc.x + wy * pc.y + wz * pc.z + wo;
                    if ((w0 > reach && w1 > reach && w3 > reach) || (w0 < -reach && w1 < -reach && w3 < -reach)) continue;
                    float u0 = ux * pa.x + uy * pa.y + uz * pa.z + uo, v0 = vx * pa.x + vy * pa.y + vz * pa.z + vo;
                    float u1 = ux * pb.x + uy * pb.y + uz * pb.z + uo, v1 = vx * pb.x + vy * pb.y + vz * pb.z + vo;
                    float u2 = ux * pc.x + uy * pc.y + uz * pc.z + uo, v2 = vx * pc.x + vy * pc.y + vz * pc.z + vo;
                    // Twice the projected area = the world face normal along n (t x b = n): > 0 faces the rays' origin side.
                    float area = (u1 - u0) * (v2 - v0) - (v1 - v0) * (u2 - u0);
                    if (area > -1e-12f && area < 1e-12f) continue;
                    if (!backfaces && area < 0f) continue;
                    int i0 = Mathf.Max(0, Mathf.CeilToInt((Mathf.Min(u0, Mathf.Min(u1, u2)) + halfU) / stepU));
                    int i1 = Mathf.Min(Grid - 1, Mathf.FloorToInt((Mathf.Max(u0, Mathf.Max(u1, u2)) + halfU) / stepU));
                    int j0 = Mathf.Max(0, Mathf.CeilToInt((Mathf.Min(v0, Mathf.Min(v1, v2)) + half) / stepV));
                    int j1 = Mathf.Min(Grid - 1, Mathf.FloorToInt((Mathf.Max(v0, Mathf.Max(v1, v2)) + half) / stepV));
                    if (i0 > i1 || j0 > j1) continue;
                    float inv = 1f / area;
                    for (int j = j0; j <= j1; j++)
                    {
                        float pv = -half + j * stepV;
                        for (int i = i0; i <= i1; i++)
                        {
                            float pu = -halfU + i * stepU;
                            float b1 = ((pu - u0) * (v2 - v0) - (pv - v0) * (u2 - u0)) * inv;
                            float b2 = ((u1 - u0) * (pv - v0) - (v1 - v0) * (pu - u0)) * inv;
                            float b0 = 1f - b1 - b2;
                            if (b0 < 0f || b1 < 0f || b2 < 0f) continue;
                            float w = b0 * w0 + b1 * w1 + b2 * w3;
                            int k = j * Grid + i;
                            if (w > reach || w < -reach || w <= depth[k]) continue;
                            depth[k] = w;
                            // The face normal in patch coordinates, then in the world (the patch frame is a rotation).
                            float e1u = u1 - u0, e1v = v1 - v0, e1w = w1 - w0, e2u = u2 - u0, e2v = v2 - v0, e2w = w3 - w0;
                            var np = new Vector3(e1v * e2w - e1w * e2v, e1w * e2u - e1u * e2w, area);
                            var normal = (t * np.x + b * np.y + n * np.z).normalized;
                            if (Vector3.Dot(normal, n) < 0f) normal = -normal;
                            hits[k] = new SurfaceHit
                            {
                                Point = c + t * pu + b * pv + n * w, Normal = normal, Part = pi, Triangle = tri,
                                Bary = new Vector3(b0, b1, b2),
                            };
                        }
                    }
                }
            }
            for (int k = 0; k < count; k++)
            {
                if (GridDepth[k] > float.NegativeInfinity) found[k] = true;
                else if (GridOtherDepth[k] > float.NegativeInfinity)
                {
                    GridHits[k] = GridOther[k];
                    found[k] = true;
                }
            }
        }

        // Bake time spent this frame (0.10.15): body stains wait for a frame with room (StainAffordable).
        private static int _bakeFrame = -1;
        private static double _bakeMsFrame;
        private const double StainBakeBudgetMs = 3.0;

        /// <summary>
        /// A blood stain may bake this frame: nothing baked yet, or less than <see cref="StainBakeBudgetMs"/> so far.
        /// Stains run down a bleeding body for minutes; one frame later looks the same, a stacked bake is a stutter.
        /// </summary>
        internal static bool StainAffordable => _bakeFrame != Time.frameCount || _bakeMsFrame < StainBakeBudgetMs;

        /// <summary>Bakes one pending skin at its current pose for the surface rays; returns its part index.</summary>
        private static int BakePending(Baked baked, int index)
        {
            using var part = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.BodyBake);
            var smr = baked.Pending[index];
            baked.Pending.RemoveAt(index);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (_scratch == null) _scratch = new Mesh { name = Prefix + "Bake", hideFlags = HideFlags.HideAndDontSave };
            var mesh = _scratch;
            using (RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.BodySkin))
            {
                // BakeMesh(true) is fully local (scale divided out): the rays go through the skin's whole transform, scale too
                // (release check 5: a BEAR Tiger T-shirt at scale 0.1 baked 10x and wounds on it landed off the shirt).
                smr.BakeMesh(mesh, true);
            }
            var source = smr.sharedMesh;
            var vertices = VertexLists.Count > 0 ? VertexLists.Pop() : new List<Vector3>();
            Topology topo;
            using (RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.BodyCopy))
            {
                mesh.GetVertices(vertices);
                topo = TopologyOf(source, mesh, vertices);
            }
            var triangles = topo.Triangles;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int v = 0; v < vertices.Count; v++)
            {
                var p = vertices[v];
                if (p.x < min.x) min.x = p.x;
                if (p.y < min.y) min.y = p.y;
                if (p.z < min.z) min.z = p.z;
                if (p.x > max.x) max.x = p.x;
                if (p.y > max.y) max.y = p.y;
                if (p.z > max.z) max.z = p.z;
            }
            var t = smr.transform;
            var scale = t.lossyScale;
            var toWorld = Matrix4x4.TRS(t.position, t.rotation, scale);
            double ms = watch.Elapsed.TotalMilliseconds;
            if (_bakeFrame != Time.frameCount)
            {
                _bakeFrame = Time.frameCount;
                _bakeMsFrame = 0;
            }
            _bakeMsFrame += ms;
            // 0.10.12 normal raid: `Bleed` worst frames of 10-19 ms remained; this names the meshes behind them.
            if (ms >= 4.0 && Plugin.PerfLogging)
            {
                Plugin.Log.LogInfo($"[Perf] slow bake {ms:F1} ms: {baked.Player?.Profile?.Nickname} {smr.name} ({vertices.Count} verts, " +
                                   $"{(baked.Player != null && baked.Player.HealthController != null && baked.Player.HealthController.IsAlive ? "alive" : "corpse")})");
            }
            baked.Parts.Add(new BakedPart
            {
                Source = smr, SourceMesh = source, SourcePosition = t.position, Vertices = vertices, Triangles = triangles, Topo = topo,
                ToWorld = toWorld, ToLocal = toWorld.inverse, Mirrored = scale.x * scale.y * scale.z < 0f, Min = min, Max = max,
            });
            return baked.Parts.Count - 1;
        }

        /// <summary>End of the frame: bakes unused for a while (or over the cap, oldest first) destroyed.</summary>
        private IEnumerator CleanupAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            // perf-4: was outside every timing scope (end of frame, after Perf's LateUpdate: counted in the next frame).
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Wounds);
            _cleanupQueued = false;
            List<int> drop = null;
            foreach (var pair in _baked)
            {
                var b = pair.Value;
                if (b.Player == null || Time.time - b.LastUsed > BakeLifetime)
                {
                    (drop ??= new List<int>()).Add(pair.Key);
                    _dropped[pair.Key] = $"bake unused for {Time.time - b.LastUsed:F1} s, dropped";
                }
            }
            while (_baked.Count - (drop?.Count ?? 0) > MaxBakes)
            {
                int oldest = -1;
                float at = float.MaxValue;
                foreach (var pair in _baked)
                {
                    if ((drop == null || !drop.Contains(pair.Key)) && pair.Value.LastUsed < at)
                    {
                        at = pair.Value.LastUsed;
                        oldest = pair.Key;
                    }
                }
                if (oldest < 0) break;
                (drop ??= new List<int>()).Add(oldest);
                _dropped[oldest] = $"more than {MaxBakes} bodies baked, the oldest dropped";
            }
            if (drop == null) yield break;
            foreach (int id in drop)
            {
                DestroyBake(_baked[id]);
                _baked.Remove(id);
            }
        }

        private void ClearBakes()
        {
            foreach (var b in _baked.Values) DestroyBake(b);
            _baked.Clear();
            _dropped.Clear();
        }

        private static void DestroyBake(Baked baked)
        {
            using var timing = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.BakeDestroy);
            foreach (var part in baked.Parts)
            {
                if (part.Vertices != null && VertexLists.Count < 64) VertexLists.Push(part.Vertices);
                part.Vertices = null;
            }
            baked.Parts.Clear();
            baked.Pending.Clear();
        }

        // ---- Skinned patches ---------------------------------------------------------------------

        /// <summary>
        /// A 5x5 grid centred on the hit, each point dropped onto the baked surface and given the
        /// cloth's bone weights there; bind poses are the bones' current pose, so the patch starts
        /// exactly on the surface and then deforms like the cloth. Points that find no surface
        /// (patch overhanging an edge) are transparent.
        /// </summary>
        private bool SpawnSkinned(Player player, Baked baked, SurfaceHit center, float size, PatchKind kind, Vector3? axis,
            int cell = 0, float aspect = 1f, bool mirror = false)
        {
            using var timing = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.SpawnPatch);
            var centreWeights = Weights(baked, center);
            if (centreWeights == null) return false;

            var n = center.Normal.normalized;
            Vector3 t;
            var along = axis.HasValue ? Vector3.ProjectOnPlane(axis.Value, n) : Vector3.zero;
            if (along.sqrMagnitude > 0.01f)
            {
                // Texture's long axis (+U) along the given direction (stains: the way blood runs).
                t = along.normalized;
            }
            else
            {
                t = Vector3.Cross(n, Mathf.Abs(n.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
                t = Quaternion.AngleAxis(Random.Range(0f, 360f), n) * t;
            }
            var b = Vector3.Cross(n, t);
            float half = size * Random.Range(0.9f, 1.15f) * 0.5f;
            float halfU = half * aspect;
            float lift = LiftFor(kind);

            int count = Grid * Grid;
            var world = new Vector3[count];
            var normals = new Vector3[count];
            var colors = new Color32[count];
            var vertexWeights = new List<(Transform bone, float w)>[count];
            // Each point dropped onto the surface along -n from 4 cm above (the centre's skin first, else any baked one).
            LayGrid(baked, center.Part, center.Point, t, b, n, halfU, half, GridFound);
            for (int j = 0; j < Grid; j++)
            {
                for (int i = 0; i < Grid; i++)
                {
                    int k = j * Grid + i;
                    var flat = center.Point + t * ((i / (float)(Grid - 1) - 0.5f) * 2f * halfU) + b * ((j / (float)(Grid - 1) - 0.5f) * 2f * half);
                    List<(Transform, float)> w = null;
                    var on = GridHits[k];
                    if (GridFound[k]) w = Weights(baked, on);
                    if (w != null)
                    {
                        world[k] = on.Point + on.Normal * lift;
                        // The surface's own normal (0.10.10; was RecalculateNormals per patch, ~ the same).
                        normals[k] = on.Normal.normalized;
                        vertexWeights[k] = w;
                        colors[k] = new Color32(255, 255, 255, 255);
                    }
                    else
                    {
                        world[k] = flat + n * lift;
                        normals[k] = n;
                        vertexWeights[k] = centreWeights;
                        colors[k] = new Color32(255, 255, 255, 0);
                    }
                }
            }

            var rootBone = centreWeights[0].bone;
            var bones = new List<Transform>();
            var index = new Dictionary<Transform, int>();
            var boneWeights = new BoneWeight[count];
            for (int k = 0; k < count; k++)
            {
                var w = vertexWeights[k];
                var bw = new BoneWeight();
                for (int q = 0; q < w.Count && q < 4; q++)
                {
                    if (!index.TryGetValue(w[q].bone, out int bi))
                    {
                        bi = bones.Count;
                        bones.Add(w[q].bone);
                        index[w[q].bone] = bi;
                    }
                    switch (q)
                    {
                        case 0: bw.boneIndex0 = bi; bw.weight0 = w[q].w; break;
                        case 1: bw.boneIndex1 = bi; bw.weight1 = w[q].w; break;
                        case 2: bw.boneIndex2 = bi; bw.weight2 = w[q].w; break;
                        default: bw.boneIndex3 = bi; bw.weight3 = w[q].w; break;
                    }
                }
                boneWeights[k] = bw;
            }

            // Mesh space is world space now: each bone's bind pose is its current world-to-local.
            var bindposes = new Matrix4x4[bones.Count];
            for (int i = 0; i < bones.Count; i++) bindposes[i] = bones[i].worldToLocalMatrix;

            // Cells with a corner off the body are left out (0.10.3): the lit colour pass ignores vertex alpha.
            var tris = new List<int>(_gridTriangles.Length);
            for (int q = 0; q + 5 < _gridTriangles.Length; q += 6)
            {
                bool all = true;
                for (int e = 0; e < 6 && all; e++) all = colors[_gridTriangles[q + e]].a > 0;
                if (!all) continue;
                for (int e = 0; e < 6; e++) tris.Add(_gridTriangles[q + e]);
            }

            var batch = BatchFor(player, kind, baked.Parts[center.Part].Source.gameObject.layer);
            if (batch == null) return false;
            var patch = new Patch
            {
                Batch = batch,
                Anchor = rootBone,
                LocalCentre = rootBone.InverseTransformPoint(center.Point),
                Vertices = world,
                Normals = normals,
                Uvs = WoundTextures.CellUvs(cell, mirror, Grid),
                Colors = colors,
                Triangles = tris.ToArray(),
                Weights = boneWeights,
                Bones = bones.ToArray(),
                Bindposes = bindposes,
            };
            batch.Patches.Add(patch);
            Append(batch, patch);
            _dirty.Add(batch);
            Track(player, patch, kind == PatchKind.Stain);
            if (!_loggedMode)
            {
                _loggedMode = true;
                Plugin.Log.LogInfo($"Wound decals: skinned patches batched per body ({bones.Count} bones on the first, surface {baked.Parts[center.Part].Source.name}), shader {ShaderName}");
            }
            return true;
        }

        // ---- Batches -----------------------------------------------------------------------------

        /// <summary>The body's batch for this kind, made on its first patch (null: no pelvis to hang it on).</summary>
        private PatchBatch BatchFor(Player player, PatchKind kind, int layer)
        {
            long key = (long)player.Id * 4 + (int)kind;
            if (_batches.TryGetValue(key, out var batch) && batch.Go != null) return batch;

            var pelvis = player.PlayerBones?.Pelvis?.Original;
            if (pelvis == null) return null;
            var go = new GameObject(Prefix + "Batch") { layer = layer };
            go.transform.SetParent(pelvis, false);
            var mesh = new Mesh { name = Prefix + "Batch" };
            mesh.MarkDynamic();
            go.AddComponent<OwnedMesh>().Mesh = mesh;

            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.quality = SkinQuality.Bone4;
            smr.updateWhenOffscreen = false;
            smr.rootBone = pelvis;
            // A box around the whole body in pelvis space (undo the bone's scale).
            var lossy = pelvis.lossyScale;
            smr.localBounds = new Bounds(Vector3.zero, new Vector3(2.6f / Mathf.Max(Mathf.Abs(lossy.x), 1e-4f),
                2.6f / Mathf.Max(Mathf.Abs(lossy.y), 1e-4f), 2.6f / Mathf.Max(Mathf.Abs(lossy.z), 1e-4f)));
            smr.sharedMaterials = Materials(kind);
            Shade(smr);
            smr.skinnedMotionVectors = false;
            smr.enabled = false;
            // Unlit modes: one light level for the whole batch, at the pelvis.
            go.AddComponent<WoundLight>().Init(smr, kind, pelvis, pelvis.position, Vector3.up);

            batch = new PatchBatch { Go = go, Renderer = smr, Mesh = mesh, Kind = kind };
            _batches[key] = batch;
            return batch;
        }

        /// <summary>Adds a patch's geometry to the batch's lists (bones shared when bone and bind pose match).</summary>
        private void Append(PatchBatch batch, Patch patch)
        {
            int baseVertex = batch.Vertices.Count;
            var map = new int[patch.Bones.Length];
            for (int i = 0; i < patch.Bones.Length; i++) map[i] = BoneSlot(batch, patch.Bones[i], patch.Bindposes[i]);

            for (int v = 0; v < patch.Vertices.Length; v++)
            {
                batch.Vertices.Add(patch.Vertices[v]);
                batch.Normals.Add(patch.Normals[v]);
                batch.Uvs.Add(patch.Uvs[v]);
                batch.Colors.Add(patch.Colors[v]);
                var bw = patch.Weights[v];
                // Unused slots (weight 0) point at bone 0, which every patch has.
                bw.boneIndex0 = map[bw.boneIndex0];
                bw.boneIndex1 = map[bw.boneIndex1];
                bw.boneIndex2 = map[bw.boneIndex2];
                bw.boneIndex3 = map[bw.boneIndex3];
                batch.Weights.Add(bw);
            }
            foreach (int t in patch.Triangles) batch.Triangles.Add(baseVertex + t);
        }

        private static int BoneSlot(PatchBatch batch, Transform bone, Matrix4x4 bindpose)
        {
            if (!batch.BoneSlots.TryGetValue(bone, out var slots))
            {
                batch.BoneSlots[bone] = slots = new List<int>(2);
            }
            foreach (int s in slots)
            {
                if (batch.Bindposes[s] == bindpose) return s;
            }
            int slot = batch.Bones.Count;
            batch.Bones.Add(bone);
            batch.Bindposes.Add(bindpose);
            slots.Add(slot);
            return slot;
        }

        /// <summary>Writes the batch's lists into its mesh; after a removal, rebuilds the lists from the live patches.</summary>
        private void Upload(PatchBatch batch)
        {
            using var part = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.PatchUpload);
            var mesh = batch.Mesh;
            var smr = batch.Renderer;
            if (batch.Rebuild)
            {
                batch.Rebuild = false;
                batch.Vertices.Clear();
                batch.Normals.Clear();
                batch.Uvs.Clear();
                batch.Colors.Clear();
                batch.Weights.Clear();
                batch.Triangles.Clear();
                batch.Bones.Clear();
                batch.Bindposes.Clear();
                batch.BoneSlots.Clear();
                foreach (var p in batch.Patches) Append(batch, p);
            }

            smr.sharedMesh = null;
            mesh.Clear();
            if (batch.Vertices.Count == 0 || batch.Triangles.Count == 0)
            {
                smr.enabled = false;
                return;
            }
            mesh.SetVertices(batch.Vertices);
            mesh.SetNormals(batch.Normals);
            mesh.SetUVs(0, batch.Uvs);
            mesh.SetColors(batch.Colors);
            mesh.boneWeights = batch.Weights.ToArray();
            mesh.bindposes = batch.Bindposes.ToArray();
            mesh.SetTriangles(batch.Triangles, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2.6f);
            smr.bones = batch.Bones.ToArray();
            smr.sharedMesh = mesh;
            smr.enabled = true;
        }

        /// <summary>Takes a patch away: destroys a flat one, rebuilds a skinned one's batch without it.</summary>
        private void Remove(Patch patch)
        {
            if (patch == null || patch.Removed) return;
            patch.Removed = true;
            if (patch.Flat != null)
            {
                Destroy(patch.Flat);
                return;
            }
            var batch = patch.Batch;
            if (batch == null || batch.Go == null) return;
            batch.Patches.Remove(patch);
            batch.Rebuild = true;
            _dirty.Add(batch);
        }

        /// <summary>The cloth's bone weights at a surface point: the triangle's three vertices blended by barycentrics, top 4, normalised.</summary>
        private List<(Transform bone, float w)> Weights(Baked baked, SurfaceHit at)
        {
            if (at.Part < 0 || at.Part >= baked.Parts.Count) return null;
            var part = baked.Parts[at.Part];
            var smr = part.Source;
            var shared = smr != null ? smr.sharedMesh : null;
            if (shared == null) return null;

            if (!_weights.TryGetValue(shared, out var weights))
            {
                weights = shared.isReadable ? shared.boneWeights : null;
                _weights[shared] = weights;
            }
            if (weights == null || weights.Length == 0) return null;

            if (part.Triangles == null) return null;
            int t = at.Triangle * 3;
            if (at.Triangle < 0 || t + 2 >= part.Triangles.Length) return null;

            var bones = smr.bones;
            var sum = new Dictionary<Transform, float>();
            for (int c = 0; c < 3; c++)
            {
                int v = part.Triangles[t + c];
                if (v < 0 || v >= weights.Length) return null;
                float bary = c == 0 ? at.Bary.x : c == 1 ? at.Bary.y : at.Bary.z;
                var bw = weights[v];
                AddWeight(sum, bones, bw.boneIndex0, bw.weight0 * bary);
                AddWeight(sum, bones, bw.boneIndex1, bw.weight1 * bary);
                AddWeight(sum, bones, bw.boneIndex2, bw.weight2 * bary);
                AddWeight(sum, bones, bw.boneIndex3, bw.weight3 * bary);
            }
            if (sum.Count == 0) return null;

            var list = new List<(Transform bone, float w)>();
            foreach (var pair in sum) list.Add((pair.Key, pair.Value));
            list.Sort((x, y) => y.w.CompareTo(x.w));
            if (list.Count > 4) list.RemoveRange(4, list.Count - 4);
            float total = 0f;
            foreach (var x in list) total += x.w;
            if (total <= 1e-5f) return null;
            for (int i = 0; i < list.Count; i++) list[i] = (list[i].bone, list[i].w / total);
            return list;
        }

        private static void AddWeight(Dictionary<Transform, float> sum, Transform[] bones, int index, float w)
        {
            if (w <= 0f || bones == null || index < 0 || index >= bones.Length || bones[index] == null) return;
            sum.TryGetValue(bones[index], out float have);
            sum[bones[index]] = have + w;
        }

        // ---- Flat patch (fallback) ---------------------------------------------------------------

        private void Spawn(Player player, Transform bone, Vector3 at, Vector3 normal, float size, PatchKind kind, int cell = 0)
        {
            var go = new GameObject(Prefix);
            go.transform.SetParent(bone, true);
            go.transform.SetPositionAndRotation(at + normal * LiftFor(kind),
                Quaternion.LookRotation(normal, Mathf.Abs(Vector3.Dot(normal, Vector3.up)) < 0.99f ? Vector3.up : Vector3.forward) *
                Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)));
            float s = size * Random.Range(0.9f, 1.15f);
            // Undo the bone's scale so the patch keeps its real size.
            var lossy = bone.lossyScale;
            go.transform.localScale = new Vector3(s / Mathf.Max(lossy.x, 1e-4f), s / Mathf.Max(lossy.y, 1e-4f), s / Mathf.Max(lossy.z, 1e-4f));
            // The collider's own layer (HitCollider/Deadbody) may not be drawn: use the body mesh's layer.
            go.layer = BodyLayer(player, bone.gameObject.layer);
            var quad = Object.Instantiate(_quad);
            var cellUvs = WoundTextures.CellUvs(cell, false, 2);
            quad.uv = new[] { cellUvs[0], cellUvs[1], cellUvs[3], cellUvs[2] };
            go.AddComponent<OwnedMesh>().Mesh = quad;
            go.AddComponent<MeshFilter>().sharedMesh = quad;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = Materials(kind);
            Shade(renderer);
            go.AddComponent<WoundLight>().Init(renderer, kind, bone, at, normal);
            Track(player, new Patch { Flat = go }, false);
            if (!_loggedMode)
            {
                _loggedMode = true;
                Plugin.Log.LogInfo($"Wound decals: flat patches on bones (fallback), shader {ShaderName}");
            }
        }

        /// <summary>The patch's materials for the current `Decal shader` mode (falls back to what exists).</summary>
        private Material[] Materials(PatchKind kind)
        {
            var mul = kind == PatchKind.Exit ? _exit : kind == PatchKind.Stain ? _stain : kind == PatchKind.Puncture ? _punct : _entry;
            var col = kind == PatchKind.Exit ? _exitColour : kind == PatchKind.Stain ? _stainColour : kind == PatchKind.Puncture ? _punctColour : _entryColour;
            var lit = kind == PatchKind.Exit ? _exitLit : kind == PatchKind.Stain ? _stainLit : kind == PatchKind.Puncture ? _punctLit : _entryLit;
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
                    return new[] { mul ?? col };
            }
        }

        /// <summary>`Decal shader` changed: existing patches switch materials.</summary>
        internal void Reshade()
        {
            var objects = new List<GameObject>();
            foreach (var b in _batches.Values) if (b.Go != null) objects.Add(b.Go);
            foreach (var p in _all) if (p.Alive && p.Flat != null) objects.Add(p.Flat);
            foreach (var go in objects)
            {
                var light = go.GetComponent<WoundLight>();
                var r = go.GetComponent<Renderer>();
                if (light == null || r == null) continue;
                r.SetPropertyBlock(null);
                r.sharedMaterials = Materials(light.Kind);
                Shade(r);
                light.Refresh();
            }
        }

        /// <summary>Lit mode: lit by the body's probes; unlit modes: the colour pass is tinted by WoundLight.</summary>
        private static void Shade(Renderer r)
        {
            if (Lit)
            {
                LitBlood.Setup(r);
                r.receiveShadows = false;
                return;
            }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        /// <summary>The lit colour pass is in use (mode Lit and the shader exists).</summary>
        internal static bool Lit => Plugin.DecalShader.Value == DecalShaderMode.Lit && Instance != null && Instance._entryLit != null;

        /// <summary>Per-hit placement line, while `Debug / Log wound placement` is on.</summary>
        private static void Debug(HitEvent hit, string what)
        {
            if (!Plugin.LogWounds.Value) return;
            Plugin.Log.LogInfo($"[Wound] {hit.Target?.Profile?.Nickname} {hit.WeaponName} {hit.Entry.Collider}: {what}");
        }

        /// <summary>
        /// Removes this body's wound and stain patches centred within <paramref name="radius"/> of
        /// <paramref name="at"/> (0.10.5: the skull burst hole, where the exit patch would hang over the gap).
        /// </summary>
        internal int ClearNear(Player player, Vector3 at, float radius)
        {
            if (player == null) return 0;
            int cleared = 0;
            foreach (var perBody in new[] { _perBody, _stainsPerBody })
            {
                if (!perBody.TryGetValue(player.Id, out var queue)) continue;
                foreach (var patch in queue)
                {
                    // The patch's centre follows its bone (a batch's bounds are a box around the body).
                    if (!patch.Alive || !patch.Centre(out var centre) || (centre - at).sqrMagnitude > radius * radius) continue;
                    Remove(patch);
                    cleared++;
                }
            }
            if (cleared > 0 && Plugin.LogWounds.Value)
            {
                Plugin.Log.LogInfo($"[Wound] {player.Profile?.Nickname}: {cleared} patch(es) removed over a skull burst or a removed part");
            }
            return cleared;
        }

        private void Track(Player player, Patch patch, bool stain)
        {
            var perBody = stain ? _stainsPerBody : _perBody;
            var all = stain ? _allStains : _all;
            if (!perBody.TryGetValue(player.Id, out var queue))
            {
                perBody[player.Id] = queue = new Queue<Patch>();
            }
            queue.Enqueue(patch);
            all.Enqueue(patch);
            while (queue.Count > (stain ? MaxStainsPerBody : MaxPerBody)) Remove(queue.Dequeue());
            while (all.Count > (stain ? MaxStainsTotal : MaxTotal)) Remove(all.Dequeue());
        }

        /// <summary>
        /// Blood on the outside of the body (0.10.0): a stain patch on the surface nearest to
        /// <paramref name="at"/>, found by looking back at the body along -<paramref name="outward"/>,
        /// with its long axis along the way blood runs. False when no surface is there (the stream
        /// ran off an edge or into a gap), so the caller stops extending that streak.
        /// </summary>
        internal bool Stain(Player player, Vector3 at, Vector3 outward, float size, bool atWound, BloodTone tone = BloodTone.Venous, int step = 0,
            int cell = -1)
        {
            if (player == null || !Plugin.VisualsEnabled.Value) return false;
            using var part = RealTargets.Diagnostics.Perf.Part(RealTargets.Diagnostics.PerfPart.Stains);
            EnsureResources();
            if (_stain == null && _stainColour == null) return false;

            var baked = Bake(player);
            const float back = 0.12f;
            if (!Surface(baked, at + outward * back, -outward, 0.3f, -1, out var on)) return false;
            // Far behind the expected surface = a gap (between the legs, past the hip): not on this run.
            float depth = Vector3.Dot(at + outward * back - on.Point, outward);
            if (!atWound && (depth < back - 0.06f || depth > back + 0.08f)) return false;

            var run = Vector3.ProjectOnPlane(Vector3.down, on.Normal);
            return SpawnSkinned(player, baked, on, size, PatchKind.Stain, run.sqrMagnitude > 0.04f ? run : (Vector3?)null, cell >= 0 ? cell : WoundTextures.StainCell(tone, step));
        }

        private static int BodyLayer(Player player, int fallback)
        {
            var bodies = new List<BodyRenderer>();
            player.PlayerBody?.GetBodyRenderersNonAlloc(bodies);
            foreach (var body in bodies)
            {
                if (body.Renderers == null) continue;
                foreach (var r in body.Renderers)
                {
                    if (r != null && r.enabled) return r.gameObject.layer;
                }
            }
            return fallback;
        }

        /// <summary>Builds the wound textures and materials while the raid loads (0.10.10).</summary>
        internal void Warm() => EnsureResources();

        /// <summary>
        /// Bakes and ray-casts a real body once (your own, at spawn), then throws it away: rc-1 test, the raid's first hit
        /// spent 15.3 ms in "surfaces and patches" baking and cooking a body's skins on demand, later bodies far less, and
        /// warming a 12-triangle box at load changed nothing. Returns the ms it took.
        /// </summary>
        internal double WarmBody(Player player)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (player == null) return 0;
            EnsureResources();
            var baked = Bake(player);
            while (baked.Pending.Count > 0) BakePending(baked, baked.Pending.Count - 1);
            var top = player.Position + Vector3.up * 2.5f;
            Surface(baked, top, Vector3.down, 3f, -1, out _, bake: false);
            double ms = watch.Elapsed.TotalMilliseconds;
#if !PUBLIC
            SelfCheck(baked);
#endif
            _baked.Remove(player.Id);
            DestroyBake(baked);
            return ms;
        }

#if !PUBLIC
        /// <summary>
        /// Dev check of perf-6's own ray test against PhysX on your body at spawn: per baked skin a MeshCollider from the same
        /// vertices, 40 rays at random triangles (from 20 cm out, random side); both must hit the same point (1 mm), with the
        /// same normal, or both miss. One `[Surface] self-check` line.
        /// </summary>
        private static void SelfCheck(Baked baked)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int rays = 0, agree = 0, bothMiss = 0;
            float worst = 0f;
            string first = null;
            var state = Random.state;
            Random.InitState(1234);
            for (int i = 0; i < baked.Parts.Count; i++)
            {
                var part = baked.Parts[i];
                if (part.Vertices == null || part.Triangles == null || part.Triangles.Length < 3) continue;
                var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                if (part.Vertices.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(part.Vertices);
                mesh.SetTriangles(part.Triangles, 0);
                var go = new GameObject(Prefix + "SelfCheck") { hideFlags = HideFlags.HideAndDontSave };
                go.transform.SetPositionAndRotation(part.ToWorld.GetColumn(3), part.ToWorld.rotation);
                go.transform.localScale = part.ToWorld.lossyScale;
                var collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                Physics.SyncTransforms();
                int tris = part.Triangles.Length / 3;
                for (int r = 0; r < 40; r++)
                {
                    int k = Random.Range(0, tris) * 3;
                    var centre = part.ToWorld.MultiplyPoint3x4((part.Vertices[part.Triangles[k]] + part.Vertices[part.Triangles[k + 1]] +
                                                                part.Vertices[part.Triangles[k + 2]]) / 3f);
                    var side = Random.onUnitSphere;
                    var ray = new Ray(centre + side * 0.2f, -side);
                    rays++;
                    bool physx = collider.Raycast(ray, out var hit, 0.4f);
                    float best = float.MaxValue;
                    var ours = default(SurfaceHit);
                    ours.Part = -1;
                    Test(baked, i, ray, 0.4f, ref best, ref ours);
                    bool mine = ours.Part == i;
                    if (!physx && !mine)
                    {
                        agree++;
                        bothMiss++;
                        continue;
                    }
                    if (physx && mine)
                    {
                        float diff = Vector3.Distance(hit.point, ours.Point);
                        var physxNormal = Vector3.Dot(hit.normal, ray.direction) > 0f ? -hit.normal : hit.normal;
                        if (diff > worst) worst = diff;
                        if (diff < 0.001f && Vector3.Dot(physxNormal, ours.Normal) > 0.99f)
                        {
                            agree++;
                            continue;
                        }
                    }
                    first ??= $"{part.Source?.name}: PhysX {(physx ? $"{hit.point} tri {hit.triangleIndex}" : "miss")}, ours " +
                              $"{(mine ? $"{ours.Point} tri {ours.Triangle}" : "miss")}";
                }
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(mesh);
            }
            // perf-7: a patch grid in one pass (LayGrid) against its 25 points cast one by one as before (Surface).
            int points = 0, same = 0, patches = 0;
            float gridWorst = 0f;
            string gridFirst = null;
            var found = new bool[Grid * Grid];
            var gridWatch = System.Diagnostics.Stopwatch.StartNew();
            double layMs = 0;
            for (int attempt = 0; attempt < 200 && patches < 30; attempt++)
            {
                int pi = Random.Range(0, baked.Parts.Count);
                var part = baked.Parts[pi];
                if (part.Vertices == null || part.Triangles == null || part.Triangles.Length < 3) continue;
                int k3 = Random.Range(0, part.Triangles.Length / 3) * 3;
                var wa = part.ToWorld.MultiplyPoint3x4(part.Vertices[part.Triangles[k3]]);
                var wb = part.ToWorld.MultiplyPoint3x4(part.Vertices[part.Triangles[k3 + 1]]);
                var wc = part.ToWorld.MultiplyPoint3x4(part.Vertices[part.Triangles[k3 + 2]]);
                var face = Vector3.Cross(wb - wa, wc - wa);
                if (face.sqrMagnitude < 1e-12f) continue;
                face.Normalize();
                var mid = (wa + wb + wc) / 3f;
                if (!Surface(baked, mid + face * 0.05f, -face, 0.1f, -1, out var center, bake: false)) continue;
                patches++;
                var n = center.Normal.normalized;
                var t = Vector3.Cross(n, Mathf.Abs(n.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
                t = Quaternion.AngleAxis(Random.Range(0f, 360f), n) * t;
                var b = Vector3.Cross(n, t);
                float half = Random.Range(0.02f, 0.1f), halfU = half * Random.Range(0.6f, 2f);
                var lay = System.Diagnostics.Stopwatch.StartNew();
                LayGrid(baked, center.Part, center.Point, t, b, n, halfU, half, found);
                layMs += lay.Elapsed.TotalMilliseconds;
                for (int j = 0; j < Grid; j++)
                {
                    for (int i = 0; i < Grid; i++)
                    {
                        int k = j * Grid + i;
                        var flat = center.Point + t * ((i / (float)(Grid - 1) - 0.5f) * 2f * halfU) + b * ((j / (float)(Grid - 1) - 0.5f) * 2f * half);
                        bool cast = Surface(baked, flat + n * 0.04f, -n, 0.08f, center.Part, out var on) ||
                                    Surface(baked, flat + n * 0.04f, -n, 0.08f, -1, out on, bake: false);
                        points++;
                        if (!cast && !found[k])
                        {
                            same++;
                            continue;
                        }
                        if (cast && found[k])
                        {
                            float diff = Vector3.Distance(on.Point, GridHits[k].Point);
                            if (diff > gridWorst) gridWorst = diff;
                            if (diff < 0.001f && on.Part == GridHits[k].Part)
                            {
                                same++;
                                continue;
                            }
                        }
                        gridFirst ??= $"{part.Source?.name} point {k}: rays {(cast ? $"part {on.Part} tri {on.Triangle} {on.Point}" : "miss")}, " +
                                      $"grid {(found[k] ? $"part {GridHits[k].Part} tri {GridHits[k].Triangle} {GridHits[k].Point}" : "miss")}";
                    }
                }
            }
            Random.state = state;
            Plugin.Log.LogInfo($"[Surface] self-check: {agree}/{rays} rays agree with PhysX ({bothMiss} both miss), worst point " +
                               $"{worst * 1000f:F2} mm, backfaces {Physics.queriesHitBackfaces}, {watch.Elapsed.TotalMilliseconds:F1} ms" +
                               (first != null ? $"; first mismatch {first}" : string.Empty));
            Plugin.Log.LogInfo($"[Surface] grid self-check: {same}/{points} points of {patches} patches agree with single rays, worst " +
                               $"{gridWorst * 1000f:F2} mm, one pass {layMs / Mathf.Max(1, patches):F3} ms per patch, " +
                               $"{gridWatch.Elapsed.TotalMilliseconds:F1} ms" + (gridFirst != null ? $"; first mismatch {gridFirst}" : string.Empty));
        }
#endif

        private void EnsureResources()
        {
            if (_quad != null) return;

            _quad = new Mesh { name = "RealTargetsWoundQuad", hideFlags = HideFlags.HideAndDontSave };
            _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            _quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            _quad.colors32 = new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            // Front face toward +Z (the outward normal).
            _quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _quad.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            _quad.RecalculateBounds();

            _gridTriangles = new int[(Grid - 1) * (Grid - 1) * 6];
            int k = 0;
            for (int j = 0; j < Grid - 1; j++)
            {
                for (int i = 0; i < Grid - 1; i++)
                {
                    int a = j * Grid + i;
                    _gridTriangles[k++] = a; _gridTriangles[k++] = a + 1; _gridTriangles[k++] = a + Grid;
                    _gridTriangles[k++] = a + 1; _gridTriangles[k++] = a + Grid + 1; _gridTriangles[k++] = a + Grid;
                }
            }

            // Multiply pass darkens the cloth as lit; colour pass (alpha blended, tinted by the local
            // light level per patch, WoundLight) makes the wound readable on black cloth.
            var multiply = GroundBlood.FindShader(false, out var multiplyName);
            var blended = GroundBlood.FindShader(true, out var blendedName);
            if (multiply == null && blended == null)
            {
                Plugin.Log.LogWarning("Wound decals: no usable shader found.");
                return;
            }
            var entryTex = WoundTextures.Entry();
            var exitTex = WoundTextures.Exit();
            var stainTex = WoundTextures.Stain();
            var punctTex = WoundTextures.Puncture();
            if (multiply != null && multiplyName.Contains("Multiply"))
            {
                _punct = new Material(multiply) { mainTexture = punctTex, hideFlags = HideFlags.HideAndDontSave };
                GroundBlood.NoSoftFade(_punct);
                _entry = new Material(multiply) { mainTexture = entryTex, hideFlags = HideFlags.HideAndDontSave };
                _exit = new Material(multiply) { mainTexture = exitTex, hideFlags = HideFlags.HideAndDontSave };
                _stain = new Material(multiply) { mainTexture = stainTex, hideFlags = HideFlags.HideAndDontSave };
                GroundBlood.NoSoftFade(_stain);
                // Under the wound holes (a stain laid later must not cover the hole).
                _stain.renderQueue = 2995;
                GroundBlood.NoSoftFade(_entry);
                GroundBlood.NoSoftFade(_exit);
            }
            if (blended != null && blendedName.Contains("Alpha Blended"))
            {
                _punctColour = new Material(blended) { mainTexture = punctTex, hideFlags = HideFlags.HideAndDontSave };
                GroundBlood.NoSoftFade(_punctColour);
                _punctColour.renderQueue = 3001;
                _entryColour = new Material(blended) { mainTexture = entryTex, hideFlags = HideFlags.HideAndDontSave };
                _exitColour = new Material(blended) { mainTexture = exitTex, hideFlags = HideFlags.HideAndDontSave };
                _stainColour = new Material(blended) { mainTexture = stainTex, hideFlags = HideFlags.HideAndDontSave };
                GroundBlood.NoSoftFade(_stainColour);
                _stainColour.renderQueue = 2996;
                GroundBlood.NoSoftFade(_entryColour);
                GroundBlood.NoSoftFade(_exitColour);
                // Drawn after the multiply pass.
                _entryColour.renderQueue = 3001;
                _exitColour.renderQueue = 3001;
            }
            // Lit colour pass: same textures as albedo, glossy (wet); stains under the holes.
            // 1.2.0: the entry is mostly scraped skin (abrasion margin), only the hole is wet: less gloss than 0.7.
            _entryLit = LitBlood.Transparent(entryTex, new Color(1f, 1f, 1f, 0.9f), 0.45f, 3001, "RealTargetsWoundEntryLit", 0.35f, BloodSurface.Bodies);
            // Less gloss than a wound: torn fabric, only its soaked ring is wet.
            _punctLit = LitBlood.Transparent(punctTex, new Color(1f, 1f, 1f, 0.9f), 0.3f, 3001, "RealTargetsWoundPunctureLit", 0.35f, BloodSurface.Bodies);
            _exitLit = LitBlood.Transparent(exitTex, new Color(1f, 1f, 1f, 0.9f), 0.6f, 3001, "RealTargetsWoundExitLit", 0.35f, BloodSurface.Bodies);
            // blood-1: soaked fabric is not glossy (0.5 shone under torches).
            _stainLit = LitBlood.Transparent(stainTex, new Color(1f, 1f, 1f, 0.85f), 0.25f, 2996, "RealTargetsWoundStainLit", 0.3f, BloodSurface.Bodies);
            ShaderName = $"{(_entry != null ? multiplyName : "-")} + {(_entryColour != null ? blendedName : "-")} + lit {LitBlood.TransparentName}";
            Plugin.Log.LogInfo($"Wound decals: shaders {ShaderName}, mode {Plugin.DecalShader.Value}, {GroundBlood.SoftParticleState()}");
        }
    }

    /// <summary>
    /// Tints a wound patch's colour pass by the light falling on it (SceneLight), re-checked a few
    /// times a second while visible, so it shows in light and stays dark in the dark.
    /// </summary>
    internal sealed class WoundLight : MonoBehaviour
    {
        public PatchKind Kind;
        private Renderer _renderer;
        private Transform _anchor;
        private Vector3 _localPoint, _localNormal;
        private MaterialPropertyBlock _block;
        private float _next;
        private static readonly int TintId = Shader.PropertyToID("_TintColor");

        internal void Init(Renderer renderer, PatchKind kind, Transform anchor, Vector3 point, Vector3 normal)
        {
            _renderer = renderer;
            Kind = kind;
            _anchor = anchor;
            _localPoint = anchor.InverseTransformPoint(point);
            _localNormal = anchor.InverseTransformDirection(normal);
            _block = new MaterialPropertyBlock();
            Refresh();
        }

        private void Update()
        {
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Wounds);
            if (Time.time < _next || _renderer == null) return;
            _next = Time.time + Random.Range(0.2f, 0.35f);
            if (_renderer.isVisible) Refresh();
        }

        internal void Refresh()
        {
            // Lit mode: the shader takes the light itself.
            if (_anchor == null || WoundDecals.Lit) return;
            float light = SceneLight.At(_anchor.TransformPoint(_localPoint), _anchor.TransformDirection(_localNormal).normalized);
            // _TintColor 0.5 = texture as is (the shader doubles it).
            float t = 0.5f * SceneLight.Unlit(light) * 1.6f;
            _renderer.GetPropertyBlock(_block);
            _block.SetColor(TintId, new Color(t, t, t, 0.5f * 0.85f));
            _renderer.SetPropertyBlock(_block);
        }
    }

    /// <summary>Destroys a generated mesh together with its object (patches die with their bots).</summary>
    internal sealed class OwnedMesh : MonoBehaviour
    {
        public Mesh Mesh;

        private void OnDestroy()
        {
            if (Mesh != null) Destroy(Mesh);
        }
    }

    /// <summary>
    /// Procedural wound textures (1.2.0 realism pass): each kind is a 2x2 atlas of 128 px cells (alpha = coverage; the lit
    /// cutout shader draws above ~0.35, so soft halos are left out on purpose and crusts are speckled instead).
    /// A Forge user: holes "look unrealistic and star like (real holes don't really do that)"; until 1.1.0 every exit was
    /// the same 6-pointed star from one seed and every entry the same red disc with a wide bright halo. Now, after
    /// forensic descriptions of gunshot wounds:
    /// - skin entry: a small round hole a little under the bullet's size (skin closes back), edges turned in, a narrow
    ///   reddish-brown margin of abrasion; concentric when square-on, eccentric (wider on the side the bullet came from,
    ///   the patch stretched 1/cos of the angle) when oblique; a slightly torn variant; a jagged one for fragments.
    ///   Almost no blood on it: the bleeding's stains bring that;
    /// - skin exit: larger, irregular, edges everted, no abrasion margin; slit-like (most pistol exits), irregular,
    ///   stellate with uneven arms, or a ragged crater with torn muscle and some fat for rifle energy;
    /// - fabric: an entry puncture (round, or a cross-shaped tear as woven cloth often splits) with the grey-black bullet
    ///   wipe ring; an exit tear (ragged, or a slit) with fibres pushed outward; both soaking dark;
    /// - stains: two venous runs, a brighter arterial one with spatter, and a frothy lung one (pink foam, bubbles).
    /// Cells are picked per wound (EntryCell, ExitCell, PunctureCell, StainCell) and mapped by CellUvs.
    /// </summary>
    internal static class WoundTextures
    {
        private const int Cell = 128;

        /// <summary>Entry: hole radius and hole + margin radius, in cell units (patch side = outer diameter / (2 x EntryOuter)).</summary>
        internal const float EntryHole = 0.12f;
        internal const float EntryOuter = 0.165f;
        /// <summary>Exit: the opening's reach in cell units (patch side = exit size / (2 x ExitReach)).</summary>
        internal const float ExitReach = 0.30f;
        /// <summary>Fabric: hole reach for entries, tear reach for exits.</summary>
        internal const float PunctureHole = 0.13f;
        internal const float PunctureTear = 0.22f;

        // ---- Atlases ------------------------------------------------------------------------------

        internal static Texture2D Entry() => Atlas(EntryCellShade);
        internal static Texture2D Exit() => Atlas(ExitCellShade);
        internal static Texture2D Puncture() => Atlas(PunctureCellShade);
        internal static Texture2D Stain() => Atlas(StainCellShade);

        /// <summary>UVs of a 5x5 patch grid on atlas cell <paramref name="cell"/>; <paramref name="mirror"/> flips it across +U.</summary>
        internal static Vector2[] CellUvs(int cell, bool mirror, int grid)
        {
            var uvs = new Vector2[grid * grid];
            float cx = (cell & 1) * 0.5f, cy = (cell >> 1) * 0.5f;
            const float inset = 0.004f;
            for (int j = 0; j < grid; j++)
            {
                for (int i = 0; i < grid; i++)
                {
                    float u = i / (float)(grid - 1), v = j / (float)(grid - 1);
                    if (mirror) v = 1f - v;
                    uvs[j * grid + i] = new Vector2(cx + inset + u * (0.5f - 2f * inset), cy + inset + v * (0.5f - 2f * inset));
                }
            }
            return uvs;
        }

        // ---- Picking a cell -----------------------------------------------------------------------

        /// <summary>How much an oblique entry stretches along the shot: 1/cos of the angle to the surface normal (max 2.3).</summary>
        internal static float EntryAspect(Vector3 dir, Vector3 normal)
        {
            float cos = Mathf.Abs(Vector3.Dot(dir.normalized, normal.normalized));
            return Mathf.Clamp(1f / Mathf.Max(cos, 0.43f), 1f, 2.3f);
        }

        /// <summary>Skin entry cell: 3 fragment (jagged), 1 eccentric (oblique), 2 slightly torn (big bore, sometimes), 0 clean round.</summary>
        internal static int EntryCell(bool fragment, float calibreMm, float aspect)
        {
            if (fragment) return 3;
            if (aspect > 1.15f) return 1;
            return calibreMm >= 9f && Random.value < 0.5f ? 2 : 0;
        }

        /// <summary>Skin exit cell by energy: pistol mostly slit-like, rifle irregular or stellate, high energy or a burst a ragged crater.</summary>
        internal static int ExitCell(float energy, bool burst)
        {
            float x = Random.value;
            if (burst || energy > 2600f) return x < 0.7f ? 3 : 2;
            if (energy > 1200f) return x < 0.4f ? 1 : x < 0.75f ? 2 : 3;
            if (energy > 600f) return x < 0.45f ? 0 : x < 0.8f ? 1 : 2;
            return x < 0.65f ? 0 : 1;
        }

        /// <summary>Fabric cell: entries 0 round / 1 cross tear, exits 2 ragged / 3 slit.</summary>
        internal static int PunctureCell(bool exit) => exit ? (Random.value < 0.6f ? 2 : 3) : (Random.value < 0.65f ? 0 : 1);

        /// <summary>Stain cell: venous 0, arterial 2, a lung wound frothy at the wound (first steps) and bright below; 1 = soak blot.</summary>
        internal static int StainCell(BloodTone tone, int step)
        {
            switch (tone)
            {
                case BloodTone.Arterial: return 2;
                case BloodTone.Pulmonary: return step < 4 ? 3 : 2;
                default: return 0;
            }
        }

        /// <summary>The round soak blot (stain cell 1): a body lying in blood, a hole facing straight up or down.</summary>
        internal const int SoakCell = 1;

        // ---- Skin entry ---------------------------------------------------------------------------

        private static Color EntryCellShade(int cell, float x, float y, float r, float a)
        {
            float seed = 17.3f * (cell + 1);
            float n = Noise(x * 14f + seed, y * 14f - seed);
            float fine = Noise(x * 40f - seed, y * 40f + seed * 0.5f);
            float hole = EntryHole;
            float collar = 0.032f + 0.012f * (n - 0.5f);
            switch (cell)
            {
                case 0: // clean, concentric
                    hole *= 1f + 0.035f * Mathf.Sin(a * 3f + seed) + 0.03f * (fine - 0.5f);
                    break;
                case 1: // oblique: wide margin on the side it came from (-U)
                    hole *= 1f + 0.04f * Mathf.Sin(a * 2f + seed) + 0.03f * (fine - 0.5f);
                    collar += 0.05f * Mathf.Pow(Mathf.Max(0f, -Mathf.Cos(a)), 1.4f);
                    break;
                case 2: // slightly torn: a few short splits out of the hole
                    hole *= 1.08f + 0.04f * Mathf.Sin(a * 3f + seed);
                    float split = Mathf.Pow(Mathf.Max(0f, Mathf.Cos(a * 3f + 0.9f)), 30f) + 0.6f * Mathf.Pow(Mathf.Max(0f, Mathf.Cos(a * 2f - 1.7f)), 40f);
                    hole += 0.055f * split;
                    collar *= 1f - 0.6f * split;
                    break;
                default: // fragment: jagged, irregular margin
                    hole *= 1.05f + 0.16f * (Noise(Mathf.Cos(a) * 2.2f + seed, Mathf.Sin(a) * 2.2f) - 0.5f) + 0.08f * (fine - 0.5f) +
                            0.05f * Mathf.Sin(a * 7f + seed);
                    collar = 0.02f + 0.045f * Noise(Mathf.Cos(a) * 3f - seed, Mathf.Sin(a) * 3f + seed);
                    break;
            }
            if (r < hole * 0.72f) return BloodPalette.WoundTrack;
            // The edge is turned in: a wet, dark red lip down into the track.
            if (r < hole) return Color.Lerp(BloodPalette.WoundTrack, new Color(0.15f, 0.026f, 0.024f), (r - hole * 0.72f) / (hole * 0.28f));
            if (r < hole + collar)
            {
                float t = (r - hole) / Mathf.Max(collar, 1e-3f);
                var c = Color.Lerp(BloodPalette.Abrasion * 0.72f, BloodPalette.Abrasion, Mathf.Sqrt(t)) * (0.86f + 0.28f * fine);
                c.a = 1f;
                return c;
            }
            // A speckled crust of blood just outside the margin (cutout: some specks show).
            float crust = r - hole - collar;
            if (crust < 0.035f && fine * (1f - crust / 0.035f) > 0.42f) return new Color(0.19f, 0.03f, 0.03f, 0.8f);
            return Clear;
        }

        // ---- Skin exit ----------------------------------------------------------------------------

        private static Color ExitCellShade(int cell, float x, float y, float r, float a)
        {
            float seed = 31.7f * (cell + 1);
            float n = Noise(x * 9f + seed, y * 9f - seed);
            float fine = Noise(x * 34f - seed, y * 34f + seed);
            float sd;
            bool crater = cell == 3;
            switch (cell)
            {
                case 0: // slit with two short side splits
                {
                    float main = Capsule(x, y, -0.27f, 0.02f, 0.27f, -0.02f, 0f) - Mathf.Lerp(0.05f, 0.012f, Mathf.Abs(x) / 0.3f);
                    float s1 = Capsule(x, y, -0.09f, 0.0f, -0.13f, 0.09f, 0f) - 0.014f;
                    float s2 = Capsule(x, y, 0.11f, -0.01f, 0.16f, -0.08f, 0f) - 0.012f;
                    sd = Mathf.Min(main, Mathf.Min(s1, s2)) + 0.012f * (fine - 0.5f);
                    break;
                }
                case 1: // irregular, lobed
                {
                    float R = 0.17f + 0.035f * Mathf.Sin(a * 2f + seed) + 0.025f * Mathf.Sin(a * 3f - seed * 0.7f) + 0.015f * Mathf.Sin(a * 5f + 1.1f);
                    sd = r - R - 0.03f * (n - 0.5f) - 0.014f * (fine - 0.5f);
                    break;
                }
                case 2: // stellate, uneven: a core and four arms of different length, width and spacing
                {
                    sd = r - 0.085f - 0.015f * (fine - 0.5f);
                    float[] angles = { 0.3f, 1.75f, 3.4f, 4.6f };
                    float[] lengths = { 0.27f, 0.17f, 0.23f, 0.13f };
                    for (int k = 0; k < angles.Length; k++)
                    {
                        float ex = Mathf.Cos(angles[k]) * lengths[k], ey = Mathf.Sin(angles[k]) * lengths[k];
                        float t = Mathf.Clamp01((x * ex + y * ey) / (lengths[k] * lengths[k]));
                        float arm = Capsule(x, y, 0f, 0f, ex, ey, 0f) - Mathf.Lerp(0.05f, 0.008f, t);
                        sd = Mathf.Min(sd, arm);
                    }
                    sd += 0.012f * (fine - 0.5f);
                    break;
                }
                default: // ragged crater
                {
                    float R = 0.25f + 0.04f * Mathf.Sin(a * 2f + seed) + 0.03f * Mathf.Sin(a * 3f + 2.3f) + 0.02f * Mathf.Sin(a * 7f - seed);
                    sd = r - R - 0.05f * (n - 0.5f) - 0.02f * (fine - 0.5f);
                    break;
                }
            }

            float rim = crater ? 0.034f : 0.022f;
            if (sd < 0f)
            {
                float depth = Mathf.Clamp01(-sd / (crater ? 0.16f : 0.08f));
                // Torn muscle: striated dark red, deeper = darker.
                float fibre = Noise(x * 8f + seed, y * 46f - seed);
                var c = Color.Lerp(BloodPalette.Muscle * (0.75f + 0.45f * fibre), BloodPalette.WoundTrack, Mathf.Pow(depth, 1.3f));
                // Fat just under the skin edge of a torn (not slit) opening.
                if ((cell == 1 || crater) && -sd < 0.018f && -sd > 0.006f && Noise(x * 15f - seed, y * 15f) > 0.7f) c = BloodPalette.Fat * (0.7f + 0.25f * fine);
                c.a = 1f;
                return c;
            }
            if (sd < rim)
            {
                // Everted skin edge, ragged (tags), bloodied.
                if (sd > rim * (0.45f + 0.7f * fine)) return Clear;
                var c = BloodPalette.TornSkin * (0.72f + 0.38f * n);
                c.a = 1f;
                return c;
            }
            float margin = sd - rim;
            if (margin < 0.07f && n * (1f - margin / 0.07f) > 0.3f)
            {
                var c = Color.Lerp(BloodPalette.SoakWet, BloodPalette.VenousFilm, fine);
                c.a = 0.85f;
                return c;
            }
            return Clear;
        }

        // ---- Fabric -------------------------------------------------------------------------------

        private static Color PunctureCellShade(int cell, float x, float y, float r, float a)
        {
            float seed = 53.1f * (cell + 1);
            float n = Noise(x * 11f + seed, y * 11f - seed);
            float fine = Noise(x * 36f - seed, y * 36f + seed);
            bool exit = cell >= 2;
            float sd;
            switch (cell)
            {
                case 0:
                    sd = r - PunctureHole * (1f + 0.12f * Mathf.Sin(a * 7f + 0.4f) + 0.08f * Mathf.Sin(a * 13f) + 0.12f * (n - 0.5f));
                    break;
                case 1: // cross-shaped split of the weave
                {
                    float c1 = Capsule(x, y, -0.15f, -0.15f, 0.15f, 0.15f, 0f) - 0.022f;
                    float c2 = Capsule(x, y, -0.13f, 0.14f, 0.14f, -0.12f, 0f) - 0.02f;
                    sd = Mathf.Min(Mathf.Min(c1, c2), r - 0.06f) + 0.01f * (fine - 0.5f);
                    break;
                }
                case 2: // ragged exit tear (blood-3: harmonics 3 + 5 read as a heart every time): lobes from angular noise
                    sd = r - PunctureTear * (1f + 0.55f * (Noise(Mathf.Cos(a) * 1.7f + seed, Mathf.Sin(a) * 1.7f - seed) - 0.5f) +
                                             0.25f * (Noise(Mathf.Cos(a) * 4.5f - seed, Mathf.Sin(a) * 4.5f) - 0.5f) + 0.18f * (n - 0.5f));
                    break;
                default: // torn slit (blood-3: the clean "L" read geometric): a bent, uneven tear with a short side split, jagged
                {
                    float c1 = Capsule(x, y, -0.25f, -0.05f, -0.04f, 0.03f, 0f) - 0.03f;
                    float c2 = Capsule(x, y, -0.04f, 0.03f, 0.16f, -0.02f, 0f) - 0.022f;
                    float c3 = Capsule(x, y, 0.06f, 0.0f, 0.12f, -0.14f, 0f) - 0.012f;
                    sd = Mathf.Min(c1, Mathf.Min(c2, c3)) + 0.03f * (Noise(x * 26f + seed, y * 26f) - 0.5f) + 0.014f * (fine - 0.5f);
                    break;
                }
            }
            if (sd < 0f)
            {
                // Looking through: the under-layer (entry) or the wound under the cloth (exit).
                float depth = Mathf.Clamp01(-sd / 0.05f);
                return exit ? Color.Lerp(new Color(0.15f, 0.025f, 0.022f, 1f), BloodPalette.WoundTrack, depth)
                            : Color.Lerp(new Color(0.07f, 0.04f, 0.035f, 1f), new Color(0.02f, 0.012f, 0.01f, 1f), depth);
            }
            // Torn fibres: pushed in at an entry (short), out at an exit (longer, wetter).
            // Frayed edge: irregular tufts (angular noise), not a regular comb of spikes.
            float tuft = Noise(Mathf.Cos(a) * 5f + seed, Mathf.Sin(a) * 5f - seed) * 0.6f + Noise(Mathf.Cos(a) * 13f - seed, Mathf.Sin(a) * 13f) * 0.4f;
            float fibre = Mathf.Pow(Mathf.Clamp01((tuft - 0.45f) / 0.4f), 2f);
            float fibreLen = exit ? 0.018f + 0.05f * fibre : 0.012f + 0.03f * fibre;
            if (sd < fibreLen)
            {
                // blood-2: the fibre band read as a bright red outline ring: fibres dark, wet, only partly covering.
                if (Noise(x * 55f + seed, y * 55f) < 0.35f) return Clear;
                var c = (exit ? new Color(0.085f, 0.018f, 0.017f) : new Color(0.07f, 0.04f, 0.034f)) * (0.85f + 0.3f * fine);
                c.a = 0.95f;
                return c;
            }
            // Bullet wipe: grey-black ring around an entry.
            if (!exit && sd < fibreLen + 0.022f && fine > 0.25f)
            {
                var c = BloodPalette.BulletWipe * (0.85f + 0.3f * n);
                c.a = 0.9f;
                return c;
            }
            // Soak: blood wicking into the weave, dark, patchy toward its edge.
            // Soak: wicking into the weave, dark, its edge breaking up into speckles (cutout: no soft edge otherwise).
            float reach = exit ? 0.19f + 0.08f * n : 0.1f + 0.05f * n;
            float soak = (sd - fibreLen) / reach;
            float speck = Noise(x * 70f - seed, y * 70f + seed * 0.3f);
            if (soak < 1f && Mathf.Pow(1f - soak, 1.4f) * (0.35f + 0.5f * fine + 0.45f * speck) > 0.42f)
            {
                var c = Color.Lerp(BloodPalette.SoakWet, BloodPalette.SoakEdge, soak);
                c.a = 0.9f;
                return c;
            }
            return Clear;
        }

        // ---- Stains -------------------------------------------------------------------------------

        private static Color StainCellShade(int cell, float x, float y, float r, float a)
        {
            float seed = 71.9f * (cell + 1);
            float n = Noise(x * 8f + seed, y * 8f - seed * 0.3f);
            float fine = Noise(x * 30f - seed, y * 30f + seed);
            // Ellipse long along +U (the way blood runs) with fingers at the leading edge.
            // blood-1: kept well inside the patch (a run reaching the 5x5 grid edge was cut square) and tapering toward +U.
            // blood-2: runs broke into beads (narrow, tapered ellipses 3 cm apart): less taper, longer; cell 1 (blood-3) is a
            // round soak blot (a body lying in its blood, a hole facing up or down) instead of a second run.
            bool blot = cell == 1;
            float taper = blot ? 1f : Mathf.Lerp(1.08f, 0.82f, Mathf.Clamp01(x + 0.5f));
            float ex = x / (blot ? 0.36f : 0.44f), ey = y / ((blot ? 0.36f : 0.24f) * taper);
            float e = Mathf.Sqrt(ex * ex + ey * ey);
            float fingers = !blot && x > 0.05f ? 0.22f * Mathf.Pow(Mathf.Abs(Mathf.Sin(y * 22f + seed)), 6f) * (x / 0.44f) : 0f;
            float edge = (blot ? 0.72f + 0.3f * n : 0.74f + 0.26f * n) + 0.12f * (fine - 0.5f) + fingers;
            Color baseColour;
            switch (cell)
            {
                case 2: baseColour = BloodPalette.ArterialFilm; break;
                case 3: baseColour = BloodPalette.PulmonaryFilm; break;
                default: baseColour = BloodPalette.VenousFilm; break;
            }
            if (e > edge)
            {
                // Arterial: fine spatter around the run.
                if (cell == 2 && r < 0.47f && fine > 0.8f && n > 0.45f) return new Color(baseColour.r, baseColour.g, baseColour.b, 0.9f);
                return Clear;
            }
            float soft = Mathf.Clamp01((edge - e) / 0.18f);
            float wet = Mathf.Clamp01(1f - e / edge);
            // Thicker in the middle of the run: darker.
            var c = Color.Lerp(baseColour, baseColour * 0.62f + BloodPalette.SoakWet * 0.38f, wet);
            if (cell == 3)
            {
                // Froth: bubbles of a few sizes, denser where it leaves the wound (-U).
                // Clumps of foam near where it leaves the wound (-U), a few stray bubbles further down the run.
                float density = Mathf.Lerp(0.5f, 0.12f, Mathf.Clamp01(x + 0.5f));
                float bubbles = Bubbles(x, y, seed);
                if (bubbles > 0f && Noise(x * 5f + 3f, y * 5f) < density)
                {
                    c = Color.Lerp(c, BloodPalette.Froth, bubbles * 0.65f);
                }
            }
            c.a = Mathf.Clamp01(soft * (0.7f + 0.3f * n) * 0.95f);
            return c;
        }

        /// <summary>0..1 bubble highlight: a jittered grid of round bubbles, bright rim, darker middle.</summary>
        private static float Bubbles(float x, float y, float seed)
        {
            float best = 0f;
            for (int layer = 0; layer < 2; layer++)
            {
                float scale = layer == 0 ? 42f : 70f;
                float gx = x * scale + seed, gy = y * scale - seed;
                int ix = Mathf.FloorToInt(gx), iy = Mathf.FloorToInt(gy);
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int cx = ix + dx, cy = iy + dy;
                        float h = Hash(cx, cy, layer);
                        if (h < 0.68f) continue;
                        float px = cx + 0.5f + 0.35f * (Hash(cy, cx, layer + 7) - 0.5f), py = cy + 0.5f + 0.35f * (Hash(cx + 3, cy - 5, layer) - 0.5f);
                        float rad = 0.22f + 0.22f * h;
                        float d = Mathf.Sqrt((gx - px) * (gx - px) + (gy - py) * (gy - py)) / rad;
                        if (d > 1f) continue;
                        // Rim brighter than the middle (a thin film catching light).
                        best = Mathf.Max(best, d > 0.65f ? 0.9f : 0.45f);
                    }
                }
            }
            return best;
        }

        private static float Hash(int x, int y, int salt)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + salt * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
            }
        }

        // ---- Helpers ------------------------------------------------------------------------------

        private static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

        /// <summary>Distance from (x, y) to the segment a-b, minus <paramref name="radius"/>.</summary>
        private static float Capsule(float x, float y, float ax, float ay, float bx, float by, float radius)
        {
            float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
            float t = Mathf.Clamp01((px * dx + py * dy) / Mathf.Max(dx * dx + dy * dy, 1e-6f));
            float qx = px - dx * t, qy = py - dy * t;
            return Mathf.Sqrt(qx * qx + qy * qy) - radius;
        }

        /// <summary>A 2x2 atlas: shade(cell, x, y, r, angle) per pixel, cell coords -0.5..0.5; nothing past r 0.48 (mips stay in their cell).</summary>
        private static Texture2D Atlas(System.Func<int, float, float, float, float, Color> shade)
        {
            const int size = Cell * 2;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color[size * size];
            for (int cell = 0; cell < 4; cell++)
            {
                int ox = (cell & 1) * Cell, oy = (cell >> 1) * Cell;
                for (int j = 0; j < Cell; j++)
                {
                    for (int i = 0; i < Cell; i++)
                    {
                        float x = (i + 0.5f) / Cell - 0.5f, y = (j + 0.5f) / Cell - 0.5f;
                        float r = Mathf.Sqrt(x * x + y * y);
                        pixels[(oy + j) * size + ox + i] = r > 0.48f ? Clear : shade(cell, x, y, r, Mathf.Atan2(y, x));
                    }
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(true, false);
            return tex;
        }

        /// <summary>Cheap smooth noise 0..1.</summary>
        private static float Noise(float x, float y)
        {
            float v = Mathf.PerlinNoise(x + 37.1f, y + 11.7f) * 0.7f + Mathf.PerlinNoise(x * 3.1f + 5f, y * 3.1f + 2f) * 0.3f;
            return Mathf.Clamp01(v);
        }
    }
}
