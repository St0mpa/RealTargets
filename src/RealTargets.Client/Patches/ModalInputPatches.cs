using System;
using System.Reflection;
using EFT.InputSystem;
using HarmonyLib;
using RealTargets.Lab;
using SPT.Reflection.Patching;
using UnityEngine;

namespace RealTargets.Patches
{
    /// <summary>
    /// While one of our modal screens is open (F3 spawn menu) EFT gets no input:
    /// every key reads as released and the mouse axes as still. EFT reads keys (InputKey.Update) and
    /// mouse look (AxisUpdater / RemoteAxisUpdater) in Update, before OnGUI, so the per-frame
    /// Input.ResetInputAxes() the screens used came too late to stop aiming (Stompa, 0.9.3: aiming
    /// behind the spawn menu) and, worse, wiped the F3 press the menu needed to close (stuck menu).
    /// Our screens read their own keys from IMGUI events, which these patches don't touch.
    /// </summary>
    internal static class ModalInputPatches
    {
        internal static bool ModalOpen => TestLab.MenuOpen;

        internal static void Enable()
        {
            new KeyPatch().Enable();
            new AxisPatch().Enable();
            new RemoteAxisPatch().Enable();
        }

        /// <summary>Keys: fed "not held", so held keys release cleanly instead of sticking.</summary>
        private class KeyPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(InputKey), nameof(InputKey.Update));

            [PatchPrefix]
            private static bool Prefix(InputKey __instance)
            {
                if (!ModalOpen) return true;
                try
                {
                    __instance.Press = InputManager.UpdateInputMatrix[0, (int)__instance.Press];
                }
                catch (Exception)
                {
                    // Leave the state as it is.
                }
                return false;
            }
        }

        private class AxisPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(AxisUpdater), nameof(AxisUpdater.Update));

            [PatchPrefix]
            private static bool Prefix(AxisUpdater __instance)
            {
                if (!ModalOpen) return true;
                __instance._currentValue = 0f;
                return false;
            }
        }

        /// <summary>Raw-mouse mode: keeps tracking the cursor (no jump on close) but reports no motion.</summary>
        private class RemoteAxisPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(RemoteAxisUpdater), nameof(RemoteAxisUpdater.GetValue));

            [PatchPostfix]
            private static void Postfix(ref float __result)
            {
                if (ModalOpen) __result = 0f;
            }
        }
    }
}
