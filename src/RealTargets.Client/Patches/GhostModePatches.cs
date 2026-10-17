using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RealTargets.Patches
{
    /// <summary>
    /// Debug "ghost mode": bots never register the local player as an enemy, so they don't
    /// fight back while you test-shoot them. Blocks the three BotsGroup methods that add an
    /// enemy; seeing, hearing, being hit and group reports are expected to go through them
    /// (inferred from method names, verify in raid; SAIN might keep its own path). Takes effect for
    /// new detections only: bots that already know about you when it's switched on keep fighting.
    /// Only active while [Debug] Debug mode and [Debug] Ghost mode are both on.
    /// </summary>
    internal static class GhostModePatches
    {
        internal static bool Active => Plugin.DebugMode.Value && Plugin.GhostMode.Value;

        internal static bool IsGhost(IPlayer person) =>
            Active && person != null && person.IsYourPlayer;

        internal static void Enable()
        {
            new AddEnemyPatch().Enable();
            new CheckAndAddEnemyPatch().Enable();
            new EnemiesAddPatch().Enable();
        }

        private class AddEnemyPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() =>
                AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.AddEnemy));

            [PatchPrefix]
            private static bool Prefix(IPlayer person, ref bool __result)
            {
                if (!IsGhost(person))
                {
                    return true;
                }

                __result = false;
                return false;
            }
        }

        private class CheckAndAddEnemyPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() =>
                AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.CheckAndAddEnemy));

            [PatchPrefix]
            private static bool Prefix(IPlayer player, ref bool __result)
            {
                if (!IsGhost(player))
                {
                    return true;
                }

                __result = false;
                return false;
            }
        }

        private class EnemiesAddPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() =>
                AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.EnemiesAdd));

            [PatchPrefix]
            private static bool Prefix(IPlayer person) => !IsGhost(person);
        }
    }
}
