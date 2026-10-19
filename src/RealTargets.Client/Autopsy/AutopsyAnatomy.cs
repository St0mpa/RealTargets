using System;
using System.Collections.Generic;
using System.Text;
using EFT;
using RealTargets.Gore;
using UnityEngine;
using UnityEngine.Rendering;

namespace RealTargets.Autopsy
{
    /// <summary>
    /// One drawable layer of the autopsy's 3D view (skeleton, organ zones or energy cavities): simple shapes merged
    /// into one mesh, coloured per vertex and re-shaded when the view or the hovered shot changes.
    /// Rim = brighter at the silhouette (organs, like the body); Solid = lit by facing (bones);
    /// Glow = bright core, faint edge (cavities, drawn additive).
    /// </summary>
    internal sealed class XrayLayer : IDisposable
    {
        internal enum Look { Rim, Solid, Glow }

        private const int SphereRings = 10;
        private const int SphereSegments = 16;

        public readonly Look Kind;
        public Mesh Mesh { get; private set; }

        private readonly List<Vector3> _v = new List<Vector3>(4096);
        private readonly List<Vector3> _n = new List<Vector3>(4096);
        private readonly List<Color> _c = new List<Color>(4096);
        private readonly List<int> _g = new List<int>(4096);
        private readonly List<int> _t = new List<int>(12000);
        private Vector3[] _vertices;
        private Vector3[] _normals;
        private Color[] _base;
        private int[] _groups;
        private Color32[] _out;

        public XrayLayer(Look kind) => Kind = kind;

        public void Dispose()
        {
            if (Mesh != null) UnityEngine.Object.Destroy(Mesh);
            Mesh = null;
            // A build that failed half-way must not leak its shapes into the next one.
            _v.Clear(); _n.Clear(); _c.Clear(); _g.Clear(); _t.Clear();
        }

        /// <summary>Ellipsoid with semi-axes <paramref name="radii"/> along right / up / forward.</summary>
        public void Ellipsoid(Vector3 centre, Vector3 right, Vector3 up, Vector3 forward, Vector3 radii, Color colour, int group = 0)
        {
            int start = _v.Count;
            for (int r = 0; r <= SphereRings; r++)
            {
                float lat = Mathf.PI * r / SphereRings;
                float y = Mathf.Cos(lat), ring = Mathf.Sin(lat);
                for (int s = 0; s <= SphereSegments; s++)
                {
                    float lon = 2f * Mathf.PI * s / SphereSegments;
                    var unit = new Vector3(ring * Mathf.Cos(lon), y, ring * Mathf.Sin(lon));
                    _v.Add(centre + right * (unit.x * radii.x) + up * (unit.y * radii.y) + forward * (unit.z * radii.z));
                    var n = right * (unit.x / Mathf.Max(radii.x, 1e-4f)) + up * (unit.y / Mathf.Max(radii.y, 1e-4f)) +
                            forward * (unit.z / Mathf.Max(radii.z, 1e-4f));
                    _n.Add(n.normalized);
                    _c.Add(colour);
                    _g.Add(group);
                }
            }
            for (int r = 0; r < SphereRings; r++)
            {
                for (int s = 0; s < SphereSegments; s++)
                {
                    int a = start + r * (SphereSegments + 1) + s, b = a + SphereSegments + 1;
                    Quad(a, b, a + 1, b + 1);
                }
            }
        }

        public void Sphere(Vector3 centre, float radius, Color colour, int group = 0) =>
            Ellipsoid(centre, Vector3.right, Vector3.up, Vector3.forward, Vector3.one * radius, colour, group);

        /// <summary>Open tube from a to b (bones, vessels, ribs).</summary>
        public void Tube(Vector3 a, Vector3 b, float radius, Color colour, int group = 0, int sides = 8)
        {
            var d = b - a;
            if (d.sqrMagnitude < 1e-8f) return;
            Lathe(a, d.normalized, new[] { 0f, d.magnitude }, new[] { radius, radius }, new[] { colour, colour }, group, sides);
        }

        /// <summary>
        /// Surface of revolution along <paramref name="dir"/> from <paramref name="start"/>: ring i sits at
        /// distances[i] with radii[i] and colours[i]. Normals are radial (good enough for the X-ray shading).
        /// </summary>
        public void Lathe(Vector3 start, Vector3 dir, float[] distances, float[] radii, Color[] colours, int group, int sides = 14)
        {
            Basis(dir, out var u, out var w);
            int startIndex = _v.Count;
            for (int i = 0; i < distances.Length; i++)
            {
                var c = start + dir * distances[i];
                for (int s = 0; s <= sides; s++)
                {
                    float angle = 2f * Mathf.PI * s / sides;
                    var radial = u * Mathf.Cos(angle) + w * Mathf.Sin(angle);
                    _v.Add(c + radial * radii[i]);
                    _n.Add(radial);
                    _c.Add(colours[i]);
                    _g.Add(group);
                }
            }
            for (int i = 0; i < distances.Length - 1; i++)
            {
                for (int s = 0; s < sides; s++)
                {
                    int a = startIndex + i * (sides + 1) + s, b = a + sides + 1;
                    Quad(a, b, a + 1, b + 1);
                }
            }
        }

        private void Quad(int a, int b, int c, int d)
        {
            _t.Add(a); _t.Add(b); _t.Add(c);
            _t.Add(c); _t.Add(b); _t.Add(d);
        }

        private static void Basis(Vector3 dir, out Vector3 u, out Vector3 w)
        {
            u = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            w = Vector3.Cross(dir, u).normalized;
        }

        public bool Build(string name)
        {
            if (Mesh != null) UnityEngine.Object.Destroy(Mesh);
            Mesh = null;
            if (_v.Count == 0) return false;
            Mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave, indexFormat = _v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            Mesh.SetVertices(_v);
            Mesh.SetNormals(_n);
            Mesh.SetTriangles(_t, 0);
            Mesh.RecalculateBounds();
            _vertices = _v.ToArray();
            _normals = _n.ToArray();
            _base = _c.ToArray();
            _groups = _g.ToArray();
            _out = new Color32[_vertices.Length];
            _v.Clear(); _n.Clear(); _c.Clear(); _g.Clear(); _t.Clear();
            return true;
        }

        /// <summary>Vertex colours for this eye; a hovered shot (group) brightens, the other shots' shapes dim.</summary>
        public void Shade(Vector3 eye, int highlight)
        {
            if (Mesh == null) return;
            for (int i = 0; i < _vertices.Length; i++)
            {
                float facing = Mathf.Abs(Vector3.Dot(_normals[i], (eye - _vertices[i]).normalized));
                var c = _base[i];
                float a;
                switch (Kind)
                {
                    case Look.Solid:
                        float lit = 0.45f + 0.55f * facing;
                        c = new Color(c.r * lit, c.g * lit, c.b * lit, c.a);
                        a = c.a;
                        break;
                    case Look.Glow:
                        a = c.a * (0.2f + 0.8f * facing * facing);
                        break;
                    default:
                        float rim = 1f - facing;
                        a = c.a * (0.08f + 0.92f * rim * rim * Mathf.Sqrt(rim));
                        break;
                }
                int g = _groups[i];
                if (highlight != 0 && g != 0) a *= g == highlight ? 2f : 0.3f;
                _out[i] = new Color32((byte)(Mathf.Clamp01(c.r) * 255f), (byte)(Mathf.Clamp01(c.g) * 255f), (byte)(Mathf.Clamp01(c.b) * 255f),
                    (byte)(Mathf.Clamp01(a) * 255f));
            }
            Mesh.colors32 = _out;
        }
    }

    /// <summary>
    /// Simplified anatomy for the autopsy's 3D body, placed from the death pose: a skeleton (spine, skull, ribs,
    /// pelvis, limb bones from EFT's limb colliders; orange where EFT had a fracture) and translucent organ zones
    /// (brain, heart, lungs, liver, stomach, gut, kidneys) authored in the body frame
    /// (BodyFrame: pelvis -> neck = up). Zones are approximate and visual only (ADR 0008: EFT decides damage).
    /// Width scale from the shoulder joints (the upper-arm colliders' tops), height and depth from pelvis -> neck.
    /// Each shot's path (entry -> exit, or entry + its depth) marks the zones it crosses, and the zones inside its
    /// temporary cavity, so the view and the report can show what a shot went through.
    /// </summary>
    internal sealed class AutopsyAnatomy : IDisposable
    {
        private static readonly Color Bone = new Color(0.93f, 0.89f, 0.78f, 0.55f);
        private static readonly Color Fractured = new Color(1f, 0.55f, 0.15f, 0.75f);
        private static readonly Color Artery = new Color(1f, 0.12f, 0.1f, 0.5f);
        private static readonly Color HitGlow = new Color(1f, 0.95f, 0.8f);
        private static readonly Color CavityTint = new Color(1f, 0.55f, 0.15f);
        // Main arteries (aorta, carotids, femoral, brachial): built but not shown or listed (Stompa, 1.1.0 test:
        // the zones are visuals only, the vessel lines added noise).
        private static readonly bool ShowArteries = false;
        // Shoulder-joint span (upper-arm collider tops) of the body the zones are authored for.
        private const float ShoulderSpan = 0.36f;

        /// <summary>Width scale used by the last Build and what it came from (for the log).</summary>
        public string ScaleNote { get; private set; } = string.Empty;

        public readonly XrayLayer Skeleton = new XrayLayer(XrayLayer.Look.Solid);
        public readonly XrayLayer Organs = new XrayLayer(XrayLayer.Look.Rim);

        private sealed class Organ
        {
            public string Name;
            public Color Colour;
            public Vector3 Centre, Right, Up, Forward, Radii;
            // Vessels: a tube instead of an ellipsoid.
            public bool Vessel;
            public Vector3 A, B;
            public float Radius;
            public readonly List<int> Hit = new List<int>(2);
            public readonly List<int> Near = new List<int>(2);
        }

        private readonly List<Organ> _organs = new List<Organ>(20);

        public void Dispose()
        {
            Skeleton.Dispose();
            Organs.Dispose();
        }

        /// <summary>A shot's path through the body (centre-relative), for the organ test.</summary>
        internal struct Path
        {
            public int Shot;
            public Vector3 Start, End;
            public float CavityRadius;
        }

        /// <summary>Builds both layers; call with the death pose applied. Positions are relative to <paramref name="centre"/>.</summary>
        public void Build(Player player, BodyRecord record, Vector3 centre, List<Path> paths)
        {
            _organs.Clear();
            var bones = player.PlayerBones;
            var frame = BodyFrame.Build(player, record.Pose.Facing);
            if (frame == null) return;

            var pelvis = frame.Anchors["pelvis"];
            var neck = frame.Anchors["neck"];
            float length = Vector3.Distance(pelvis, neck);
            float s = Mathf.Clamp(length / 0.5f, 0.6f, 1.6f);
            // Width: PlayerBones.LeftShoulder/RightShoulder sit near the spine (collarbone roots: 1.1.0 test, the
            // chest came out ~40 % too narrow at the old 0.6 floor), so the shoulder joints come from the upper-arm
            // colliders, the same points the collarbones are drawn to. Fallback: the height scale.
            float k = s;
            float bonesApart = bones.LeftShoulder != null && bones.RightShoulder != null
                ? Vector3.Distance(bones.LeftShoulder.Original.position, bones.RightShoulder.Original.position) : 0f;
            if (frame.Limbs.TryGetValue(EBodyPartColliderType.LeftUpperArm, out var leftArm) &&
                frame.Limbs.TryGetValue(EBodyPartColliderType.RightUpperArm, out var rightArm) &&
                (leftArm.Bone == null || !Collapsed(leftArm.Bone)) && (rightArm.Bone == null || !Collapsed(rightArm.Bone)))
            {
                float span = Mathf.Abs(Vector3.Dot(rightArm.Proximal - leftArm.Proximal, frame.Right));
                // People's shoulder joints sit ~30-42 cm apart; outside 26-46 cm the colliders are posed oddly
                // (1.1.0 test: 55 cm on one scav -> x1.5, the chest too wide): use the height scale then.
                if (span >= 0.26f && span <= 0.46f)
                {
                    k = Mathf.Clamp(span / ShoulderSpan, 0.8f, 1.25f);
                    ScaleNote = $"width x{k:F2} (shoulder joints {span * 100f:F0} cm, shoulder bones {bonesApart * 100f:F0} cm)";
                }
                else
                {
                    ScaleNote = $"width x{k:F2} (shoulder joints {span * 100f:F0} cm implausible, height scale)";
                }
            }
            else
            {
                ScaleNote = $"width x{k:F2} (no upper-arm colliders, height scale)";
            }
            Vector3 Torso(float x, float yOfLength, float z) =>
                frame.ToWorld("pelvis", new Vector3(x, yOfLength * length, z)) - centre;

            // Head: the HeadCommon collider's centre (as the head gore uses), its up from neck to head bone.
            var headBone = bones.Head?.Original;
            bool headGone = headBone == null || Collapsed(headBone);
            var headCentre = frame.Anchors.TryGetValue("col:" + EBodyPartColliderType.HeadCommon, out var hc) ? hc : frame.Anchors["head"];
            var headUp = (frame.Anchors["head"] - neck).sqrMagnitude > 1e-6f ? (frame.Anchors["head"] - neck).normalized : frame.Up;
            var headForward = Vector3.ProjectOnPlane(frame.Forward, headUp).normalized;
            if (headForward.sqrMagnitude < 0.5f) headForward = frame.Forward;
            var headRight = Vector3.Cross(headUp, headForward).normalized;
            var brain = headCentre - centre + headUp * 0.02f * s;
            // The head's own axes from its colliders (gore build test, Stompa: "brain location in the head is bit off" on a
            // head slumped onto the shoulder; the torso's forward and the HeadCommon centre put it into the face):
            // forward = back of the head -> eyes, up = jaw -> crown; the brain sits above the eyes-occiput line, 45 % of
            // the way up to the crown collider.
            if (frame.Anchors.TryGetValue("col:" + EBodyPartColliderType.Eyes, out var eyes) &&
                frame.Anchors.TryGetValue("col:" + EBodyPartColliderType.BackHead, out var occiput))
            {
                var fwd = eyes - occiput;
                if (fwd.sqrMagnitude > 1e-4f)
                {
                    headForward = fwd.normalized;
                    if (frame.Anchors.TryGetValue("col:" + EBodyPartColliderType.ParietalHead, out var crown) &&
                        frame.Anchors.TryGetValue("col:" + EBodyPartColliderType.Jaw, out var jaw))
                    {
                        var up = Vector3.ProjectOnPlane(crown - jaw, headForward);
                        if (up.sqrMagnitude > 1e-4f) headUp = up.normalized;
                    }
                    else
                    {
                        headUp = Vector3.ProjectOnPlane(headUp, headForward).normalized;
                    }
                    headRight = Vector3.Cross(headUp, headForward).normalized;
                    var mid = (eyes + occiput) * 0.5f;
                    brain = (frame.Anchors.TryGetValue("col:" + EBodyPartColliderType.ParietalHead, out var top)
                        ? Vector3.Lerp(mid, top, 0.45f) : mid + headUp * 0.035f * s) - centre;
                }
            }

            bool Fracture(EBodyPart part) =>
                record.EffectsAtDeath.TryGetValue(part, out var effects) && effects.Exists(e => e.IndexOf("Fracture", StringComparison.OrdinalIgnoreCase) >= 0);
            Color BoneOf(EBodyPart part) => Fracture(part) ? Fractured : Bone;

            // ---- organs ----
            if (!headGone)
            {
                AddEllipsoid("brain", new Color(0.95f, 0.62f, 0.72f, 0.42f), brain, headRight, headUp, headForward, new Vector3(0.068f, 0.062f, 0.085f) * s);
            }
            // Lungs fill the ribcage sides with a ~7 cm gap between them (the mediastinum) where the heart sits
            // (1.1.0 test: they touched at the midline with the heart inside them).
            AddOrgan("left lung", new Color(1f, 0.55f, 0.6f, 0.34f), Torso(-0.09f * k, 0.72f, 0f), frame, new Vector3(0.055f * k, 0.12f * s, 0.075f * s));
            AddOrgan("right lung", new Color(1f, 0.55f, 0.6f, 0.34f), Torso(0.092f * k, 0.72f, 0f), frame, new Vector3(0.058f * k, 0.12f * s, 0.078f * s));
            AddOrgan("heart", new Color(1f, 0.1f, 0.12f, 0.5f), Torso(-0.015f * k, 0.69f, 0.04f * s), frame, new Vector3(0.042f * k, 0.06f * s, 0.045f * s));
            AddOrgan("liver", new Color(0.7f, 0.18f, 0.1f, 0.45f), Torso(0.06f * k, 0.52f, 0.02f * s), frame, new Vector3(0.085f * k, 0.055f * s, 0.065f * s));
            AddOrgan("stomach", new Color(0.98f, 0.75f, 0.35f, 0.4f), Torso(-0.07f * k, 0.53f, 0.04f * s), frame, new Vector3(0.065f * k, 0.05f * s, 0.045f * s));
            AddOrgan("intestines", new Color(0.92f, 0.65f, 0.42f, 0.32f), Torso(0f, 0.27f, 0.035f * s), frame, new Vector3(0.11f * k, 0.09f * s, 0.065f * s));
            AddOrgan("left kidney", new Color(0.7f, 0.28f, 0.6f, 0.45f), Torso(-0.07f * k, 0.42f, -0.05f * s), frame, new Vector3(0.03f * s, 0.05f * s, 0.025f * s));
            AddOrgan("right kidney", new Color(0.7f, 0.28f, 0.6f, 0.45f), Torso(0.07f * k, 0.41f, -0.05f * s), frame, new Vector3(0.03f * s, 0.05f * s, 0.025f * s));

            if (ShowArteries) AddArteries();

            void AddArteries()
            {
                AddVessel("aorta", Torso(-0.01f * k, 0.80f, -0.02f * s), Torso(-0.01f * k, 0.20f, -0.015f * s), 0.011f * s);
                if (!headGone)
                {
                    AddVessel("left carotid", Torso(-0.025f * k, 0.97f, 0.02f * s), brain - headRight * 0.035f * s - headUp * 0.05f * s, 0.005f * s);
                    AddVessel("right carotid", Torso(0.025f * k, 0.97f, 0.02f * s), brain + headRight * 0.035f * s - headUp * 0.05f * s, 0.005f * s);
                }
                Limb(frame, EBodyPartColliderType.LeftThigh, centre, (a, b, medial, r) => AddVessel("left femoral artery", a + medial * r * 0.4f, b + medial * r * 0.4f, 0.0055f * s));
                Limb(frame, EBodyPartColliderType.RightThigh, centre, (a, b, medial, r) => AddVessel("right femoral artery", a + medial * r * 0.4f, b + medial * r * 0.4f, 0.0055f * s));
                Limb(frame, EBodyPartColliderType.LeftUpperArm, centre, (a, b, medial, r) => AddVessel("left brachial artery", a + medial * r * 0.5f, b + medial * r * 0.5f, 0.004f * s));
                Limb(frame, EBodyPartColliderType.RightUpperArm, centre, (a, b, medial, r) => AddVessel("right brachial artery", a + medial * r * 0.5f, b + medial * r * 0.5f, 0.004f * s));
            }

            // What each shot went through (and what its temporary cavity reached).
            foreach (var path in paths)
            {
                foreach (var organ in _organs)
                {
                    if (Crosses(organ, path.Start, path.End, 0f)) organ.Hit.Add(path.Shot);
                    else if (path.CavityRadius > 0f && Crosses(organ, path.Start, path.End, path.CavityRadius * 0.7f)) organ.Near.Add(path.Shot);
                }
            }
            foreach (var organ in _organs)
            {
                var c = organ.Colour;
                if (organ.Hit.Count > 0) c = new Color(Mathf.Lerp(c.r, HitGlow.r, 0.45f), Mathf.Lerp(c.g, HitGlow.g, 0.45f), Mathf.Lerp(c.b, HitGlow.b, 0.45f), Mathf.Min(1f, c.a * 2.4f));
                else if (organ.Near.Count > 0) c = new Color(Mathf.Lerp(c.r, CavityTint.r, 0.4f), Mathf.Lerp(c.g, CavityTint.g, 0.4f), Mathf.Lerp(c.b, CavityTint.b, 0.4f), Mathf.Min(1f, c.a * 1.6f));
                if (organ.Vessel) Organs.Tube(organ.A, organ.B, organ.Radius, organ.Hit.Count > 0 ? c : new Color(c.r, c.g, c.b, c.a * 1.4f), 0, 6);
                else Organs.Ellipsoid(organ.Centre, organ.Right, organ.Up, organ.Forward, organ.Radii, c);
            }
            Organs.Build("RealTargets.AutopsyOrgans");

            // ---- skeleton ----
            var spineBack = -frame.Forward * 0.035f * s;
            var p0 = pelvis - centre + spineBack;
            var p1 = (frame.Anchors.TryGetValue("spine3", out var sp3) ? sp3 : Vector3.Lerp(pelvis, neck, 0.75f)) - centre + spineBack;
            var p2 = neck - centre + spineBack * 0.5f;
            var spine = BoneOf(EBodyPart.Chest);
            Skeleton.Tube(p0, Vector3.Lerp(p0, p1, 0.5f), 0.013f * s, BoneOf(EBodyPart.Stomach));
            Skeleton.Tube(Vector3.Lerp(p0, p1, 0.5f), p1, 0.013f * s, spine);
            Skeleton.Tube(p1, p2, 0.012f * s, spine);
            if (!headGone)
            {
                Skeleton.Tube(p2, frame.Anchors["head"] - centre, 0.011f * s, BoneOf(EBodyPart.Head));
                var skull = BoneOf(EBodyPart.Head);
                Skeleton.Ellipsoid(brain, headRight, headUp, headForward, new Vector3(0.08f, 0.075f, 0.097f) * s, new Color(skull.r, skull.g, skull.b, 0.22f));
            }
            // Pelvic girdle.
            Skeleton.Ellipsoid(Torso(0f, 0.03f, -0.005f * s), frame.Right, frame.Up, frame.Forward, new Vector3(0.13f * k, 0.065f * s, 0.08f * s),
                new Color(Bone.r, Bone.g, Bone.b, 0.3f));
            // Ribcage: six rings, widest in the middle, plus the sternum.
            var ribs = BoneOf(EBodyPart.Chest);
            Vector3 frontTop = Vector3.zero, frontBottom = Vector3.zero;
            for (int i = 0; i < 6; i++)
            {
                float y = 0.90f - i * 0.065f;
                float rx = (0.10f + 0.045f * Mathf.Sin(Mathf.PI * (i + 0.5f) / 6f)) * k;
                float rz = (0.075f + 0.012f * Mathf.Sin(Mathf.PI * (i + 0.5f) / 6f)) * s;
                var c = Torso(0f, y, 0f);
                Vector3 prev = default;
                for (int j = 0; j <= 18; j++)
                {
                    float a = 2f * Mathf.PI * j / 18f;
                    var p = c + frame.Right * (Mathf.Sin(a) * rx) + frame.Forward * (Mathf.Cos(a) * rz) - frame.Up * (Mathf.Cos(a) * 0.02f * s);
                    if (j > 0) Skeleton.Tube(prev, p, 0.0045f * s, ribs, 0, 5);
                    prev = p;
                }
                var front = c + frame.Forward * rz - frame.Up * 0.02f * s;
                if (i == 0) frontTop = front;
                if (i == 4) frontBottom = front;
            }
            Skeleton.Tube(frontTop, frontBottom, 0.008f * s, ribs);

            // Collarbones, arms, legs (from EFT's limb colliders: they follow the pose and are gone with a severed limb).
            var collarBase = Torso(0f, 0.95f, 0.03f * s);
            Limb(frame, EBodyPartColliderType.LeftUpperArm, centre, (a, b, _, __) => { Skeleton.Tube(collarBase, a, 0.007f * s, BoneOf(EBodyPart.Chest)); Joint(a, 0.016f * s); Skeleton.Tube(a, b, 0.011f * s, BoneOf(EBodyPart.LeftArm)); Joint(b, 0.013f * s); });
            Limb(frame, EBodyPartColliderType.RightUpperArm, centre, (a, b, _, __) => { Skeleton.Tube(collarBase, a, 0.007f * s, BoneOf(EBodyPart.Chest)); Joint(a, 0.016f * s); Skeleton.Tube(a, b, 0.011f * s, BoneOf(EBodyPart.RightArm)); Joint(b, 0.013f * s); });
            Limb(frame, EBodyPartColliderType.LeftForearm, centre, (a, b, _, __) => { Skeleton.Tube(a, b, 0.009f * s, BoneOf(EBodyPart.LeftArm)); Hand(bones.LeftPalm, b); });
            Limb(frame, EBodyPartColliderType.RightForearm, centre, (a, b, _, __) => { Skeleton.Tube(a, b, 0.009f * s, BoneOf(EBodyPart.RightArm)); Hand(bones.RightPalm, b); });
            Limb(frame, EBodyPartColliderType.LeftThigh, centre, (a, b, _, __) => { Joint(a, 0.022f * s); Skeleton.Tube(a, b, 0.015f * s, BoneOf(EBodyPart.LeftLeg)); Joint(b, 0.019f * s); });
            Limb(frame, EBodyPartColliderType.RightThigh, centre, (a, b, _, __) => { Joint(a, 0.022f * s); Skeleton.Tube(a, b, 0.015f * s, BoneOf(EBodyPart.RightLeg)); Joint(b, 0.019f * s); });
            Limb(frame, EBodyPartColliderType.LeftCalf, centre, (a, b, _, __) => { Skeleton.Tube(a, b, 0.012f * s, BoneOf(EBodyPart.LeftLeg)); Joint(b, 0.014f * s); });
            Limb(frame, EBodyPartColliderType.RightCalf, centre, (a, b, _, __) => { Skeleton.Tube(a, b, 0.012f * s, BoneOf(EBodyPart.RightLeg)); Joint(b, 0.014f * s); });
            Skeleton.Build("RealTargets.AutopsySkeleton");

            void Joint(Vector3 at, float r) => Skeleton.Sphere(at, r, new Color(Bone.r, Bone.g, Bone.b, 0.5f));
            void Hand(Transform palm, Vector3 wrist)
            {
                if (palm == null || Collapsed(palm)) return;
                var p = palm.position - centre;
                Skeleton.Tube(wrist, p, 0.008f * s, Bone);
                Skeleton.Sphere(p, 0.022f * s, new Color(Bone.r, Bone.g, Bone.b, 0.4f));
            }
            void AddEllipsoid(string name, Color colour, Vector3 c, Vector3 right, Vector3 up, Vector3 forward, Vector3 radii) =>
                _organs.Add(new Organ { Name = name, Colour = colour, Centre = c, Right = right, Up = up, Forward = forward, Radii = radii });
            void AddVessel(string name, Vector3 a, Vector3 b, float r) =>
                _organs.Add(new Organ { Name = name, Colour = Artery, Vessel = true, A = a, B = b, Radius = r });
        }

        private void AddOrgan(string name, Color colour, Vector3 centre, BodyFrame frame, Vector3 radii) =>
            _organs.Add(new Organ { Name = name, Colour = colour, Centre = centre, Right = frame.Right, Up = frame.Up, Forward = frame.Forward, Radii = radii });

        /// <summary>A limb collider's bone (centre-relative ends, medial direction, radius), unless the limb is gone.</summary>
        private static void Limb(BodyFrame frame, EBodyPartColliderType type, Vector3 centre, Action<Vector3, Vector3, Vector3, float> draw)
        {
            if (!frame.Limbs.TryGetValue(type, out var limb)) return;
            if (limb.Bone != null && Collapsed(limb.Bone)) return;
            draw(limb.Proximal - centre, limb.Distal - centre, limb.Medial, limb.Radius);
        }

        /// <summary>Dismemberment shrinks a removed part's bone to 1 %: its anatomy goes with it.</summary>
        private static bool Collapsed(Transform t) => t.lossyScale.x < 0.3f;

        private static bool Crosses(Organ o, Vector3 a, Vector3 b, float inflate)
        {
            if (o.Vessel) return SegmentDistance(a, b, o.A, o.B) <= o.Radius + 0.004f + inflate;
            // Into the ellipsoid's unit-sphere space; the segment enters if its closest point to the origin is within 1.
            var r = o.Radii + Vector3.one * inflate;
            Vector3 Local(Vector3 p)
            {
                var d = p - o.Centre;
                return new Vector3(Vector3.Dot(d, o.Right) / r.x, Vector3.Dot(d, o.Up) / r.y, Vector3.Dot(d, o.Forward) / r.z);
            }
            var la = Local(a);
            var ld = Local(b) - la;
            float len2 = ld.sqrMagnitude;
            float t = len2 > 1e-8f ? Mathf.Clamp01(-Vector3.Dot(la, ld) / len2) : 0f;
            return (la + ld * t).sqrMagnitude <= 1f;
        }

        private static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = d1.sqrMagnitude, e = d2.sqrMagnitude, f = Vector3.Dot(d2, r);
            float s, t;
            if (a <= 1e-8f && e <= 1e-8f) return r.magnitude;
            if (a <= 1e-8f)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-8f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                    s = denom > 1e-8f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            return ((p1 + d1 * s) - (p2 + d2 * t)).magnitude;
        }

        /// <summary>"heart (#3), left lung (#3, #5); cavity: liver (#5)" for the legend; empty when no shot reached an organ.</summary>
        /// <summary>"left lung, heart; cavity reached: liver" for one shot; empty when it reached none.</summary>
        public string SummaryFor(int shot)
        {
            var hit = new List<string>(3);
            var near = new List<string>(3);
            foreach (var o in _organs)
            {
                if (o.Hit.Contains(shot)) hit.Add(o.Name);
                else if (o.Near.Contains(shot)) near.Add(o.Name);
            }
            if (hit.Count == 0 && near.Count == 0) return string.Empty;
            string through = string.Join(", ", hit);
            string cavity = near.Count > 0 ? $"cavity reached: {string.Join(", ", near)}" : string.Empty;
            return hit.Count > 0 && near.Count > 0 ? $"{through}; {cavity}" : hit.Count > 0 ? through : cavity;
        }

        public string Summary()
        {
            var hit = new StringBuilder();
            var near = new StringBuilder();
            foreach (var o in _organs)
            {
                if (o.Hit.Count > 0) Append(hit, o.Name, o.Hit);
                else if (o.Near.Count > 0) Append(near, o.Name, o.Near);
            }
            if (hit.Length == 0 && near.Length == 0) return string.Empty;
            return hit.Length > 0 && near.Length > 0 ? $"{hit}; cavity reached: {near}" : hit.Length > 0 ? hit.ToString() : $"cavity reached: {near}";

            static void Append(StringBuilder sb, string name, List<int> shots)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(name).Append(" (");
                var seen = new HashSet<int>();
                bool first = true;
                foreach (int n in shots)
                {
                    if (!seen.Add(n)) continue;
                    if (!first) sb.Append(", ");
                    sb.Append('#').Append(n);
                    first = false;
                }
                sb.Append(')');
            }
        }

        // ---------- energy cavities ----------

        private static readonly Color CavityCold = new Color(0.6f, 0.05f, 0.02f);
        private static readonly Color CavityWarm = new Color(1f, 0.45f, 0.1f);
        private static readonly Color CavityHot = new Color(1f, 0.92f, 0.62f);

        private static readonly string[] ExpandingTokens =
            { "HP", "JHP", "SP", "JSP", "RIP", "HSP", "HYDRA", "WARMAGEDDON", "EXPANSIVE", "QUAKEMAKER", "SP7", "SP8", "HYDRA-SHOK" };

        /// <summary>
        /// Hollow/soft points and shotgun slugs dump their energy early: their cavity peaks near the entry.
        /// Slugs by calibre (a single projectile in 12/20 ga or 23x75: lead, FTX, Dual Sabot, Poleva-6u alike);
        /// rifle and pistol rounds by short name (not split on '-', so 9x39 SP-5 stays FMJ).
        /// </summary>
        internal static bool Expanding(string ammo, string caliber, int projectiles)
        {
            if (projectiles == 1 && !string.IsNullOrEmpty(caliber) &&
                (caliber.EndsWith("12g") || caliber.EndsWith("20g") || caliber.EndsWith("23x75"))) return true;
            if (string.IsNullOrEmpty(ammo)) return false;
            foreach (var token in ammo.ToUpperInvariant().Split(' ', '.', '/', '(', ')', '"'))
            {
                if (Array.IndexOf(ExpandingTokens, token) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Peak radius of the temporary cavity: grows with the square root of the energy left in the body
        /// (the exit wound uses the same law, WoundDecals), x [Hit model] Cavity scale.
        /// About 5 cm for 1300 J (5.56 that stays in), 7 cm for 2800 J (7.62x51), 3 cm for a 9 mm.
        /// </summary>
        internal static float CavityRadius(float deposited, bool pellet) =>
            deposited <= 1f ? 0f : Mathf.Clamp((0.012f + 0.0011f * Mathf.Sqrt(deposited)) * Plugin.CavityScale.Value * (pellet ? 0.6f : 1f), 0.006f, 0.09f);

        /// <summary>How deep a round that stayed in got: more energy, deeper (pellets stop sooner).</summary>
        internal static float StayedInDepth(float energy, bool pellet) =>
            pellet ? Mathf.Clamp(0.04f + 0.00015f * energy, 0.05f, 0.15f) : Mathf.Clamp(0.05f + 0.00011f * energy, 0.06f, 0.25f);

        /// <summary>
        /// The temporary cavity along one path as a glowing spindle: narrow at the entry, widest where the round
        /// dumps most energy (rifle FMJ after ~11 cm when it yaws, expanding rounds within ~4 cm), narrowing again.
        /// Colour runs deep red -> orange -> white-hot with the local width.
        /// </summary>
        internal static void Cavity(XrayLayer layer, Vector3 entry, Vector3 dir, float length, float peakRadius, bool expanding, int shot)
        {
            if (length < 0.01f || peakRadius <= 0f) return;
            const int rings = 24;
            float peakAt = expanding ? Mathf.Min(0.04f, 0.3f * length) : Mathf.Min(0.11f, 0.62f * length);
            float before = Mathf.Max(peakAt * (expanding ? 0.8f : 0.45f), 0.01f);
            float after = Mathf.Max((length - peakAt) * (expanding ? 0.55f : 0.6f), 0.02f);
            float neck = Mathf.Max(peakRadius * (expanding ? 0.22f : 0.12f), 0.004f);
            var distances = new float[rings + 1];
            var radii = new float[rings + 1];
            var colours = new Color[rings + 1];
            for (int i = 0; i <= rings; i++)
            {
                float d = length * i / rings;
                float w = d < peakAt ? before : after;
                float x = (d - peakAt) / w;
                float r = neck + (peakRadius - neck) * Mathf.Exp(-x * x);
                float heat = Mathf.Clamp01((r - neck) / Mathf.Max(peakRadius - neck, 1e-4f));
                var c = heat < 0.5f ? Color.Lerp(CavityCold, CavityWarm, heat * 2f) : Color.Lerp(CavityWarm, CavityHot, (heat - 0.5f) * 2f);
                c.a = 0.1f + 0.3f * heat;
                distances[i] = d;
                radii[i] = r;
                colours[i] = c;
            }
            layer.Lathe(entry, dir, distances, radii, colours, shot, 14);
        }
    }
}
