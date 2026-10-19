using System.Collections.Generic;
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

        /// <summary>Index of the leg (HitEvent.Legs) this path is; 0 = where the projectile first went in.</summary>
        public int Leg;
        /// <summary>Goes on from inside the body (the same round's earlier-frame hit): no hole or bleed at Start.</summary>
        public bool FromInside;

        /// <summary>
        /// The first stretch through flesh. False when nothing entered the body: a plate stopped it, or soft
        /// armour/helmet let less than `Armour stop threshold` of the damage through (blunt only, no hole).
        /// </summary>
        internal static bool Resolve(HitEvent hit, out WoundPath path)
        {
            path = default;
            if (hit == null || hit.Segments.Count == 0) return false;
            if (hit.Legs.Count == 0) return ResolveSingle(hit, out path);
            return ResolveLeg(hit, 0, out path);
        }

        /// <summary>
        /// Every stretch through flesh (a round through a forearm into the chest: two, 1.1.0 test); a later leg
        /// stopped by armour on its own collider (a vest behind the arm) is left out. Clears <paramref name="paths"/>.
        /// </summary>
        internal static void ResolveAll(HitEvent hit, List<WoundPath> paths)
        {
            paths.Clear();
            if (hit == null || hit.Segments.Count == 0) return;
            if (hit.Legs.Count == 0)
            {
                if (ResolveSingle(hit, out var single)) paths.Add(single);
                return;
            }
            for (int i = 0; i < hit.Legs.Count; i++)
            {
                if (ResolveLeg(hit, i, out var path)) paths.Add(path);
                else if (i == 0) return;
            }
        }

        private static bool ResolveLeg(HitEvent hit, int index, out WoundPath path)
        {
            path = default;
            var leg = hit.Legs[index];
            var entry = hit.Segments[leg.EntrySegment];
            // Same test as the wound patches (WoundDecals), so a hole always bleeds and vice versa.
            if (!leg.FromInside && entry.StoppedByArmour(Plugin.ArmourStopFraction.Value)) return false;

            path.Leg = index;
            path.FromInside = leg.FromInside;
            path.Entry = entry;
            path.Start = leg.Start;
            path.Direction = leg.Direction;
            if (index == 0)
            {
                float raw = hit.ImpactEnergy > 0f ? hit.ImpactEnergy : entry.DamageIn * 20f;
                path.Energy = raw * Mathf.Max(hit.FleshFraction, 0.05f);
            }
            else
            {
                // What it still carried going into this stretch, past any armour on it.
                float flesh = entry.ThroughArmour ? HitModel.FleshFraction(entry.DamageIn, entry.DamageOut) : 1f;
                float raw = hit.HasBallistics && leg.Speed > 0f ? HitModel.Energy(hit.BulletMassGram, leg.Speed) : entry.DamageIn * 20f;
                path.Energy = raw * Mathf.Max(flesh, 0.05f);
            }

            path.Exited = leg.Exited;
            path.End = leg.End;
            path.ExitCollider = leg.ExitCollider;
            if (!path.Exited)
            {
                float depth = Mathf.Clamp(0.06f + path.Energy / 12000f, 0.06f, 0.35f);
                path.End = path.Start + path.Direction * depth;
                path.ExitCollider = entry.Collider;
            }
            return true;
        }

        /// <summary>Before legs (hits that never went through HitCollector.BuildLegs): entry + farthest exit.</summary>
        private static bool ResolveSingle(HitEvent hit, out WoundPath path)
        {
            path = default;
            int first = hit.Segments.FindIndex(s => !s.HasPlate);
            if (first < 0) return false;

            var entry = hit.Segments[first];
            if (entry.StoppedByArmour(Plugin.ArmourStopFraction.Value)) return false;
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
