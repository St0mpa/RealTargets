using System;
using System.Collections.Generic;
using EFT;
using EFT.Visual;
using RealTargets.Hits;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealTargets.Autopsy
{
    /// <summary>
    /// The autopsy's 3D body: a translucent copy of the body in its death pose with every shot drawn through it.
    /// Snapshot: DeathPose is applied to the corpse, EFT's LOD0 body skins (body, feet, head, hands: no gear) are
    /// baked and every hit's bone-relative point is read, then the live pose is put back, all in one call.
    /// Render: a private camera that sees no scene layer (culling mask 0) draws only our command buffer into a
    /// render texture, on demand (open, drag, zoom, hover), never every frame. Unlit "X-ray" look: vertex colours
    /// from the body part's health at death, brighter where the surface turns away from the view (no lights).
    /// </summary>
    internal sealed class AutopsyModel : IDisposable
    {
        internal const int Width = 380;
        internal const int Height = 420;
        private const int Supersample = 2;
        private const float Fov = 30f;

        private static readonly Vector3 Remote = new Vector3(0f, 6000f, 0f);
        private static readonly Color Background = new Color(0.035f, 0.04f, 0.045f, 1f);
        private static readonly Color Healthy = new Color(0.55f, 0.75f, 0.92f);
        private static readonly Color Hurt = new Color(1f, 0.25f, 0.18f);
        private static readonly Color Destroyed = new Color(0.65f, 0.04f, 0.03f);
        internal static readonly Color Yours = new Color(1f, 0.77f, 0.25f, 1f);
        internal static readonly Color Others = new Color(0.55f, 0.75f, 1f, 1f);
        private static readonly Color Fatal = new Color(0.95f, 0.15f, 0.1f, 1f);

        private static Mesh _sphere;
        private static Mesh _cylinder;
        private static Material _bodyMaterial;
        private static Material _solidMaterial;
        private static Material _markMaterial;
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>The image the view shows; a new one if the old was lost (the view re-reads it after each render).</summary>
        public RenderTexture Texture { get; private set; }
        private Camera _camera;
        private bool _rebuiltLogged;
        private readonly CommandBuffer _buffer = new CommandBuffer { name = "RealTargets autopsy" };
        private readonly MaterialPropertyBlock _props = new MaterialPropertyBlock();

        private Mesh _body;
        private Vector3[] _vertices;
        private Vector3[] _normals;
        private Color[] _tint;
        private Color32[] _colours;

        /// <summary>One projectile's marks, centre-relative. End = where a round that stayed in stopped.</summary>
        private sealed class Mark
        {
            public Vector3 Entry;
            public readonly List<Vector3> Exits = new List<Vector3>(2);
            public Vector3 Direction;
            public Vector3 End;
            public bool Stopped, Fatal, You;
            /// <summary>Goes on from inside (same round, later frame): no entry ball.</summary>
            public bool FromInside;
            public bool InferredExit;
            /// <summary>Its guessed exit was not one (a later frame found the round still inside): runs on to End.</summary>
            public bool PassThrough;
            /// <summary>Outside the body between the previous stretch's exit and this entry (a forearm into the chest).</summary>
            public bool Gap;
            public Vector3 GapFrom;
            /// <summary>Energy it carried into this stretch (J); 0 for fragments.</summary>
            public float Energy;
        }

        private const int MaxCavities = 96;
        /// <summary>Farthest a skin may reach from the chest centre in any pose (feet ~1.1 m, raised hands ~0.9 m).</summary>
        private const float MaxReach = 1.6f;

        private readonly Dictionary<int, List<Mark>> _shots = new Dictionary<int, List<Mark>>();
        private readonly AutopsyAnatomy _anatomy = new AutopsyAnatomy();
        private readonly XrayLayer _cavities = new XrayLayer(XrayLayer.Look.Glow);
        private int _shadedHighlight = -1;

        /// <summary>Organs on a shot path (and in a cavity) for the legend; empty if none.</summary>
        public string OrganSummary { get; private set; } = string.Empty;

        /// <summary>One shot's wound channel, for its detail line (single projectiles only).</summary>
        internal struct Channel
        {
            public float Length, CavityRadius;
            public bool StayedIn, Expanding;
            /// <summary>Stretches through flesh: 2 for a forearm into the chest.</summary>
            public int Stretches;
        }

        private readonly Dictionary<int, Channel> _channels = new Dictionary<int, Channel>();

        // Orbit camera: yaw/pitch/distance around Target (centre-relative). A focus or reset glides there.
        private struct ViewState
        {
            public float Yaw, Pitch, Distance;
            public Vector3 Target;
        }

        private const float GlideSeconds = 0.35f;
        private ViewState _view = new ViewState { Pitch = -8f, Distance = 2.5f };
        private ViewState _glideFrom, _glideTo;
        private float _glide = 1f;
        private float _defaultYaw;
        private float _defaultDistance;
        private float _bodyRadius = 1f;
        private bool _dirty = true;
        // Re-shade on the first render after a snapshot and whenever the eye moves (a NaN sentinel never compared true:
        // the body stayed Unity's default opaque white, 1.1.0 dev).
        private bool _needsShade = true;
        private Vector3 _shadedFrom;

        public bool Ready => _body != null;
        public int VertexCount => _vertices?.Length ?? 0;

        public AutopsyModel()
        {
            EnsureResources();
            EnsureRenderer();
        }

        /// <summary>
        /// The render texture and the private camera, made again if they are gone. Your own X-ray is made in the raid and
        /// shown in the menu: release check 3 found its camera destroyed with the raid (NullReferenceException in
        /// `get_transform` every frame, only in Unity's Player.log) although its GameObject was HideAndDontSave.
        /// </summary>
        private void EnsureRenderer()
        {
            bool rebuilt = false;
            if (Texture == null || !Texture.IsCreated())
            {
                if (Texture != null) UnityEngine.Object.Destroy(Texture);
                Texture = new RenderTexture(Width * Supersample, Height * Supersample, 24, RenderTextureFormat.ARGB32)
                {
                    name = "RealTargets.Autopsy3D",
                    // Never unloaded with the scene (Dispose frees it).
                    hideFlags = HideFlags.HideAndDontSave,
                    antiAliasing = 4,
                    filterMode = FilterMode.Bilinear,
                };
                Texture.Create();
                rebuilt = _camera != null || _body != null;
            }
            if (_camera == null)
            {
                rebuilt |= _body != null;
                MakeCamera();
            }
            _camera.targetTexture = Texture;
            if (rebuilt)
            {
                _dirty = true;
                _needsShade = true;
                if (!_rebuiltLogged)
                {
                    _rebuiltLogged = true;
                    Plugin.Log.LogInfo("[Autopsy] 3D view: camera or image was gone (scene change), made again");
                }
            }
        }

        private void MakeCamera()
        {
            var go = new GameObject("RealTargets.Autopsy3D") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _camera = go.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.cullingMask = 0; // nothing from the scene: only our command buffer draws
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Background;
            _camera.fieldOfView = Fov;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 50f;
            _camera.renderingPath = RenderingPath.Forward;
            _camera.allowHDR = false;
            _camera.allowMSAA = true;
            _camera.useOcclusionCulling = false;
            _camera.targetTexture = Texture;
            _camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _buffer);
        }

        public void Dispose()
        {
            if (_camera != null)
            {
                _camera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _buffer);
                UnityEngine.Object.Destroy(_camera.gameObject);
            }
            _buffer.Release();
            if (_body != null) UnityEngine.Object.Destroy(_body);
            _anatomy.Dispose();
            _cavities.Dispose();
            if (Texture != null)
            {
                Texture.Release();
                UnityEngine.Object.Destroy(Texture);
            }
        }

        // ---------- snapshot ----------

        /// <summary>Builds the model for this body; false = no pose, no body or bake failed (the caller falls back to 2D).</summary>
        public bool Snapshot(BodyRecord record, List<AutopsyShot> shots)
        {
            if (_body != null)
            {
                UnityEngine.Object.Destroy(_body);
                _body = null;
            }
            _shots.Clear();
            _channels.Clear();
            _anatomy.Dispose();
            _cavities.Dispose();
            OrganSummary = string.Empty;
            _needsShade = true;
            var player = record?.Player;
            if (player == null || record.Pose == null || player.PlayerBody == null) return false;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var live = record.Pose.Apply();
            if (live == null) return false;
            Vector3 centre;
            var positions = new List<Vector3>(40000);
            var normals = new List<Vector3>(40000);
            var tints = new List<Color>(40000);
            var triangles = new List<int>(120000);
            var skinNotes = new List<string>(2);
            int skins = 0;
            try
            {
                var bones = player.PlayerBones;
                var pelvis = bones.Pelvis.Original.position;
                var ribcage = bones.Ribcage != null ? bones.Ribcage.Original.position : pelvis + Vector3.up * 0.3f;
                centre = (pelvis + ribcage) * 0.5f;

                var partOfBone = PartOfBone(player);
                foreach (var pair in player.PlayerBody.BodySkins)
                {
                    if (pair.Key == EBodyModelPart.DogTag || pair.Key == EBodyModelPart.Voice) continue;
                    var smr = Lod0(pair.Value);
                    if (smr == null || smr.sharedMesh == null)
                    {
                        skinNotes.Add($"{pair.Key} no LOD0 mesh");
                        continue;
                    }
                    int firstVertex = positions.Count, firstIndex = triangles.Count;
                    if (!Bake(smr, centre, partOfBone, record, positions, normals, tints, triangles))
                    {
                        skinNotes.Add($"{pair.Key} {smr.name} baked empty");
                        continue;
                    }
                    // Release check 3: a PMC's X-ray came out as a huge dim outline around a tiny skeleton (3 skins, 9999
                    // vertices): a skin far outside any body pose is left out, so it can't blow up the view's framing.
                    float reach = 0f;
                    for (int i = firstVertex; i < positions.Count; i++) reach = Mathf.Max(reach, positions[i].sqrMagnitude);
                    reach = Mathf.Sqrt(reach);
                    if (reach > MaxReach)
                    {
                        skinNotes.Add($"{pair.Key} {smr.name} left out: reaches {reach:F1} m from the chest " +
                                      $"({positions.Count - firstVertex} vertices, scale {smr.transform.lossyScale.x:F2}, mesh {smr.sharedMesh.bounds.size.magnitude:F1} m)");
                        positions.RemoveRange(firstVertex, positions.Count - firstVertex);
                        normals.RemoveRange(firstVertex, normals.Count - firstVertex);
                        tints.RemoveRange(firstVertex, tints.Count - firstVertex);
                        triangles.RemoveRange(firstIndex, triangles.Count - firstIndex);
                        continue;
                    }
                    var scale = smr.transform.lossyScale;
                    if (Mathf.Abs(scale.x - 1f) > 0.01f || Mathf.Abs(scale.y - 1f) > 0.01f || Mathf.Abs(scale.z - 1f) > 0.01f)
                    {
                        skinNotes.Add($"{pair.Key} {smr.name} scaled {scale.x:F2}, reaches {reach:F2} m");
                    }
                    skins++;
                }

                // Every shot's points with the body in this pose, its path and its temporary cavity.
                var paths = new List<AutopsyAnatomy.Path>(shots.Count * 2);
                int cavities = 0;
                foreach (var shot in shots)
                {
                    var list = new List<Mark>(shot.Projectiles.Count);
                    foreach (var p in shot.Projectiles)
                    {
                        if (!p.Entry3D.Valid) continue;
                        bool stopped = AutopsyDiagram.StoppedByArmour(p.Hit);
                        bool fatal = p.Hit.Status == HitStatus.Killed;
                        bool pellet = p.Hit.ProjectileCount > 1;
                        bool expanding = AutopsyAnatomy.Expanding(p.Hit.AmmoName, p.Hit.Caliber, p.Hit.ProjectileCount);
                        int from = list.Count;

                        if (stopped || p.Legs3D.Count == 0)
                        {
                            // Stopped by armour, or recorded before legs: entry + its exits as one stretch.
                            var mark = new Mark
                            {
                                Entry = p.Entry3D.World - centre,
                                Direction = p.Entry3D.WorldDirection.normalized,
                                Stopped = stopped,
                                Fatal = fatal,
                                You = p.Hit.ShooterIsYou,
                                Energy = p.Hit.IsFragment ? 0f : p.Hit.EffectiveEnergy,
                            };
                            for (int i = 0; i < p.Exits3D.Count; i++)
                            {
                                // Armour-plate exits are outside the body: only flesh exits make the channel.
                                if (p.Exits3D[i].Valid && i < p.Hit.Exits.Count && !p.Hit.Exits[i].plate) mark.Exits.Add(p.Exits3D[i].World - centre);
                            }
                            list.Add(mark);
                        }
                        else
                        {
                            // One mark per stretch through flesh (a forearm, then the chest: 1.1.0 test).
                            Mark previous = null;
                            foreach (var leg in p.Legs3D)
                            {
                                var mark = new Mark
                                {
                                    Entry = leg.Start.World - centre,
                                    Direction = leg.Start.WorldDirection.normalized,
                                    Fatal = fatal && previous == null,
                                    You = p.Hit.ShooterIsYou,
                                    FromInside = leg.FromInside,
                                    InferredExit = leg.InferredExit,
                                    Energy = p.Hit.IsFragment ? 0f : leg.Energy,
                                };
                                if (leg.Exited && leg.End.Valid) mark.Exits.Add(leg.End.World - centre);
                                if (previous != null)
                                {
                                    if (leg.FromInside)
                                    {
                                        // A later frame found it still inside: the exit before was an inner collider's, not one.
                                        previous.Exits.Clear();
                                        previous.PassThrough = true;
                                        previous.End = mark.Entry;
                                    }
                                    else if (!leg.FromInside && previous.Exits.Count > 0)
                                    {
                                        mark.Gap = true;
                                        mark.GapFrom = previous.Exits[0];
                                    }
                                }
                                list.Add(mark);
                                previous = mark;
                            }
                        }

                        // Channels, cavities and organ paths, stretch by stretch: the energy left in the body is all of
                        // it if the round stayed in, a share of it on a through-stretch.
                        float length = 0f, widest = 0f;
                        for (int m = from; m < list.Count; m++)
                        {
                            var mark = list[m];
                            if (mark.Stopped) continue;
                            bool through = mark.Exits.Count > 0;
                            if (!mark.PassThrough)
                            {
                                mark.End = through ? mark.Exits[0] : mark.Entry + mark.Direction * AutopsyAnatomy.StayedInDepth(mark.Energy, pellet);
                            }
                            float cavity = AutopsyAnatomy.CavityRadius(through || mark.PassThrough ? mark.Energy * 0.45f : mark.Energy, pellet);
                            paths.Add(new AutopsyAnatomy.Path { Shot = shot.Number, Start = mark.Entry, End = mark.End, CavityRadius = cavity });
                            var d = mark.End - mark.Entry;
                            length += d.magnitude;
                            widest = Mathf.Max(widest, cavity);
                            if (cavity > 0f && cavities < MaxCavities && d.sqrMagnitude > 1e-6f)
                            {
                                AutopsyAnatomy.Cavity(_cavities, mark.Entry, d.normalized, d.magnitude, cavity, expanding, shot.Number);
                                cavities++;
                            }
                        }
                        if (shot.Projectiles.Count == 1 && list.Count > from && !list[from].Stopped)
                        {
                            var lastMark = list[list.Count - 1];
                            _channels[shot.Number] = new Channel
                            {
                                Length = length,
                                CavityRadius = widest,
                                StayedIn = lastMark.Exits.Count == 0 && !lastMark.PassThrough,
                                Expanding = expanding,
                                Stretches = list.Count - from,
                            };
                        }
                    }
                    if (list.Count > 0) _shots[shot.Number] = list;
                }
                _cavities.Build("RealTargets.AutopsyCavities");

                // Skeleton and organ zones need the death pose: built here, before it is undone.
                try
                {
                    _anatomy.Build(player, record, centre, paths);
                    OrganSummary = _anatomy.Summary();
                }
                catch (Exception ex)
                {
                    // Anatomy is extra: the body and shots still show.
                    Plugin.Log.LogWarning($"[Autopsy] anatomy layer skipped: {ex.Message}");
                    _anatomy.Dispose();
                    OrganSummary = string.Empty;
                }
            }
            finally
            {
                live.Restore();
            }

            if (positions.Count == 0)
            {
                Plugin.Log.LogWarning($"[Autopsy] 3D model {record.Name}: no skin to show ({string.Join("; ", skinNotes)})");
                return false;
            }
            _body = new Mesh { name = "RealTargets.AutopsyBody", hideFlags = HideFlags.HideAndDontSave, indexFormat = positions.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            _body.SetVertices(positions);
            _body.SetNormals(normals);
            _body.SetTriangles(triangles, 0);
            _body.RecalculateBounds();
            _vertices = positions.ToArray();
            _normals = normals.ToArray();
            _tint = tints.ToArray();
            _colours = new Color32[_vertices.Length];

            // Start in front of the body (its facing at death), the whole body in view.
            var facing = Vector3.ProjectOnPlane(record.Pose.Facing, Vector3.up);
            _defaultYaw = facing.sqrMagnitude > 1e-4f ? Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg : 0f;
            float radius = 0.3f;
            foreach (var v in _vertices) radius = Mathf.Max(radius, v.magnitude);
            _bodyRadius = radius;
            _defaultDistance = radius / Mathf.Sin(Fov * 0.5f * Mathf.Deg2Rad) * 1.02f;
            ResetView(false);

            Plugin.Log.LogInfo($"[Autopsy] 3D model {record.Name}: {skins} skins, {_vertices.Length} vertices, {_shots.Count} shots, " +
                               $"anatomy {(_anatomy.Skeleton.Mesh != null ? $"on, {_anatomy.ScaleNote}" : "off")}, {(OrganSummary.Length > 0 ? OrganSummary : "no organ on a path")}, " +
                               $"{watch.Elapsed.TotalMilliseconds:F1} ms{(skinNotes.Count > 0 ? $"; skins: {string.Join("; ", skinNotes)}" : "")}");
            return true;
        }

        private static SkinnedMeshRenderer Lod0(LoddedSkin skin)
        {
            if (skin == null || skin._lods == null) return null;
            foreach (var lod in skin._lods)
            {
                if (lod != null && lod.SkinnedMeshRenderer != null) return lod.SkinnedMeshRenderer;
            }
            return null;
        }

        /// <summary>
        /// The skin bone carrying each body-part collider (the collider sits on it or on a child of it, as in
        /// Dismemberment.Sever); any other bone finds its part by walking up to one of these.
        /// </summary>
        private static Dictionary<Transform, EBodyPart> PartOfBone(Player player)
        {
            var map = new Dictionary<Transform, EBodyPart>();
            var colliders = player.PlayerBones?.BodyPartCollidersDictionary;
            if (colliders == null) return map;
            var skinBones = new HashSet<Transform>();
            foreach (var pair in player.PlayerBody.BodySkins)
            {
                var smr = Lod0(pair.Value);
                if (smr?.bones == null) continue;
                foreach (var b in smr.bones)
                {
                    if (b != null) skinBones.Add(b);
                }
            }
            foreach (var pair in colliders)
            {
                var bpc = pair.Value;
                if (bpc == null || bpc.Collider == null || bpc is ArmorPlateCollider) continue;
                var t = bpc.TransformCached != null ? bpc.TransformCached : bpc.Collider.transform;
                while (t != null && !skinBones.Contains(t)) t = t.parent;
                if (t != null && !map.ContainsKey(t)) map[t] = AutopsyText.PartOf(pair.Key);
            }
            return map;
        }

        private static EBodyPart PartOf(Transform bone, Dictionary<Transform, EBodyPart> map, Dictionary<Transform, EBodyPart> cache)
        {
            if (bone == null) return EBodyPart.Chest;
            if (cache.TryGetValue(bone, out var cached)) return cached;
            var part = EBodyPart.Chest;
            for (var t = bone; t != null; t = t.parent)
            {
                if (map.TryGetValue(t, out part)) break;
                part = EBodyPart.Chest;
            }
            cache[bone] = part;
            return part;
        }

        private static bool Bake(SkinnedMeshRenderer smr, Vector3 centre, Dictionary<Transform, EBodyPart> partOfBone, BodyRecord record,
            List<Vector3> positions, List<Vector3> normals, List<Color> tints, List<int> triangles)
        {
            var baked = new Mesh();
            try
            {
                // Release check 5: BakeMesh(true) is fully local (the skin's scale divided out), so it needs the full transform.
                // Some BEAR tops (Tshirt_bear_Tiger_lod0) sit at scale 0.1; position + rotation only put them 10x, 14.7 m out.
                smr.BakeMesh(baked, true);
                var v = baked.vertices;
                var n = baked.normals;
                if (v.Length == 0) return false;
                var toWorld = smr.transform.localToWorldMatrix;

                // Body part per vertex from its strongest bone (readable meshes); else the nearest bone of the skin.
                var source = smr.sharedMesh;
                BoneWeight[] weights = source.isReadable && source.vertexCount == v.Length ? source.boneWeights : null;
                var bones = smr.bones;
                var cache = new Dictionary<Transform, EBodyPart>();
                var partColours = new Dictionary<EBodyPart, Color>();

                int offset = positions.Count;
                for (int i = 0; i < v.Length; i++)
                {
                    var world = toWorld.MultiplyPoint3x4(v[i]);
                    positions.Add(world - centre);
                    normals.Add(n.Length == v.Length ? toWorld.MultiplyVector(n[i]).normalized : Vector3.up);

                    EBodyPart part;
                    if (weights != null && bones != null && weights[i].boneIndex0 < bones.Length)
                    {
                        part = PartOf(bones[weights[i].boneIndex0], partOfBone, cache);
                    }
                    else
                    {
                        part = NearestPart(world, partOfBone);
                    }
                    if (!partColours.TryGetValue(part, out var colour))
                    {
                        colour = PartColour(record, part);
                        partColours[part] = colour;
                    }
                    tints.Add(colour);
                }
                for (int s = 0; s < baked.subMeshCount; s++)
                {
                    foreach (int index in baked.GetTriangles(s)) triangles.Add(index + offset);
                }
                return true;
            }
            finally
            {
                UnityEngine.Object.Destroy(baked);
            }
        }

        private static EBodyPart NearestPart(Vector3 world, Dictionary<Transform, EBodyPart> partOfBone)
        {
            float best = float.MaxValue;
            var part = EBodyPart.Chest;
            foreach (var pair in partOfBone)
            {
                if (pair.Key == null) continue;
                float d = (pair.Key.position - world).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    part = pair.Value;
                }
            }
            return part;
        }

        private static Color PartColour(BodyRecord record, EBodyPart part)
        {
            if (!record.HealthAtDeath.TryGetValue(part, out var hp)) return Healthy;
            if (hp.destroyed) return Destroyed;
            float fraction = hp.maximum > 0f ? Mathf.Clamp01(hp.current / hp.maximum) : 1f;
            return Color.Lerp(Hurt, Healthy, fraction);
        }

        // ---------- view ----------

        private float MinDistance => Mathf.Max(0.35f, _defaultDistance * 0.1f);

        /// <summary>Orbit by a mouse delta in pixels (the caller picks the sign: body follows the mouse or the camera does).</summary>
        public void Rotate(Vector2 delta)
        {
            _glide = 1f;
            _view.Yaw += delta.x * 0.4f;
            _view.Pitch = Mathf.Clamp(_view.Pitch + delta.y * 0.3f, -80f, 80f);
            _dirty = true;
        }

        public void Zoom(float wheel)
        {
            _glide = 1f;
            _view.Distance = Mathf.Clamp(_view.Distance * (1f - wheel * 0.1f), MinDistance, _defaultDistance * 1.6f);
            _dirty = true;
        }

        /// <summary>
        /// Moves the orbit point along the screen's up by a share of the view's height (+1 = one full view up),
        /// kept inside the body's bounding sphere.
        /// </summary>
        public void Pan(float share)
        {
            _glide = 1f;
            var up = Quaternion.LookRotation(-Direction(_view), Vector3.up) * Vector3.up;
            float height = 2f * _view.Distance * Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
            _view.Target = Vector3.ClampMagnitude(_view.Target + up * (share * height), _bodyRadius);
            _dirty = true;
        }

        public void ResetView(bool glide)
        {
            GlideTo(new ViewState { Yaw = _defaultYaw, Pitch = -8f, Distance = _defaultDistance, Target = Vector3.zero }, glide);
        }

        /// <summary>
        /// Turns to look across one shot's channel (all its pellets for a volley), close enough that the channel fills
        /// about half the view, from the side nearest the current view. False if the shot has no marks in 3D.
        /// </summary>
        public bool Focus(int shot, bool glide)
        {
            if (!_shots.TryGetValue(shot, out var list) || list.Count == 0) return false;
            var bounds = new Bounds(list[0].Entry, Vector3.zero);
            foreach (var m in list)
            {
                bounds.Encapsulate(m.Entry);
                foreach (var exit in m.Exits) bounds.Encapsulate(exit);
                if (m.Exits.Count == 0 && !m.Stopped) bounds.Encapsulate(m.End);
            }

            var current = Direction(_view);
            var view = current;
            // One projectile (one or more stretches): look across its whole way in -> out.
            if (list.Count == 1 || _channels.ContainsKey(shot))
            {
                var m = list[0];
                var last = list[list.Count - 1];
                var axis = (last.Exits.Count > 0 ? last.Exits[0] : last.Stopped ? last.Entry + last.Direction : last.End) - m.Entry;
                if (axis.sqrMagnitude > 1e-6f)
                {
                    view = Vector3.ProjectOnPlane(current, axis.normalized);
                    if (view.sqrMagnitude < 1e-4f) view = Vector3.Cross(axis.normalized, Vector3.up);
                    if (view.sqrMagnitude < 1e-4f) view = current;
                    view.Normalize();
                }
            }

            float size = Mathf.Max(bounds.extents.magnitude, 0.05f);
            float distance = Mathf.Clamp(size / Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad) * 2f, MinDistance, _defaultDistance);
            GlideTo(new ViewState
            {
                Yaw = Mathf.Atan2(view.x, view.z) * Mathf.Rad2Deg,
                Pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(view.y, -1f, 1f)) * Mathf.Rad2Deg, -80f, 80f),
                Distance = distance,
                Target = bounds.center,
            }, glide);
            return true;
        }

        /// <summary>The shot's wound channel (single projectiles); false for volleys or shots without one.</summary>
        public bool ChannelOf(int shot, out Channel channel) => _channels.TryGetValue(shot, out channel);

        /// <summary>Organs one shot went through or reached with its cavity; empty if none.</summary>
        public string OrgansOf(int shot) => _anatomy.SummaryFor(shot);

        private void GlideTo(ViewState to, bool glide)
        {
            if (!glide)
            {
                _view = to;
                _glide = 1f;
            }
            else
            {
                _glideFrom = _view;
                _glideTo = to;
                _glide = 0f;
            }
            _dirty = true;
        }

        private static Vector3 Direction(ViewState v) => Quaternion.Euler(v.Pitch, v.Yaw, 0f) * Vector3.forward;

        public void Invalidate() => _dirty = true;

        /// <summary>Renders if anything changed; returns true when it did (the caller moves its labels).</summary>
        public bool RenderIfDirty(int highlight)
        {
            if (_body == null) return false;
            EnsureRenderer();
            if (!_dirty) return false;
            _dirty = false;

            if (_glide < 1f)
            {
                // A focus or reset glides over GlideSeconds (rendering each frame until there); input cancels it.
                _glide = Mathf.Min(1f, _glide + Time.unscaledDeltaTime / GlideSeconds);
                float t = Mathf.SmoothStep(0f, 1f, _glide);
                _view = new ViewState
                {
                    Yaw = Mathf.LerpAngle(_glideFrom.Yaw, _glideTo.Yaw, t),
                    Pitch = Mathf.Lerp(_glideFrom.Pitch, _glideTo.Pitch, t),
                    Distance = Mathf.Lerp(_glideFrom.Distance, _glideTo.Distance, t),
                    Target = Vector3.Lerp(_glideFrom.Target, _glideTo.Target, t),
                };
                if (_glide < 1f) _dirty = true;
            }

            var direction = Direction(_view);
            var eye = _view.Target + direction * _view.Distance;
            _camera.transform.SetPositionAndRotation(Remote + eye, Quaternion.LookRotation(-direction, Vector3.up));
            bool moved = _needsShade || (eye - _shadedFrom).sqrMagnitude > 1e-6f;
            if (moved)
            {
                Shade(eye);
                _anatomy.Skeleton.Shade(eye, 0);
                _anatomy.Organs.Shade(eye, 0);
                _shadedFrom = eye;
                _needsShade = false;
            }
            if (moved || highlight != _shadedHighlight)
            {
                _cavities.Shade(eye, highlight);
                _shadedHighlight = highlight;
            }

            // Order: bones (alpha, own depth) under everything, then the additive glow layers (body, organs,
            // cavities), then depth cleared so the shot marks always show, even inside a bone.
            _buffer.Clear();
            var at = Matrix4x4.Translate(Remote);
            _props.Clear();
            _props.SetColor(ColorId, Color.white);
            if (Plugin.AutopsySkeleton.Value && _anatomy.Skeleton.Mesh != null) _buffer.DrawMesh(_anatomy.Skeleton.Mesh, at, _solidMaterial, 0, 0, _props);
            _buffer.DrawMesh(_body, at, _bodyMaterial, 0, 0, _props);
            if (Plugin.AutopsyOrgans.Value && _anatomy.Organs.Mesh != null) _buffer.DrawMesh(_anatomy.Organs.Mesh, at, _bodyMaterial, 0, 0, _props);
            if (Plugin.AutopsyCavities.Value && _cavities.Mesh != null) _buffer.DrawMesh(_cavities.Mesh, at, _bodyMaterial, 0, 0, _props);
            _buffer.ClearRenderTarget(true, false, Color.clear);

            foreach (var pair in _shots)
            {
                bool lit = highlight == 0 || pair.Key == highlight;
                float alpha = lit ? 1f : 0.3f;
                float size = pair.Key == highlight ? 1.4f : 1f;
                foreach (var m in pair.Value)
                {
                    var colour = m.You ? Yours : Others;
                    foreach (var exit in m.Exits)
                    {
                        Tube(at, m.Entry, exit, 0.0045f * size, colour, 0.75f * alpha);
                        Sphere(at, exit, 0.022f * size, colour, 0.4f * alpha);
                    }
                    if (m.Exits.Count == 0 && !m.Stopped)
                    {
                        // Stayed in the body: the channel to where it stopped (depth from its energy); a pass-through
                        // runs on to the next stretch without a stop ball.
                        Tube(at, m.Entry, m.End, 0.0045f * size, colour, 0.75f * alpha);
                        if (!m.PassThrough) Sphere(at, m.End, 0.009f * size, colour, 0.7f * alpha);
                    }
                    // Out of the body and in again (a forearm, then the chest): a faint line across the gap.
                    if (m.Gap) Tube(at, m.GapFrom, m.Entry, 0.0022f * size, colour, 0.35f * alpha);
                    if (m.FromInside) continue;
                    if (m.Fatal) Sphere(at, m.Entry, 0.036f * size, Fatal, 0.35f * alpha);
                    Sphere(at, m.Entry, (m.Stopped ? 0.011f : 0.016f) * size, m.Fatal ? Fatal : colour, (m.Stopped ? 0.6f : 1f) * alpha);
                }
            }

            _camera.Render();
            return true;
        }

        /// <summary>Viewport position (0-1, y up) of a shot's first entry, for its number label; false if none.</summary>
        public bool LabelPoint(int shot, out Vector2 viewport)
        {
            viewport = default;
            if (!_shots.TryGetValue(shot, out var list) || list.Count == 0) return false;
            var p = _camera.WorldToViewportPoint(Remote + list[0].Entry);
            if (p.z <= 0f) return false;
            viewport = new Vector2(p.x, p.y);
            return true;
        }

        /// <summary>X-ray: faint where the surface faces the eye, bright at the silhouette; tint = health at death.</summary>
        private void Shade(Vector3 eye)
        {
            for (int i = 0; i < _vertices.Length; i++)
            {
                var view = eye - _vertices[i];
                float facing = Mathf.Abs(Vector3.Dot(_normals[i], view.normalized));
                float rim = 1f - facing;
                float a = 0.035f + 0.5f * rim * rim * Mathf.Sqrt(rim);
                var t = _tint[i];
                _colours[i] = new Color32((byte)(t.r * 255f), (byte)(t.g * 255f), (byte)(t.b * 255f), (byte)(Mathf.Clamp01(a) * 255f));
            }
            _body.colors32 = _colours;
        }

        private void Sphere(Matrix4x4 at, Vector3 centre, float radius, Color colour, float alpha)
        {
            _props.Clear();
            _props.SetColor(ColorId, new Color(colour.r, colour.g, colour.b, alpha));
            _buffer.DrawMesh(_sphere, at * Matrix4x4.TRS(centre, Quaternion.identity, Vector3.one * radius * 2f), _markMaterial, 0, 0, _props);
        }

        private void Tube(Matrix4x4 at, Vector3 a, Vector3 b, float radius, Color colour, float alpha)
        {
            var d = b - a;
            float length = d.magnitude;
            if (length < 1e-4f) return;
            _props.Clear();
            _props.SetColor(ColorId, new Color(colour.r, colour.g, colour.b, alpha));
            var matrix = Matrix4x4.TRS((a + b) * 0.5f, Quaternion.FromToRotation(Vector3.up, d / length), new Vector3(radius * 2f, length * 0.5f, radius * 2f));
            _buffer.DrawMesh(_cylinder, at * matrix, _markMaterial, 0, 0, _props);
        }

        // ---------- resources ----------

        private static void EnsureResources()
        {
            if (_sphere == null) _sphere = PrimitiveMesh(PrimitiveType.Sphere);
            if (_cylinder == null) _cylinder = PrimitiveMesh(PrimitiveType.Cylinder);
            if (_bodyMaterial != null) return;

            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) throw new InvalidOperationException("Hidden/Internal-Colored not found");
            // Body: additive, no depth writes, both faces: overlapping layers glow brighter (X-ray).
            _bodyMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
            _bodyMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _bodyMaterial.SetInt("_DstBlend", (int)BlendMode.One);
            _bodyMaterial.SetInt("_ZWrite", 0);
            _bodyMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            _bodyMaterial.SetInt("_Cull", (int)CullMode.Off);
            // Skeleton: alpha-blended with its own depth (bones hide the bones behind them), drawn first.
            _solidMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 2990 };
            _solidMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _solidMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _solidMaterial.SetInt("_ZWrite", 1);
            _solidMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            _solidMaterial.SetInt("_Cull", (int)CullMode.Back);
            // Marks: alpha-blended over the body, depth among themselves.
            _markMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3100 };
            _markMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _markMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _markMaterial.SetInt("_ZWrite", 1);
            _markMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            _markMaterial.SetInt("_Cull", (int)CullMode.Back);
        }

        private static Mesh PrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var mesh = UnityEngine.Object.Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.DestroyImmediate(go);
            var colours = new Color32[mesh.vertexCount];
            for (int i = 0; i < colours.Length; i++) colours[i] = new Color32(255, 255, 255, 255);
            mesh.colors32 = colours;
            mesh.hideFlags = HideFlags.HideAndDontSave;
            return mesh;
        }
    }
}
