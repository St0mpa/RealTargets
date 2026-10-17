using System;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace RealTargets.Features
{
    /// <summary>
    /// Grenade blasts (0.8.0; injuries removed in 0.10.0, EFT decides damage again): after each
    /// explosion the peak overpressure at every corpse's chest (Kinney-Graham, TNT equivalent per
    /// grenade, x1.8 for a ground burst, x0.35 behind cover) throws corpses within 6 m away from it.
    /// RGD-5: ~300 kPa at 1 m, ~60 at 2 m, ~30 at 3 m. Phase 2 (limb loss) will hook in here.
    /// </summary>
    internal static class Blast
    {
        internal static void Enable()
        {
            if (ExplosionPatch.Target() != null)
            {
                new ExplosionPatch().Enable();
            }
            else
            {
                Plugin.Log.LogWarning("Blast: ExplosionSharedMethods.Explosion not found; no blast pushes.");
            }
        }

        private class ExplosionPatch : ModulePatch
        {
            internal static MethodBase Target() => AccessTools.FirstMethod(typeof(ExplosionSharedMethods), m =>
                m.Name == "Explosion" && m.GetParameters().Length >= 5 && m.GetParameters()[0].ParameterType == typeof(IExplosiveItem));

            protected override MethodBase GetTargetMethod() => Target();

            [PatchPostfix]
            private static void Postfix(IExplosiveItem grenadeItem, Vector3 explosionPosition)
            {
                try
                {
                    if (Plugin.ModEnabled.Value && Plugin.BlastEnabled.Value && grenadeItem != null && !grenadeItem.IsDummy)
                    {
                        Apply(grenadeItem, explosionPosition);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Blast: {ex.Message}");
                }
            }
        }

        /// <summary>TNT-equivalent charge in kg (public figures, rounded).</summary>
        private static float Charge(string name, IExplosiveItem item)
        {
            string n = (name ?? "").ToUpperInvariant();
            if (n.Contains("M67")) return 0.24f;      // 185 g Comp B
            if (n.Contains("RGD")) return 0.11f;      // 110 g TNT
            if (n.Contains("F-1") || n.Contains("F1")) return 0.06f; // 60 g TNT
            if (n.Contains("RGN") || n.Contains("RGO")) return 0.12f;
            if (n.Contains("VOG-25") || n.Contains("VOG25")) return 0.05f;
            if (n.Contains("VOG")) return 0.04f;
            if (n.Contains("40X46") || n.Contains("M406") || n.Contains("M433")) return 0.045f; // 32-45 g Comp B (0.10.5)
            if (n.Contains("V40")) return 0.015f;
            if (n.Contains("ZARYA") || n.Contains("FLASH")) return 0.01f;  // flashbang: ears, not lungs
            if (n.Contains("RDG") || n.Contains("M18") || n.Contains("SMOKE")) return 0f;
            return item.FragmentsCount > 0 ? 0.08f : 0f;
        }

        /// <summary>Peak incident overpressure, kPa (Kinney-Graham), for TNT kg at metres.</summary>
        internal static float Overpressure(float kg, float metres)
        {
            if (kg <= 0f) return 0f;
            float z = Mathf.Max(metres, 0.05f) / Mathf.Pow(kg, 1f / 3f);
            float num = 808f * (1f + Sq(z / 4.5f));
            float den = Mathf.Sqrt((1f + Sq(z / 0.048f)) * (1f + Sq(z / 0.32f)) * (1f + Sq(z / 1.35f)));
            return 101.325f * num / den;
        }

        private static float Sq(float x) => x * x;

        private static void Apply(IExplosiveItem grenadeItem, Vector3 at)
        {
            string name = grenadeItem is Item item ? Hits.HitCollector.ItemName(item) : "explosive";
            float charge = Charge(name, grenadeItem);
            if (charge <= 0f || !Singleton<GameWorld>.Instantiated)
            {
                return;
            }

            // Near the ground the reflected wave adds up (hemispherical burst).
            bool ground = Physics.Raycast(at + Vector3.up * 0.05f, Vector3.down, 0.6f, LayersMaskController.HighPolyWithTerrainMask);
            float w = charge * (ground ? 1.8f : 1f);

            // Heads and limbs next to the charge (dead bodies only, once EFT's damage is in).
            Gore.Dismemberment.Instance?.OnExplosion(at, charge, name);

            // Corpses in range are thrown.
            foreach (var player in Singleton<GameWorld>.Instance.AllPlayersEverExisted.ToList())
            {
                if (player == null || player.ActiveHealthController == null || player.ActiveHealthController.IsAlive) continue;
                var chest = Chest(player);
                float d = Vector3.Distance(chest, at);
                if (d > 6f) continue;
                float kpa = Overpressure(w, d) * Cover(at, chest);
                if (kpa < 20f) continue;
                Impacts.Instance?.Push(player, chest, Away(at, chest) * Mathf.Min(60f, kpa * 0.12f), corpse: true, "blast");
            }
        }

        private static Vector3 Chest(Player player)
        {
            var bones = player.PlayerBones;
            return bones?.Ribcage?.Original != null ? (bones.Ribcage.Original.position + bones.Pelvis.Original.position) * 0.5f : player.Position + Vector3.up * 1f;
        }

        private static Vector3 Away(Vector3 from, Vector3 to)
        {
            var d = to - from;
            d.y = Mathf.Max(d.y, 0f);
            return (d.normalized + Vector3.up * 0.35f).normalized;
        }

        /// <summary>Solid geometry between blast and body: the wave diffracts but loses most of its peak.</summary>
        private static float Cover(Vector3 from, Vector3 to) =>
            Physics.Linecast(from, to, LayersMaskController.HighPolyWithTerrainMask) ? 0.35f : 1f;
    }
}
