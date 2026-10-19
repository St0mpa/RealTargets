using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.UI;
using EFT.UI.SessionEnd;
using HarmonyLib;
using RealTargets.Autopsy;
using RealTargets.Gore;
using RealTargets.Hits;
using SPT.Reflection.Patching;
using UnityEngine;

namespace RealTargets.Features
{
    /// <summary>
    /// The death screen (1.1.0, Stompa 2026-10-19: "if I die in the raid, the same effects are shown on my PMC on the after-raid
    /// summary screen as we deploy on NPCs in raid"). Our wounds and gore never run on the local player in the raid, so the
    /// hits on YOU are recorded bone-relative (entry and exit points, armour, energy) and the gore rules for bots are
    /// evaluated at your death (record only: nothing changes in the raid). On EFT's raid results after a death
    /// (`SessionResultExitStatus.Show` -> `PlayerModelView.Show(lastPlayerState)`, a fresh PlayerBody in its own idle pose)
    /// the head or limbs come off (skin cut + cap, as on bots) and wound, puncture and blood patches are laid on that model's
    /// own surface, found by ray against its baked skins. All of it is undone before EFT disposes the model.
    /// Not shown: skull bursts as a crater (a large exit instead), blast-only limb loss, blood pools. The X-ray and the shot
    /// list are DeathReport's AUTOPSY switch; the gore decided here goes into your autopsy record too.
    /// </summary>
    internal sealed class DeathScreen : MonoBehaviour
    {
        private struct Mark
        {
            public string Bone;
            public Vector3 Local, LocalDir;
            public bool Exit, Armour, Burst;
            public float Size, Energy;
        }

        private const int MaxMarks = 80;
        private static readonly List<Mark> Marks = new List<Mark>();
        private static readonly HashSet<string> Severed = new HashSet<string>();
        private static readonly List<(string bone, float time, string what, string why)> HeavyLimbHits = new List<(string, float, string, string)>();
        private static readonly List<WoundPath> Paths = new List<WoundPath>(2);
        private static string _headBone;
        private static bool _headGone, _died;
        private static GameWorld _world;
        private static bool _failed;

        private static readonly EBodyPartColliderType[] Limbs =
        {
            EBodyPartColliderType.LeftUpperArm, EBodyPartColliderType.RightUpperArm, EBodyPartColliderType.LeftForearm,
            EBodyPartColliderType.RightForearm, EBodyPartColliderType.LeftThigh, EBodyPartColliderType.RightThigh,
            EBodyPartColliderType.LeftCalf, EBodyPartColliderType.RightCalf,
        };

        internal static DeathScreen Instance { get; private set; }

        // The decorated preview and what was done to it.
        private PlayerModelView _view;
        private Dismemberment.PreviewChanges _changes;
        private readonly List<GameObject> _patches = new List<GameObject>();
        private Coroutine _pending;

        private void Awake()
        {
            Instance = this;
        }

        internal static void Enable()
        {
            HitCollector.Published += OnHit;
            HitCollector.PublishedVolley += OnVolley;
            Player.OnPlayerDeadStatic += OnDead;
            new ShowPatch().Enable();
            new DestroyPatch().Enable();
        }

        /// <summary>A new raid: forget the last one's marks (they stay through the raid end for the results screen).</summary>
        internal static void RaidStarted()
        {
            var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (ReferenceEquals(world, _world)) return;
            _world = world;
            AutopsyRecords.ForgetLocal(world);
            DeathReport.RaidStarted();
            Marks.Clear();
            Severed.Clear();
            HeavyLimbHits.Clear();
            _headGone = false;
            _died = false;
            _headBone = null;
        }

        // ---- Recording (in the raid) -------------------------------------------------------------

        private static void OnHit(HitEvent hit)
        {
            if (_failed || hit == null || !hit.IsLocalPlayer || hit.Target == null || hit.IsBlast) return;
            try
            {
                RaidStarted();
                var colliders = hit.Target.PlayerBones?.BodyPartCollidersDictionary;
                if (colliders == null) return;
                _headBone ??= hit.Target.PlayerBones?.Head?.Original?.name;
                float hole = WoundDecals.HoleDiameter(hit);
                WoundPath.ResolveAll(hit, Paths);
                foreach (var path in Paths)
                {
                    if (Marks.Count >= MaxMarks) break;
                    if (!path.FromInside && colliders.TryGetValue(path.Entry.Collider, out var inBpc) && inBpc?.TransformCached != null)
                    {
                        var bone = inBpc.TransformCached;
                        Marks.Add(new Mark
                        {
                            Bone = bone.name, Local = bone.InverseTransformPoint(path.Start), LocalDir = bone.InverseTransformDirection(path.Direction),
                            Armour = path.Entry.Armour.Any, Size = hole, Energy = path.Energy,
                        });
                    }
                    if (path.Exited && colliders.TryGetValue(path.ExitCollider, out var outBpc) && outBpc?.TransformCached != null)
                    {
                        var bone = outBpc.TransformCached;
                        float energy = hit.ImpactEnergy > 0f ? path.Energy : 500f;
                        float cavity = Mathf.Clamp(0.008f + Plugin.CavityScale.Value * 0.0005f * Mathf.Sqrt(energy), 0.008f, 0.06f);
                        Marks.Add(new Mark
                        {
                            Bone = bone.name, Local = bone.InverseTransformPoint(path.End), LocalDir = bone.InverseTransformDirection(path.Direction),
                            Exit = true, Size = Mathf.Max(hole * 1.5f, cavity * 1.1f), Energy = path.Energy,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                Plugin.Log.LogError($"[Death screen] recording disabled after error: {ex}");
            }
        }

        /// <summary>The bots' head and limb rules (Dismemberment.TryHead/TryLimbs), for your killing volley and later hits on your body.</summary>
        private static void OnVolley(HitVolley volley)
        {
            if (_failed || volley == null || volley.Count == 0 || !volley.First.IsLocalPlayer || !Plugin.GoreEnabled.Value) return;
            try
            {
                RaidStarted();
                var colliders = volley.Target?.PlayerBones?.BodyPartCollidersDictionary;
                if (colliders == null) return;
                bool alive = volley.Status == HitStatus.Alive;
                int pellets = 0;
                foreach (var p in volley.Projectiles)
                {
                    if (!WoundPath.Resolve(p, out var path)) continue;
                    var c = path.Entry.Collider;
                    if (Array.IndexOf(Limbs, c) >= 0 && Dismemberment.HeavyCalibre(p.Caliber) && path.Energy >= Plugin.LimbEnergy.Value &&
                        colliders.TryGetValue(c, out var bpc) && bpc?.TransformCached != null)
                    {
                        // A volley is published 50 ms+ after its hits: one you were alive for can arrive after your death.
                        string what = $"{AutopsyText.Collider(c, true)} severed", limbWhy = $"{p.AmmoName} {path.Energy:F0} J";
                        if (alive && !_died) HeavyLimbHits.Add((bpc.TransformCached.name, Time.time, what, limbWhy));
                        else Sever(bpc.TransformCached.name, what, limbWhy);
                    }
                    if (alive || _headGone || !WoundPath.IsHead(c)) continue;
                    pellets++;
                    float calibre = WoundDecals.Calibre(p.Caliber);
                    bool single = p.ProjectileCount <= 1;
                    // The same rules and reasons as Dismemberment.TryHead (the autopsy's GORE lines).
                    string why = null;
                    if (single && calibre >= 12f && !Dismemberment.PistolCalibre(p.Caliber) && path.Energy >= Plugin.HeadLargeCalibreEnergy.Value)
                        why = $"{p.AmmoName} {calibre:F1} mm, {path.Energy:F0} J";
                    else if (path.Energy >= Plugin.HeadAnyEnergy.Value)
                        why = $"{p.AmmoName} {path.Energy:F0} J";
                    else if (!single && pellets >= Plugin.HeadPellets.Value && p.Distance <= Plugin.ShotgunRange.Value)
                        why = $"{pellets} pellets ({p.AmmoName}) at {p.Distance:F1} m";
                    if (why != null)
                    {
                        _headGone = true;
                        AutopsyRecords.NoteLocalGore("Head destroyed", why);
                    }
                    else if (single && path.Energy >= Plugin.SkullBurstEnergy.Value && colliders.TryGetValue(c, out var head) && head?.TransformCached != null)
                    {
                        AutopsyRecords.NoteLocalGore("Skull burst open", $"{p.AmmoName} {path.Energy:F0} J");
                        var bone = head.TransformCached;
                        var exit = path.Exited ? path.End : path.Start + path.Direction * 0.12f;
                        Marks.Add(new Mark
                        {
                            Bone = bone.name, Local = bone.InverseTransformPoint(exit), LocalDir = bone.InverseTransformDirection(path.Direction),
                            Exit = true, Burst = true, Size = 0.075f, Energy = path.Energy,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                Plugin.Log.LogError($"[Death screen] recording disabled after error: {ex}");
            }
        }

        private static void Sever(string bone, string what, string why)
        {
            if (Severed.Add(bone)) AutopsyRecords.NoteLocalGore(what, why);
        }

        /// <summary>Your bones our rules took at your death (head, severed limbs): DeathReport collapses them in your X-ray.</summary>
        internal static List<string> GoneBones()
        {
            var gone = new List<string>(Severed);
            if (_headGone && _headBone != null) gone.Add(_headBone);
            return gone;
        }

        private static void OnDead(Player player, IPlayer aggressor, DamageInfo damageInfo, EBodyPart part)
        {
            if (_failed || player == null || !player.IsYourPlayer) return;
            RaidStarted();
            _died = true;
            // Heavy limb hits just before the death take the limb (Dismemberment.LateDeathWindow on bots).
            foreach (var (bone, time, what, why) in HeavyLimbHits)
            {
                if (Time.time - time <= 0.5f) Sever(bone, what, why);
            }
            HeavyLimbHits.Clear();
            Plugin.Log.LogInfo($"[Death screen] you died: {Marks.Count} wound mark(s){(_headGone ? ", head destroyed" : "")}" +
                               $"{(Severed.Count > 0 ? $", severed: {string.Join(", ", Severed)}" : "")}");
        }

        // ---- The results screen -------------------------------------------------------------------

        private sealed class ShowPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SessionResultExitStatus), nameof(SessionResultExitStatus.Show),
                new[] { typeof(Profile), typeof(PlayerVisualRepresentation), typeof(ESideType), typeof(ExitStatus), typeof(TimeSpan), typeof(IEftSession), typeof(bool) });

            [PatchPostfix]
            private static void Postfix(SessionResultExitStatus __instance, ExitStatus exitStatus)
            {
                try
                {
                    if (exitStatus != ExitStatus.Killed || !Plugin.DeathScreenWounds.Value || Instance == null) return;
                    if (!_died || (Marks.Count == 0 && !_headGone && Severed.Count == 0)) return;
                    Instance.Begin(__instance._playerModelView);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Death screen] {ex.Message}");
                }
            }
        }

        /// <summary>EFT destroys the model in PlayerModelView.Destroy (Close calls it): put it back first.</summary>
        private sealed class DestroyPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(PlayerModelView), nameof(PlayerModelView.Destroy));

            [PatchPrefix]
            private static void Prefix(PlayerModelView __instance)
            {
                try
                {
                    if (Instance != null && Instance._view == __instance) Instance.Undo();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Death screen] undo: {ex.Message}");
                }
            }
        }

        private void Begin(PlayerModelView view)
        {
            if (view == null) return;
            Undo();
            if (_pending != null) StopCoroutine(_pending);
            _view = view;
            _pending = StartCoroutine(WhenLoaded(view));
        }

        private IEnumerator WhenLoaded(PlayerModelView view)
        {
            float until = Time.realtimeSinceStartup + 20f;
            while (view != null && (!view.LoadingComplete || view.PlayerBody == null))
            {
                if (Time.realtimeSinceStartup > until)
                {
                    Plugin.Log.LogWarning("[Death screen] your PMC model did not load within 20 s");
                    yield break;
                }
                yield return null;
            }
            // One more frame: the poser has put the model in its idle pose.
            yield return null;
            _pending = null;
            if (view == null || view != _view || view.PlayerBody == null) yield break;
            try
            {
                Decorate(view.PlayerBody);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Death screen] {ex}");
                Undo();
            }
        }

        private void Undo()
        {
            _changes?.Undo();
            _changes = null;
            foreach (var go in _patches)
            {
                if (go != null) Destroy(go);
            }
            _patches.Clear();
            _view = null;
        }

        // ---- Drawing on the preview ---------------------------------------------------------------

        private sealed class Part
        {
            public SkinnedMeshRenderer Source;
            public MeshCollider Collider;
            public Mesh Mesh;
        }

        private void Decorate(PlayerBody body)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var bones = new Dictionary<string, Transform>();
            foreach (var t in body.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("Base Human") && !bones.ContainsKey(t.name)) bones.Add(t.name, t);
            }
            var skins = GoreMeshes.Lod0Skins(body);
            if (skins.Count == 0)
            {
                Plugin.Log.LogWarning("[Death screen] no skins on your PMC model");
                return;
            }
            int layer = skins[0].gameObject.layer;
            var front = body.transform.forward;
            _changes = new Dismemberment.PreviewChanges();
            var notes = new List<string>();

            // Gore first: the bake then sees the cut skins and collapsed parts.
            if (_headGone && _headBone != null && bones.TryGetValue(_headBone, out var head))
            {
                notes.Add($"head destroyed ({Dismemberment.CutPreview(body, head, true, front, layer, _changes)})");
            }
            foreach (var name in Severed)
            {
                if (!bones.TryGetValue(name, out var limb) || Gone(limb)) continue;
                notes.Add($"{name.Replace("Base Human", "")} severed ({Dismemberment.CutPreview(body, limb, false, front, layer, _changes)})");
            }

            var parts = Bake(GoreMeshes.Lod0Skins(body));
            int wounds = 0, stains = 0, missed = 0;
            var decals = WoundDecals.Instance;
            try
            {
                foreach (var m in Marks)
                {
                    if (!bones.TryGetValue(m.Bone, out var bone) || Gone(bone)) continue;
                    var point = bone.TransformPoint(m.Local);
                    var dir = bone.TransformDirection(m.LocalDir).normalized;
                    var from = m.Exit ? point + dir * 0.3f : point - dir * 0.3f;
                    if (!Cast(parts, from, m.Exit ? -dir : dir, 0.6f, out var hit, out var part) && !TowardBone(parts, point, bone, out hit, out part))
                    {
                        missed++;
                        continue;
                    }
                    PatchKind kind;
                    float size;
                    if (m.Exit)
                    {
                        kind = PatchKind.Exit;
                        size = m.Size / (2f * 0.30f) * (m.Burst ? 2.2f : 1f);
                    }
                    else
                    {
                        kind = WoundDecals.EntryKindFor(part.Source.name, MaterialAt(part, hit.triangleIndex), m.Armour);
                        size = kind == PatchKind.Entry ? m.Size / (2f * 0.18f) : m.Size / (2f * 0.16f);
                    }
                    var normal = Vector3.Dot(hit.normal, dir) > 0f == !m.Exit ? -hit.normal : hit.normal;
                    if (Patch(parts, bone, hit.point, normal, size, kind, null, layer, decals)) wounds++;

                    // The blood that ran from it down the clothes, in the model's standing pose.
                    var down = Vector3.ProjectOnPlane(Vector3.down, normal);
                    if (down.sqrMagnitude < 0.01f) continue;
                    down.Normalize();
                    float stain = Mathf.Clamp(0.06f + m.Energy / 40000f, 0.06f, 0.16f) * (m.Burst ? 1.4f : 1f);
                    var below = hit.point + down * (stain * 0.35f);
                    if (Cast(parts, below + normal * 0.04f, -normal, 0.08f, out var on, out _) &&
                        Patch(parts, bone, on.point, normal, stain, PatchKind.Stain, down, layer, decals))
                    {
                        stains++;
                    }
                }
            }
            finally
            {
                foreach (var p in parts)
                {
                    if (p.Collider != null) Destroy(p.Collider.gameObject);
                    if (p.Mesh != null) Destroy(p.Mesh);
                }
            }
            Plugin.Log.LogInfo($"[Death screen] your PMC: {wounds} wound(s), {stains} stain(s){(missed > 0 ? $", {missed} mark(s) found no surface" : "")}" +
                               $"{(notes.Count > 0 ? $"; {string.Join("; ", notes)}" : "")}; {watch.Elapsed.TotalMilliseconds:F0} ms");
        }

        /// <summary>A bone collapsed by a cut here (it, or a parent, scaled to ~0).</summary>
        private static bool Gone(Transform bone)
        {
            for (var t = bone; t != null; t = t.parent)
            {
                if (t.localScale.x < 0.05f) return true;
            }
            return false;
        }

        /// <summary>Each skin baked in world space into a mesh collider of its own (submeshes kept for the material lookup).</summary>
        private static List<Part> Bake(List<SkinnedMeshRenderer> skins)
        {
            var parts = new List<Part>();
            foreach (var smr in skins)
            {
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                var v = baked.vertices;
                var t = smr.transform;
                for (int i = 0; i < v.Length; i++) v[i] = t.TransformPoint(v[i]);
                var world = new Mesh { name = "RealTargetsDeathScreenBake", indexFormat = baked.indexFormat };
                world.vertices = v;
                world.subMeshCount = baked.subMeshCount;
                for (int s = 0; s < baked.subMeshCount; s++) world.SetTriangles(baked.GetTriangles(s), s, false);
                world.RecalculateBounds();
                Destroy(baked);
                var go = new GameObject("RealTargetsDeathScreenCollider") { layer = 2 };
                var collider = go.AddComponent<MeshCollider>();
                collider.sharedMesh = world;
                parts.Add(new Part { Source = smr, Collider = collider, Mesh = world });
            }
            return parts;
        }

        private static bool Cast(List<Part> parts, Vector3 from, Vector3 dir, float distance, out RaycastHit best, out Part bestPart)
        {
            best = default;
            bestPart = null;
            float bestDist = float.MaxValue;
            var ray = new Ray(from, dir);
            foreach (var p in parts)
            {
                if (p.Collider == null || !p.Collider.Raycast(ray, out var h, distance) || h.distance >= bestDist) continue;
                best = h;
                bestDist = h.distance;
                bestPart = p;
            }
            return bestPart != null;
        }

        private static bool TowardBone(List<Part> parts, Vector3 point, Transform bone, out RaycastHit hit, out Part part)
        {
            hit = default;
            part = null;
            var inward = bone.position - point;
            float depth = inward.magnitude;
            if (depth < 0.005f) return false;
            inward /= depth;
            return Cast(parts, point - inward * 0.05f, inward, depth + 0.05f, out hit, out part);
        }

        private static string MaterialAt(Part part, int triangle)
        {
            var materials = part.Source.sharedMaterials;
            if (materials == null || materials.Length == 0 || part.Mesh == null) return "?";
            int first = 0;
            for (int s = 0; s < part.Mesh.subMeshCount; s++)
            {
                int count = (int)part.Mesh.GetIndexCount(s) / 3;
                if (triangle < first + count) return materials[Mathf.Min(s, materials.Length - 1)]?.name ?? "?";
                first += count;
            }
            return "?";
        }

        private const int Grid = 5;
        private const float Lift = 0.003f;

        /// <summary>
        /// A patch laid over the model's surface: a 5x5 grid around the point, each vertex dropped onto the nearest skin
        /// below it, kept on <paramref name="bone"/> (the model stands still in its idle pose, so it needs no skinning).
        /// </summary>
        private bool Patch(List<Part> parts, Transform bone, Vector3 centre, Vector3 normal, float size, PatchKind kind, Vector3? axis,
            int layer, WoundDecals decals)
        {
            if (decals == null) return false;
            var materials = decals.MaterialsFor(kind);
            if (materials == null || materials.Length == 0 || materials[0] == null) return false;

            var n = normal.normalized;
            Vector3 t = axis.HasValue ? Vector3.ProjectOnPlane(axis.Value, n) : Vector3.zero;
            if (t.sqrMagnitude < 0.01f)
            {
                t = Vector3.Cross(n, Mathf.Abs(n.y) < 0.95f ? Vector3.up : Vector3.right);
                t = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), n) * t;
            }
            t.Normalize();
            var b = Vector3.Cross(n, t);
            float half = size * UnityEngine.Random.Range(0.9f, 1.15f) * 0.5f;
            float lift = kind == PatchKind.Stain ? Lift : Lift + 0.0016f;

            var vertices = new Vector3[Grid * Grid];
            var normals = new Vector3[Grid * Grid];
            var uvs = new Vector2[Grid * Grid];
            var colors = new Color32[Grid * Grid];
            for (int j = 0; j < Grid; j++)
            {
                for (int i = 0; i < Grid; i++)
                {
                    int k = j * Grid + i;
                    var flat = centre + t * ((i / (float)(Grid - 1) - 0.5f) * 2f * half) + b * ((j / (float)(Grid - 1) - 0.5f) * 2f * half);
                    Vector3 at = flat, up = n;
                    if (Cast(parts, flat + n * 0.04f, -n, 0.08f, out var h, out _))
                    {
                        at = h.point;
                        up = Vector3.Dot(h.normal, n) < 0f ? -h.normal : h.normal;
                    }
                    vertices[k] = bone.InverseTransformPoint(at + up * lift);
                    normals[k] = bone.InverseTransformDirection(up);
                    uvs[k] = new Vector2(i / (float)(Grid - 1), j / (float)(Grid - 1));
                    colors[k] = new Color32(255, 255, 255, 255);
                }
            }
            var tris = new int[(Grid - 1) * (Grid - 1) * 6];
            int q = 0;
            for (int j = 0; j < Grid - 1; j++)
            {
                for (int i = 0; i < Grid - 1; i++)
                {
                    int a = j * Grid + i;
                    tris[q++] = a; tris[q++] = a + 1; tris[q++] = a + Grid;
                    tris[q++] = a + 1; tris[q++] = a + Grid + 1; tris[q++] = a + Grid;
                }
            }
            var mesh = new Mesh { name = "RealTargetsDeathScreenPatch" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.colors32 = colors;
            mesh.triangles = tris;
            mesh.RecalculateBounds();

            var go = new GameObject($"RealTargetsDeathScreen{kind}") { layer = layer };
            go.transform.SetParent(bone, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<OwnedMesh>().Mesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _patches.Add(go);
            return true;
        }
    }
}
