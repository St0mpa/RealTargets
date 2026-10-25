using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using RealTargets.Hits;
using SPT.Reflection.Patching;
using UnityEngine;

namespace RealTargets.Patches
{
    /// <summary>
    /// blood-5 (blood-4 test: entry wounds and pellet holes read cherry-red on top of our dark fabric holes): EFT paints its own
    /// blood decal into a body's clothing texture on every hit (TextureDecalsPainter.DrawDecal(List&lt;BodyRenderer&gt;, point,
    /// normal), decompiled 2026-10-24: one call per hit, queued per renderer into the material's _DecalsTex, drawn by the body's
    /// p0 ..._Decal shader). HollywoodFX swaps that texture for its own splash; with its "Wound Decals on Bodies" off the
    /// game's default still paints. Our wounds, soaks and runs already show the hit on bots, so the game's red splash only
    /// sits on top of them. Skipped for bots only (`Visuals / Hide game blood decals on bots`); you and other human
    /// players (Fika) keep the game's decals. Visual only: nothing about health or the hit changes.
    /// </summary>
    internal static class BodyDecalPainterPatch
    {
        private static bool _logged;

        internal static void Enable() => new Painter().Enable();

        private sealed class Painter : ModulePatch
        {
            protected override MethodBase GetTargetMethod() =>
                AccessTools.Method(typeof(TextureDecalsPainter), nameof(TextureDecalsPainter.DrawDecal),
                    new[] { typeof(List<BodyRenderer>), typeof(Vector3), typeof(Vector3) });

            [PatchPrefix]
            private static bool Prefix(List<BodyRenderer> renderers)
            {
                if (!Plugin.ModEnabled.Value || !Plugin.VisualsEnabled.Value || !Plugin.VisualsWounds.Value || !Plugin.HideGameBodyDecals.Value) return true;
                if (renderers == null || renderers.Count == 0) return true;
                try
                {
                    Player player = null;
                    foreach (var body in renderers)
                    {
                        if (body.Renderers == null) continue;
                        foreach (var r in body.Renderers)
                        {
                            if (r == null) continue;
                            player = r.GetComponentInParent<Player>();
                            break;
                        }
                        if (player != null) break;
                    }
                    if (player == null || !Targets.IsBot(player)) return true;
                    if (!_logged)
                    {
                        _logged = true;
                        Plugin.Log.LogInfo("[Compat] the game's blood decal on bots' clothing skipped (ours draw the wounds; Blood and wounds / Hide game blood decals on bots)");
                    }
                    return false;
                }
                catch (System.Exception)
                {
                    return true;
                }
            }
        }
    }
}
