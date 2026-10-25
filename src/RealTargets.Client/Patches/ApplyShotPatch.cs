using System;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using RealTargets.Hits;
using SPT.Reflection.Patching;

namespace RealTargets.Patches
{
    /// <summary>
    /// Captures every shot that lands on a Player (bots and the local player) and feeds it to HitCollector.
    /// <para>
    /// The hit is taken at the body collider: BodyPartCollider.ApplyHit (and ArmorPlateCollider's override, which passes
    /// its plate) hands every forward hit to the body's bridge. Offline SPT's bridge calls Player.ApplyShot (LocalPlayer
    /// doesn't override it). Under Fika every machine re-simulates every bullet, so ApplyHit runs everywhere, but the
    /// bridge applies damage on one machine only and elsewhere just plays the hit reaction; Fika's players also override
    /// ApplyShot without calling base. Capturing at the collider gives every machine every hit (ADR 0014).
    /// </para>
    /// <para>
    /// The damage after armour is only known where a body's ApplyShot (or Fika's equivalent) runs: a postfix there fills
    /// it into the hit being captured, which modified its own argument by then. Elsewhere the hit keeps the damage before
    /// armour; armour verdicts (BlockedBy/DeflectedBy) come from the ballistics and are known everywhere. ApplyShot called
    /// without a collider (flares, rockets, backblast) is recorded there directly, as before 1.2.0.
    /// </para>
    /// </summary>
    internal static class ApplyShotPatch
    {
        /// <summary>A forward hit between its collider's ApplyHit prefix and postfix.</summary>
        private sealed class Frame
        {
            public Player Target;
            public ShotSnapshot Before;
            public DamageInfo After;
            public bool HasAfter;
        }

        private static Frame _frame;
        private static bool _failed;
        private static bool _armourFailed;

        internal static void Enable()
        {
            new ColliderPatch(AccessTools.DeclaredMethod(typeof(BodyPartCollider), nameof(BodyPartCollider.ApplyHit))).Enable();
            new ColliderPatch(AccessTools.DeclaredMethod(typeof(ArmorPlateCollider), nameof(ArmorPlateCollider.ApplyHit))).Enable();
            new PlayerPatch(AccessTools.Method(typeof(Player), nameof(Player.ApplyShot))).Enable();
            foreach (var method in Compat.Fika.ApplyShotOverrides()) new PlayerPatch(method).Enable();
            foreach (var method in Compat.Fika.AfterArmourMethods()) new AfterArmourPatch(method).Enable();
        }

        /// <summary>Pre-hit state: alive, and the armour piece in the way with its durability before EFT damages it.</summary>
        private static ShotSnapshot Snapshot(Player target, in DamageInfo damageInfo, EBodyPartColliderType collider, EArmorPlateCollider plate)
        {
            var state = new ShotSnapshot
            {
                DamageInfo = damageInfo,
                TargetAlive = target.HealthController != null && target.HealthController.IsAlive,
            };
            if (_armourFailed) return state;
            try
            {
                var armour = HitCollector.FindArmour(target, damageInfo, collider, plate);
                if (armour != null)
                {
                    state.Armour = armour;
                    state.DurabilityBefore = armour.Repairable != null ? armour.Repairable.Durability : 0f;
                }
            }
            catch (Exception ex)
            {
                // Only the armour report stands down; hits are still captured.
                _armourFailed = true;
                HitCollector.ArmourLookupWorks = false;
                Plugin.Log.LogError($"ApplyShotPatch: armour lookup disabled after error: {ex}");
            }
            return state;
        }

        private static void Record(Player target, in ShotSnapshot before, in DamageInfo after, EBodyPart bodyPart,
            EBodyPartColliderType collider, EArmorPlateCollider plate, ShotId shotId, PlayerHitInfo result)
        {
            using var perf = Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Hits);
            try
            {
                HitCollector.Record(target, before, after, bodyPart, collider, plate, shotId, result);
            }
            catch (Exception ex)
            {
                // Never break the game's hit processing; report once and stand down.
                _failed = true;
                _frame = null;
                Plugin.Log.LogError($"ApplyShotPatch disabled after error: {ex}");
            }
        }

        /// <summary>BodyPartCollider.ApplyHit / ArmorPlateCollider.ApplyHit: one forward hit, on every machine.</summary>
        private class ColliderPatch : ModulePatch
        {
            private readonly MethodBase _target;
            public ColliderPatch(MethodBase target) => _target = target;
            protected override MethodBase GetTargetMethod() => _target;

            [PatchPrefix]
            private static void Prefix(BodyPartCollider __instance, DamageInfo damageInfo, out Frame __state)
            {
                __state = null;
                if (_failed || !damageInfo.IsForwardHit || Compat.Fika.IsHeadless) return;
                if (!(__instance.playerBridge?.iPlayer is Player target)) return;
                __state = new Frame
                {
                    Target = target,
                    Before = Snapshot(target, damageInfo, __instance.BodyPartColliderType, PlateOf(__instance)),
                };
                _frame = __state;
            }

            [PatchPostfix]
            private static void Postfix(BodyPartCollider __instance, DamageInfo damageInfo, ShotId shotID, PlayerHitInfo __result, Frame __state)
            {
                if (__state == null) return;
                if (_frame == __state) _frame = null;
                if (_failed) return;
                Record(__state.Target, __state.Before, __state.HasAfter ? __state.After : damageInfo, __instance.BodyPartType,
                    __instance.BodyPartColliderType, PlateOf(__instance), shotID, __result);
            }

            [PatchFinalizer]
            private static void Finalizer(Frame __state)
            {
                if (__state != null && _frame == __state) _frame = null;
            }

            private static EArmorPlateCollider PlateOf(BodyPartCollider collider) =>
                collider is ArmorPlateCollider plate ? plate.ArmorPlateColliderType : 0;
        }

        /// <summary>
        /// Player.ApplyShot, and Fika's FikaBot / ObservedPlayer overrides: inside a collider hit, fill in the damage after
        /// armour; called on its own (no collider), record the hit here.
        /// </summary>
        private class PlayerPatch : ModulePatch
        {
            private readonly MethodBase _target;
            public PlayerPatch(MethodBase target) => _target = target;
            protected override MethodBase GetTargetMethod() => _target;

            // By position: Fika names some of these parameters differently.
            [PatchPrefix]
            private static void Prefix(Player __instance, DamageInfo __0, EBodyPartColliderType __2, EArmorPlateCollider __3, out ShotSnapshot? __state)
            {
                __state = null;
                if (_failed || Compat.Fika.IsHeadless) return;
                if (_frame != null && _frame.Target == __instance) return;
                __state = Snapshot(__instance, __0, __2, __3);
            }

            [PatchPostfix]
            private static void Postfix(Player __instance, DamageInfo __0, EBodyPart __1, EBodyPartColliderType __2,
                EArmorPlateCollider __3, ShotId __4, PlayerHitInfo __result, ShotSnapshot? __state)
            {
                if (_failed) return;
                if (__state == null)
                {
                    FillAfter(__instance, __0);
                    return;
                }
                Record(__instance, __state.Value, __0, __1, __2, __3, __4, __result);
            }
        }

        /// <summary>Fika's armour-only paths (ApplyClientShot, HandleSniperShot, SimulatedApplyShot): fill in only.</summary>
        private class AfterArmourPatch : ModulePatch
        {
            private readonly MethodBase _target;
            public AfterArmourPatch(MethodBase target) => _target = target;
            protected override MethodBase GetTargetMethod() => _target;

            [PatchPostfix]
            private static void Postfix(Player __instance, DamageInfo __0) => FillAfter(__instance, __0);
        }

        private static void FillAfter(Player player, in DamageInfo after)
        {
            var frame = _frame;
            if (frame == null || frame.Target != player) return;
            frame.After = after;
            frame.HasAfter = true;
        }
    }
}
