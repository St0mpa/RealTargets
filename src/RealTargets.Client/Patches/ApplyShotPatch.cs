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
    /// Captures every shot that lands on a Player (bots and the local player) and feeds it to
    /// HitCollector. SPT runs offline raids with EFT.LocalPlayer, which does not override
    /// Player.ApplyShot (only the online ClientPlayer does), so this single patch sees every hit.
    /// The prefix snapshots damageInfo and whether the target was alive, because ApplyShot
    /// modifies its own argument, which is what the postfix would otherwise see
    /// (e.g. Damage = 0 for hits on the local player).
    /// </summary>
    internal class ApplyShotPatch : ModulePatch
    {
        private static bool _failed;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.ApplyShot));

        [PatchPrefix]
        private static void Prefix(Player __instance, ref DamageInfo damageInfo, out ShotSnapshot __state)
        {
            __state = new ShotSnapshot
            {
                DamageInfo = damageInfo,
                TargetAlive = __instance.HealthController != null && __instance.HealthController.IsAlive,
            };
        }

        [PatchPostfix]
        private static void Postfix(
            Player __instance,
            DamageInfo damageInfo,
            EBodyPart bodyPartType,
            EBodyPartColliderType colliderType,
            EArmorPlateCollider armorPlateCollider,
            ShotId shotId,
            PlayerHitInfo __result,
            ShotSnapshot __state)
        {
            if (_failed)
            {
                return;
            }

            using var perf = Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Hits);
            try
            {
                HitCollector.Record(
                    __instance, __state, damageInfo, bodyPartType, colliderType, armorPlateCollider, shotId, __result);
            }
            catch (Exception ex)
            {
                // Never break the game's hit processing; report once and stand down.
                _failed = true;
                Plugin.Log.LogError($"ApplyShotPatch disabled after error: {ex}");
            }
        }
    }
}
