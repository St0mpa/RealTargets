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
    /// - entry: round hole ~ calibre with a bruised collar and a little blood; exit: larger, ragged
    ///   stellate tear sized from the temporary cavity; textures are generated here (no assets).
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
        private const int MaxStainsPerBody = 120;
        private const int MaxStainsTotal = 1000;
        private const int Grid = 5;
        private const float Lift = 0.003f;

        /// <summary>
        /// Height above the cloth (0.10.3): the lit cutout pass is depth-tested, so overlapping patches
        /// must not share a plane (z-fighting). Stains lowest (each a little apart), holes above them.
        /// </summary>
        private static float LiftFor(PatchKind kind) => kind == PatchKind.Stain ? Lift + Random.Range(0f, 0.0009f) : Lift + 0.0016f;
        private const string Prefix = "RealTargetsWound";

        private sealed class BakedPart
        {
            public GameObject Go;
            public Mesh Mesh;
            public MeshCollider Collider;
            public SkinnedMeshRenderer Source;
            public Mesh SourceMesh;
            public Vector3 SourcePosition;
            public int[] Triangles;
        }

        /// <summary>
        /// A body's meshes baked into temporary colliders. 0.10.10 (4.1.6 raid: `Bleed` 8-12 ms and `Hits` 6-11 ms worst
        /// frames): kept while the skeleton stays where it was (every bone within <see cref="PoseTolerance"/>, same
        /// meshes), so the stains running down a still corpse no longer re-bake and re-cook its clothing every time;
        /// destroyed after <see cref="BakeLifetime"/> s unused. The colliders are only enabled during the frame that
        /// queries them (off at the end of every frame), so bullets, grenades and droplets never meet them.
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
        private const float BakeLifetime = 5f;
        private const int MaxBakes = 12;

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
        private Vector2[] _gridUvs;
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

        private void Awake()
        {
            Instance = this;
            HitCollector.Published += OnHit;
        }

        private void OnDestroy()
        {
            HitCollector.Published -= OnHit;
            ClearBakes();
        }

        internal void ResetRaid()
        {
            ClearBakes();
            _perBody.Clear();
            _all.Clear(); // the patches die with their bots
            _stainsPerBody.Clear();
            _allStains.Clear();
            _weights.Clear();
            foreach (var b in _batches.Values)
            {
                if (b.Go != null) Destroy(b.Go);
            }
            _batches.Clear();
            _dirty.Clear();
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
                    float size = look == EntryLook.Skin ? hole / (2f * 0.18f) : hole / (2f * 0.16f);
                    if (SpawnSkinned(player, baked, at, size, kind, null))
                    {
                        Debug(hit, $"{leg}entry ({look}) skinned on {surface}");
                    }
                    else
                    {
                        Spawn(player, bone, at.Point, at.Normal, size, kind);
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
                if (!SpawnSkinned(player, baked, at, exitSize / (2f * 0.30f), PatchKind.Exit, null))
                {
                    Spawn(player, bone, at.Point, at.Normal, exitSize / (2f * 0.30f), PatchKind.Exit);
                }
                Debug(hit, $"{leg}exit placed{(InferredExit(hit, path.Leg) ? " (inferred)" : "")}");
            }
            else
            {
                Debug(hit, $"{leg}no exit patch: no surface along the shot or toward {bone.name}");
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
                if (materials == null || materials.Length == 0 || part.Mesh == null) return "?";
                int sub = 0, first = 0;
                for (int s = 0; s < part.Mesh.subMeshCount; s++)
                {
                    int count = (int)part.Mesh.GetIndexCount(s) / 3;
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
            if (_baked.TryGetValue(player.Id, out var cached))
            {
                if (cached.Frame == Time.frameCount || StillValid(cached))
                {
                    if (cached.Frame != Time.frameCount)
                    {
                        cached.Frame = Time.frameCount;
                        foreach (var part in cached.Parts)
                        {
                            if (part.Collider != null) part.Collider.enabled = true;
                        }
                    }
                    cached.LastUsed = Time.time;
                    QueueCleanup();
                    return cached;
                }
                DestroyBake(cached);
            }

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

        /// <summary>The body has not moved since the bake: every bone where it was, every baked mesh unchanged.</summary>
        private static bool StillValid(Baked baked)
        {
            if (baked.Player == null || baked.PoseBones == null) return false;
            const float tol = PoseTolerance * PoseTolerance;
            for (int i = 0; i < baked.PoseBones.Length; i++)
            {
                var b = baked.PoseBones[i];
                if (b == null || (b.position - baked.Pose[i]).sqrMagnitude > tol) return false;
            }
            foreach (var part in baked.Parts)
            {
                // A swapped mesh (skull burst cut, limb removed), a hidden renderer or a moved renderer: bake again.
                if (part.Source == null || !part.Source.enabled || part.Source.sharedMesh != part.SourceMesh ||
                    (part.Source.transform.position - part.SourcePosition).sqrMagnitude > tol) return false;
            }
            return true;
        }

        private void QueueCleanup()
        {
            if (_cleanupQueued) return;
            _cleanupQueued = true;
            StartCoroutine(CleanupAtEndOfFrame());
        }

        private static void Test(Baked baked, int i, Ray ray, float distance, ref float bestDist, ref SurfaceHit result)
        {
            var part = baked.Parts[i];
            if (part.Collider != null && part.Collider.Raycast(ray, out var hitInfo, distance) && hitInfo.distance < bestDist)
            {
                bestDist = hitInfo.distance;
                result.Point = hitInfo.point;
                result.Normal = hitInfo.normal;
                result.Part = i;
                result.Triangle = hitInfo.triangleIndex;
                result.Bary = hitInfo.barycentricCoordinate;
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

        /// <summary>Bakes one pending mesh into a collider (cooking is the expensive part); returns its part index.</summary>
        private static int BakePending(Baked baked, int index)
        {
            var smr = baked.Pending[index];
            baked.Pending.RemoveAt(index);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var mesh = new Mesh();
            // BakeMesh(true) is fully local (scale divided out): the collider takes the skin's whole transform, scale too
            // (release check 5: a BEAR Tiger T-shirt at scale 0.1 baked 10x and wounds on it landed off the shirt).
            smr.BakeMesh(mesh, true);
            var go = new GameObject(Prefix + "Bake") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
            go.transform.localScale = smr.transform.lossyScale;
            var collider = go.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
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
                Plugin.Log.LogInfo($"[Perf] slow bake {ms:F1} ms: {baked.Player?.Profile?.Nickname} {smr.name} ({mesh.vertexCount} verts, " +
                                   $"{(baked.Player != null && baked.Player.HealthController != null && baked.Player.HealthController.IsAlive ? "alive" : "corpse")})");
            }
            baked.Parts.Add(new BakedPart
            {
                Go = go, Mesh = mesh, Collider = collider, Source = smr, SourceMesh = smr.sharedMesh, SourcePosition = smr.transform.position,
            });
            return baked.Parts.Count - 1;
        }

        /// <summary>End of the frame: colliders off; bakes unused for a while (or over the cap, oldest first) destroyed.</summary>
        private IEnumerator CleanupAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            _cleanupQueued = false;
            List<int> drop = null;
            foreach (var pair in _baked)
            {
                var b = pair.Value;
                if (b.Player == null || Time.time - b.LastUsed > BakeLifetime)
                {
                    (drop ??= new List<int>()).Add(pair.Key);
                    continue;
                }
                foreach (var part in b.Parts)
                {
                    if (part.Collider != null) part.Collider.enabled = false;
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
        }

        private static void DestroyBake(Baked baked)
        {
            foreach (var part in baked.Parts)
            {
                if (part.Go != null) DestroyImmediate(part.Go);
                if (part.Mesh != null) DestroyImmediate(part.Mesh);
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
        private bool SpawnSkinned(Player player, Baked baked, SurfaceHit center, float size, PatchKind kind, Vector3? axis)
        {
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
            float lift = LiftFor(kind);

            int count = Grid * Grid;
            var world = new Vector3[count];
            var normals = new Vector3[count];
            var colors = new Color32[count];
            var vertexWeights = new List<(Transform bone, float w)>[count];
            for (int j = 0; j < Grid; j++)
            {
                for (int i = 0; i < Grid; i++)
                {
                    int k = j * Grid + i;
                    var flat = center.Point + t * ((i / (float)(Grid - 1) - 0.5f) * 2f * half) + b * ((j / (float)(Grid - 1) - 0.5f) * 2f * half);
                    List<(Transform, float)> w = null;
                    if (Surface(baked, flat + n * 0.04f, -n, 0.08f, center.Part, out var on) ||
                        Surface(baked, flat + n * 0.04f, -n, 0.08f, -1, out on, bake: false))
                    {
                        w = Weights(baked, on);
                    }
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
                batch.Uvs.Add(_gridUvs[v]);
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

            if (part.Triangles == null) part.Triangles = part.Mesh.triangles;
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

        private void Spawn(Player player, Transform bone, Vector3 at, Vector3 normal, float size, PatchKind kind)
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
            go.AddComponent<MeshFilter>().sharedMesh = _quad;
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
        internal bool Stain(Player player, Vector3 at, Vector3 outward, float size, bool atWound)
        {
            if (player == null || !Plugin.VisualsEnabled.Value) return false;
            EnsureResources();
            if (_stain == null && _stainColour == null) return false;

            var baked = Bake(player);
            const float back = 0.12f;
            if (!Surface(baked, at + outward * back, -outward, 0.3f, -1, out var on)) return false;
            // Far behind the expected surface = a gap (between the legs, past the hip): not on this run.
            float depth = Vector3.Dot(at + outward * back - on.Point, outward);
            if (!atWound && (depth < back - 0.06f || depth > back + 0.08f)) return false;

            var run = Vector3.ProjectOnPlane(Vector3.down, on.Normal);
            return SpawnSkinned(player, baked, on, size, PatchKind.Stain, run.sqrMagnitude > 0.04f ? run : (Vector3?)null);
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
        /// Bakes, cooks and ray-casts a real body once (your own, at spawn), then throws it away: rc-1 test, the raid's
        /// first hit spent 15.3 ms in "surfaces and patches" baking and cooking a body's skins on demand, later bodies far
        /// less, and warming a 12-triangle box at load changed nothing. Returns the ms it took.
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
            _baked.Remove(player.Id);
            DestroyBake(baked);
            return watch.Elapsed.TotalMilliseconds;
        }

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

            _gridUvs = new Vector2[Grid * Grid];
            for (int j = 0; j < Grid; j++)
            {
                for (int i = 0; i < Grid; i++) _gridUvs[j * Grid + i] = new Vector2(i / (float)(Grid - 1), j / (float)(Grid - 1));
            }
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
            _entryLit = LitBlood.Transparent(entryTex, new Color(1f, 1f, 1f, 0.9f), 0.7f, 3001, "RealTargetsWoundEntryLit");
            // Less gloss than a wound: torn fabric, only its soaked ring is wet.
            _punctLit = LitBlood.Transparent(punctTex, new Color(1f, 1f, 1f, 0.9f), 0.35f, 3001, "RealTargetsWoundPunctureLit");
            _exitLit = LitBlood.Transparent(exitTex, new Color(1f, 1f, 1f, 0.9f), 0.7f, 3001, "RealTargetsWoundExitLit");
            _stainLit = LitBlood.Transparent(stainTex, new Color(1f, 1f, 1f, 0.85f), 0.5f, 2996, "RealTargetsWoundStainLit", 0.3f);
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

    /// <summary>Procedural wound textures (128 px RGBA, alpha = coverage).</summary>
    internal static class WoundTextures
    {
        private const int Size = 128;

        /// <summary>Entry: dark round hole (r 0.18), abrasion collar, thin blood halo.</summary>
        internal static Texture2D Entry()
        {
            var rng = new System.Random(1234);
            return Make((x, y, r, a) =>
            {
                float n = Noise(x * 9f, y * 9f, rng);
                float hole = 0.18f + 0.015f * Mathf.Sin(a * 5f + 1.3f);
                if (r < hole * 0.75f) return new Color(0.03f, 0.005f, 0.005f, 1f);
                if (r < hole) return Color.Lerp(new Color(0.03f, 0.005f, 0.005f, 1f), new Color(0.22f, 0.03f, 0.02f, 1f), (r - hole * 0.75f) / (hole * 0.25f));
                if (r < hole + 0.07f) return new Color(0.36f, 0.09f, 0.06f, 1f) * (0.85f + 0.3f * n);
                float halo = 1f - Mathf.InverseLerp(hole + 0.07f, 0.5f + 0.08f * n, r);
                return new Color(0.42f, 0.03f, 0.03f, Mathf.Clamp01(halo * 0.9f * (0.6f + 0.6f * n)));
            });
        }

        /// <summary>
        /// Puncture (1.1.0 retest 4): a round through fabric or gear. A black hole (r ~0.16) with a frayed edge of torn
        /// fibres and a dark wet ring where blood soaked the cloth around it; no bright red halo. The bleeding's stains
        /// add the blood that runs from it.
        /// </summary>
        internal static Texture2D Puncture()
        {
            var rng = new System.Random(2468);
            return Make((x, y, r, a) =>
            {
                float n = Noise(x * 11f, y * 11f, rng);
                float edge = 0.16f + 0.025f * Mathf.Sin(a * 7f + 0.4f) + 0.015f * Mathf.Sin(a * 13f) + 0.02f * n;
                if (r < edge * 0.7f) return new Color(0.02f, 0.012f, 0.01f, 1f);
                if (r < edge) return Color.Lerp(new Color(0.02f, 0.012f, 0.01f, 1f), new Color(0.09f, 0.04f, 0.035f, 1f), (r - edge * 0.7f) / (edge * 0.3f));
                // Torn fibres: thin dark streaks sticking out past the edge.
                float fibre = Mathf.Pow(Mathf.Abs(Mathf.Sin(a * 17f + 2.1f * n)), 18f);
                if (r < edge + 0.05f + 0.06f * fibre) return new Color(0.13f, 0.06f, 0.05f, 0.95f) * (0.85f + 0.3f * n);
                float soak = 1f - Mathf.InverseLerp(edge + 0.05f, 0.34f + 0.08f * n, r);
                return new Color(0.2f, 0.03f, 0.025f, Mathf.Clamp01(soak * 0.85f * (0.7f + 0.5f * n)));
            });
        }

        /// <summary>Exit: ragged stellate tear (r ~0.30 with 6 lobes), everted torn edges, blood around.</summary>
        internal static Texture2D Exit()
        {
            var rng = new System.Random(4321);
            return Make((x, y, r, a) =>
            {
                float n = Noise(x * 7f, y * 7f, rng);
                float tear = 0.24f + 0.09f * Mathf.Pow(Mathf.Abs(Mathf.Sin(a * 3f + 0.7f)), 3f) + 0.04f * Mathf.Sin(a * 11f) + 0.03f * n;
                if (r < tear * 0.6f) return new Color(0.07f, 0.0f, 0.0f, 1f);
                if (r < tear) return Color.Lerp(new Color(0.25f, 0.02f, 0.02f, 1f), new Color(0.62f, 0.16f, 0.15f, 1f), (r - tear * 0.6f) / (tear * 0.4f)) * (0.8f + 0.4f * n);
                if (r < tear + 0.05f) return new Color(0.5f, 0.1f, 0.09f, 1f);
                float halo = 1f - Mathf.InverseLerp(tear + 0.05f, 0.5f, r);
                float speck = n > 0.72f && r < 0.5f ? 0.9f : halo * 0.85f;
                return new Color(0.4f, 0.02f, 0.02f, Mathf.Clamp01(speck));
            });
        }

        /// <summary>
        /// Stain (0.10.0): a soaked, irregular blot stretched along +U (the way blood runs), wetter
        /// and darker in the middle, a few runs at the leading edge, ragged soft rim.
        /// </summary>
        internal static Texture2D Stain()
        {
            var rng = new System.Random(777);
            return Make((x, y, r, a) =>
            {
                float n = Noise(x * 8f + 3f, y * 8f + 1f, rng);
                // Ellipse: long along x, with fingers running toward +x.
                float ex = x / 0.5f, ey = y / 0.3f;
                float e = Mathf.Sqrt(ex * ex + ey * ey);
                float finger = x > 0.05f ? 0.25f * Mathf.Pow(Mathf.Abs(Mathf.Sin(y * 22f + 1.7f)), 6f) * (x / 0.5f) : 0f;
                float edge = 0.78f + 0.2f * n + finger;
                if (e > edge) return new Color(0f, 0f, 0f, 0f);
                float soft = Mathf.Clamp01((edge - e) / 0.18f);
                float wet = Mathf.Clamp01(1f - e / edge);
                var col = Color.Lerp(new Color(0.45f, 0.04f, 0.035f), new Color(0.3f, 0.015f, 0.015f), wet);
                col.a = Mathf.Clamp01(soft * (0.7f + 0.3f * n) * 0.95f);
                return col;
            });
        }

        private static Texture2D Make(System.Func<float, float, float, float, Color> shade)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color[Size * Size];
            for (int j = 0; j < Size; j++)
            {
                for (int i = 0; i < Size; i++)
                {
                    float x = (i + 0.5f) / Size - 0.5f, y = (j + 0.5f) / Size - 0.5f;
                    float r = Mathf.Sqrt(x * x + y * y);
                    var c = r > 0.5f ? new Color(0f, 0f, 0f, 0f) : shade(x, y, r, Mathf.Atan2(y, x));
                    pixels[j * Size + i] = c;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(true, false);
            return tex;
        }

        /// <summary>Cheap value noise 0..1 (smooth enough at 128 px).</summary>
        private static float Noise(float x, float y, System.Random rng)
        {
            float v = Mathf.PerlinNoise(x + 37.1f, y + 11.7f) * 0.7f + Mathf.PerlinNoise(x * 3.1f + 5f, y * 3.1f + 2f) * 0.3f;
            return Mathf.Clamp01(v);
        }
    }
}
