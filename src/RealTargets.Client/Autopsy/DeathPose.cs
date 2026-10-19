using EFT;
using UnityEngine;

namespace RealTargets.Autopsy
{
    /// <summary>
    /// The skeleton as it was at the moment of death (Player.OnPlayerDeadStatic fires inside the fatal hit,
    /// before the ragdoll takes over): the root's world pose and every transform's local pose under it.
    /// The autopsy's 3D view applies it to the corpse, bakes the body, and puts the live pose back within the
    /// same call, so nothing renders or simulates in between (the 0.9 3D view's HitPose, tag v0.9.4-sim).
    /// Scale is not stored: bones that Dismemberment collapsed stay collapsed, so gore shows in the snapshot.
    /// </summary>
    internal sealed class DeathPose
    {
        public float Time;
        /// <summary>The character's facing at death (the 3D view starts looking at the front).</summary>
        public Vector3 Facing;

        private Transform _root;
        private Vector3 _rootPosition;
        private Quaternion _rootRotation;
        private Transform[] _bones;
        private Vector3[] _localPositions;
        private Quaternion[] _localRotations;

        internal static DeathPose Capture(Player player)
        {
            var root = Root(player);
            if (root == null) return null;
            var pose = new DeathPose
            {
                Time = UnityEngine.Time.time,
                Facing = player.Transform != null ? player.Transform.forward : root.forward,
                _root = root,
                _rootPosition = root.position,
                _rootRotation = root.rotation,
            };
            pose.Read(root.GetComponentsInChildren<Transform>(true));
            return pose;
        }

        /// <summary>Puts the skeleton into this pose; returns the live pose to put back with <see cref="Restore"/>.</summary>
        internal DeathPose Apply()
        {
            if (_root == null || _bones == null) return null;
            var live = new DeathPose { _root = _root, _rootPosition = _root.position, _rootRotation = _root.rotation };
            live.Read(_bones);
            _root.SetPositionAndRotation(_rootPosition, _rootRotation);
            Write();
            return live;
        }

        /// <summary>Puts back a live pose returned by <see cref="Apply"/>.</summary>
        internal void Restore()
        {
            if (_root == null || _bones == null) return;
            Write();
            _root.SetPositionAndRotation(_rootPosition, _rootRotation);
        }

        private void Write()
        {
            for (int i = 0; i < _bones.Length; i++)
            {
                var t = _bones[i];
                if (t == null || t == _root) continue;
                t.localPosition = _localPositions[i];
                t.localRotation = _localRotations[i];
            }
        }

        private void Read(Transform[] bones)
        {
            _bones = bones;
            _localPositions = new Vector3[bones.Length];
            _localRotations = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                _localPositions[i] = bones[i].localPosition;
                _localRotations[i] = bones[i].localRotation;
            }
        }

        private static Transform Root(Player player)
        {
            var bones = player != null ? player.PlayerBones : null;
            if (bones == null) return null;
            return bones.RootJoint != null ? bones.RootJoint : bones.Pelvis?.Original;
        }
    }
}
