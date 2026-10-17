using EFT;
using RealTargets.Hits;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// Where one projectile went through flesh: entry, exit (if it went through), direction and the
    /// energy it brought into the body. Shared by the bleed model and the impact effects.
    /// </summary>
    internal struct WoundPath
    {
        public HitSegment Entry;
        public Vector3 Start;
        public Vector3 Direction;
        public bool Exited;
        public Vector3 End;
        public EBodyPartColliderType ExitCollider;
        /// <summary>Kinetic energy that reached flesh (J): impact energy x share past armour.</summary>
        public float Energy;

        /// <summary>
        /// False when nothing entered the body: a plate stopped it, or soft armour/helmet let less
        /// than `Armour stop threshold` of the damage through (blunt only, no hole).
        /// </summary>
        internal static bool Resolve(HitEvent hit, out WoundPath path)
        {
            path = default;
            if (hit == null || hit.Segments.Count == 0) return false;

            int first = hit.Segments.FindIndex(s => !s.HasPlate);
            if (first < 0) return false;

            var entry = hit.Segments[first];
            // Same test as the wound patches (WoundDecals), so a hole always bleeds and vice versa.
            if (entry.DamageIn > 0.01f && entry.DamageOut / entry.DamageIn < Plugin.ArmourStopFraction.Value) return false;
            float flesh = hit.FleshFraction;

            path.Entry = entry;
            path.Start = entry.Point;
            path.Direction = entry.Direction.sqrMagnitude > 0.5f ? entry.Direction.normalized : hit.Direction.normalized;
            float raw = hit.ImpactEnergy > 0f ? hit.ImpactEnergy : entry.DamageIn * 20f;
            path.Energy = raw * Mathf.Max(flesh, 0.05f);

            // The farthest flesh exit in line with the shot (< 0.8 m: a body, not the next one).
            float best = 0f;
            foreach (var e in hit.Exits)
            {
                if (e.plate) continue;
                float along = Vector3.Dot(e.point - path.Start, path.Direction);
                if (along > best && along < 0.8f)
                {
                    best = along;
                    path.End = e.point;
                    path.ExitCollider = e.collider;
                    path.Exited = true;
                }
            }

            if (!path.Exited)
            {
                float depth = Mathf.Clamp(0.06f + path.Energy / 12000f, 0.06f, 0.35f);
                path.End = path.Start + path.Direction * depth;
                path.ExitCollider = entry.Collider;
            }
            return true;
        }

        public static bool IsHead(EBodyPartColliderType c) =>
            c == EBodyPartColliderType.HeadCommon || c == EBodyPartColliderType.ParietalHead || c == EBodyPartColliderType.BackHead ||
            c == EBodyPartColliderType.Ears || c == EBodyPartColliderType.Eyes || c == EBodyPartColliderType.Jaw;

        public static bool IsNeck(EBodyPartColliderType c) =>
            c == EBodyPartColliderType.NeckFront || c == EBodyPartColliderType.NeckBack;

        public static bool IsChest(EBodyPartColliderType c) =>
            c == EBodyPartColliderType.RibcageUp || c == EBodyPartColliderType.RibcageLow || c == EBodyPartColliderType.SpineTop ||
            c == EBodyPartColliderType.LeftSideChestUp || c == EBodyPartColliderType.RightSideChestUp ||
            c == EBodyPartColliderType.LeftSideChestDown || c == EBodyPartColliderType.RightSideChestDown;
    }
}
