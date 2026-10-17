using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using RealTargets.Hits;
using SPT.Reflection.Patching;

namespace RealTargets.Patches
{
    /// <summary>
    /// Exposes the live <see cref="Shot"/> while the game resolves its hit. ShotDelegate builds
    /// the DamageInfo and calls BallisticCollider.ApplyHit -> Player.ApplyShot synchronously, so
    /// ApplyShotPatch can read impact speed, bullet mass and ammo from here. Offline SPT uses
    /// ClientLocalGameWorld, which inherits ClientGameWorld's override (it does not call base),
    /// so both the base and the override are patched.
    /// </summary>
    internal static class ShotContext
    {
        /// <summary>The shot being resolved right now, or null outside ShotDelegate.</summary>
        internal static Shot Current;

        internal static void Enable()
        {
            new BasePatch().Enable();
            new ClientPatch().Enable();
        }

        private class BasePatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() =>
                AccessTools.DeclaredMethod(typeof(GameWorld), nameof(GameWorld.ShotDelegate));

            [PatchPrefix]
            private static void Prefix(Shot shotResult) => Begin(shotResult);

            [PatchFinalizer]
            private static void Finalizer() => Current = null;
        }

        /// <summary>
        /// Sets the current shot, and records exits: a backward hit (IsForwardHit == false) on a
        /// body collider is the projectile leaving it. BodyPartCollider.ApplyHit only forwards
        /// forward hits to Player.ApplyShot, so this is the only place exits are visible.
        /// </summary>
        private static void Begin(Shot shot)
        {
            Current = shot;
            try
            {
                if (shot != null && !shot.IsForwardHit && shot.HittedBallisticCollider is BodyPartCollider bpc &&
                    bpc.Player is Player player && shot.Ammo != null)
                {
                    HitCollector.RecordExit(player, new ShotId(shot.Ammo.Id, shot.FragmentIndex),
                        bpc.BodyPartColliderType, bpc is ArmorPlateCollider, shot.HitPoint);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Exit capture failed: {ex.Message}");
            }
        }

        private class ClientPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() =>
                AccessTools.DeclaredMethod(typeof(ClientGameWorld), nameof(ClientGameWorld.ShotDelegate));

            [PatchPrefix]
            private static void Prefix(Shot shotResult) => Begin(shotResult);

            [PatchFinalizer]
            private static void Finalizer() => Current = null;
        }
    }
}
