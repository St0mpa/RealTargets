using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// A body's anatomical frame at one moment, built from bone positions rather than bone
    /// axes (EFT's biped bones have no documented local axes). x = body right, y = up
    /// (pelvis -> neck), z = forward. Anchors are bone positions zones are authored against.
    /// Limbs come from their capsule colliders: axis, proximal end, medial and forward directions.
    /// </summary>
    internal sealed class BodyFrame
    {
        public Vector3 Right;
        public Vector3 Up;
        public Vector3 Forward;
        public readonly Dictionary<string, Vector3> Anchors = new Dictionary<string, Vector3>();
        public readonly Dictionary<EBodyPartColliderType, LimbFrame> Limbs = new Dictionary<EBodyPartColliderType, LimbFrame>();

        internal struct LimbFrame
        {
            public Vector3 Proximal;
            public Vector3 Distal;
            public float Radius;
            public Vector3 Medial;
            public Vector3 Forward;
            public Transform Bone;
        }

        private static readonly EBodyPartColliderType[] LimbColliders =
        {
            EBodyPartColliderType.LeftUpperArm, EBodyPartColliderType.LeftForearm,
            EBodyPartColliderType.RightUpperArm, EBodyPartColliderType.RightForearm,
            EBodyPartColliderType.LeftThigh, EBodyPartColliderType.LeftCalf,
            EBodyPartColliderType.RightThigh, EBodyPartColliderType.RightCalf,
        };

        /// <summary>Converts a frame-local offset (x right, y up, z forward) at an anchor to world.</summary>
        public Vector3 ToWorld(string anchor, Vector3 local)
        {
            if (!Anchors.TryGetValue(anchor, out var origin))
            {
                origin = Anchors["pelvis"];
            }
            return origin + Right * local.x + Up * local.y + Forward * local.z;
        }

        /// <summary>World position to frame-local offset from an anchor (for calibration dumps).</summary>
        public Vector3 ToLocal(string anchor, Vector3 world)
        {
            var d = world - Anchors[anchor];
            return new Vector3(Vector3.Dot(d, Right), Vector3.Dot(d, Up), Vector3.Dot(d, Forward));
        }

        internal static BodyFrame Build(Player player, Vector3? facing = null)
        {
            var bones = player != null ? player.PlayerBones : null;
            if (bones == null || bones.Pelvis == null || bones.Ribcage == null || bones.Neck == null ||
                bones.LeftShoulder == null || bones.RightShoulder == null || bones.Head == null)
            {
                return null;
            }

            var pelvis = bones.Pelvis.Original.position;
            var ribcage = bones.Ribcage.Original.position;
            var neck = bones.Neck.position;
            var head = bones.Head.Original.position;
            var left = bones.LeftShoulder.Original.position;
            var right = bones.RightShoulder.Original.position;

            var frame = new BodyFrame();
            frame.Up = (neck - pelvis).normalized;
            frame.Right = Vector3.ProjectOnPlane(right - left, frame.Up).normalized;
            frame.Forward = Vector3.Cross(frame.Right, frame.Up).normalized;

            // Cross(right, up) can point backwards depending on handedness; the character's own
            // forward settles it.
            // A recorded pose (3D view) brings the facing it had when hit.
            if (Vector3.Dot(frame.Forward, facing ?? player.Transform.forward) < 0f)
            {
                frame.Forward = -frame.Forward;
            }

            frame.Anchors["pelvis"] = pelvis;
            frame.Anchors["ribcage"] = ribcage;
            frame.Anchors["neck"] = neck;
            frame.Anchors["head"] = head;
            if (bones.Spine3 != null)
            {
                frame.Anchors["spine3"] = bones.Spine3.Original.position;
            }

            var colliders = bones.BodyPartCollidersDictionary;
            if (colliders != null)
            {
                // Collider centres as anchors ("col:HeadCommon"): they follow head tilt and posture.
                foreach (var pair in colliders)
                {
                    if (pair.Value != null && pair.Value.Collider != null && !(pair.Value is ArmorPlateCollider))
                    {
                        frame.Anchors["col:" + pair.Key] = Centre(pair.Value.Collider);
                    }
                }

                foreach (var type in LimbColliders)
                {
                    if (colliders.TryGetValue(type, out var bpc) && bpc != null && bpc.Collider != null)
                    {
                        bool isLeg = type == EBodyPartColliderType.LeftThigh || type == EBodyPartColliderType.LeftCalf ||
                                     type == EBodyPartColliderType.RightThigh || type == EBodyPartColliderType.RightCalf;
                        var root = isLeg ? pelvis : ribcage;
                        if (TryLimb(bpc, root, frame, out var limb))
                        {
                            frame.Limbs[type] = limb;
                        }
                    }
                }
            }

            return frame;
        }

        private static bool TryLimb(BodyPartCollider bpc, Vector3 bodyRoot, BodyFrame frame, out LimbFrame limb)
        {
            limb = default;
            var collider = bpc.Collider;
            var t = collider.transform;
            Vector3 a, b;
            float radius;

            if (collider is CapsuleCollider capsule)
            {
                Vector3 axis = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                float half = Mathf.Max(capsule.height * 0.5f - capsule.radius, 0.01f);
                a = t.TransformPoint(capsule.center + axis * half);
                b = t.TransformPoint(capsule.center - axis * half);
                radius = capsule.radius * MaxScale(t);
            }
            else if (collider is BoxCollider box)
            {
                var size = box.size;
                Vector3 axis = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
                float half = Vector3.Dot(size, axis) * 0.5f;
                a = t.TransformPoint(box.center + axis * half);
                b = t.TransformPoint(box.center - axis * half);
                radius = 0.25f * (size.x + size.y + size.z - Vector3.Dot(size, axis)) * MaxScale(t);
            }
            else
            {
                return false;
            }

            // Proximal end: the one nearer the joint the segment hangs from. A dead body's ragdoll body sits
            // on the segment's own bone, whose origin IS that joint (or on the parent's, whose origin is
            // further up the same chain: the nearer end is still the proximal one). 0.10.5 test: "the end
            // nearer the torso" flipped a forearm folded with the hand on the chest, and the severed piece's
            // cap went to the hand (31 cm from the elbow). Living bots have no ragdoll bodies: torso rule.
            var rb = collider.attachedRigidbody;
            var joint = rb != null ? rb.transform.position : bodyRoot;
            if ((b - joint).sqrMagnitude < (a - joint).sqrMagnitude)
            {
                (a, b) = (b, a);
            }

            // Capsule end-sphere centres sit a radius inside the limb's ends; extend to full length.
            var along = (b - a).normalized;
            a -= along * radius * 0.8f;
            b += along * radius * 0.8f;
            var mid = (a + b) * 0.5f;
            var toMidline = Vector3.ProjectOnPlane(bodyRoot - mid, along);
            var medial = toMidline.sqrMagnitude > 1e-6f ? toMidline.normalized : -frame.Right;
            var forward = Vector3.ProjectOnPlane(frame.Forward, along);
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : frame.Forward;

            limb = new LimbFrame
            {
                Proximal = a,
                Distal = b,
                Radius = radius,
                Medial = medial,
                Forward = forward,
                Bone = t,
            };
            return true;
        }

        /// <summary>
        /// Collider centre from its transform. Collider.bounds is the physics engine's copy, synced
        /// from transforms only at the next simulation step, so a pose applied for the 3D view (0.9.2)
        /// still reported the live pose: organs sat where the body lay, the brain mid-torso.
        /// </summary>
        private static Vector3 Centre(Collider collider)
        {
            var t = collider.transform;
            switch (collider)
            {
                case CapsuleCollider c: return t.TransformPoint(c.center);
                case SphereCollider c: return t.TransformPoint(c.center);
                case BoxCollider c: return t.TransformPoint(c.center);
                case MeshCollider m when m.sharedMesh != null: return t.TransformPoint(m.sharedMesh.bounds.center);
                default: return collider.bounds.center;
            }
        }

        private static float MaxScale(Transform t)
        {
            var s = t.lossyScale;
            return Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        }
    }
}
