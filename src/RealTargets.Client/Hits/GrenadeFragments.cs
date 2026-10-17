using EFT;
using UnityEngine;

namespace RealTargets.Hits
{
    /// <summary>
    /// Physical grenade fragments. EFT fires grenade shrapnel as "... Shrapnel" ammo of 0.09 g at
    /// 40-90 m/s (about 0 J) while each piece deals ~54 HP, so energy-based reactions saw grenades
    /// as harmless (0.7.0 test). Real fragments: mass and initial speed per grenade type (public
    /// figures, rounded), slowed by air drag: v = v0 * exp(-d / L), L = 12 m * (m / 0.4 g)^(1/3)
    /// (small, irregular pieces lose about half their speed in ~10 m).
    /// </summary>
    internal static class GrenadeFragments
    {
        internal static bool IsFragment(string ammoName, EDamageType damageType) =>
            damageType == EDamageType.GrenadeFragment ||
            (ammoName != null && ammoName.IndexOf("shrapnel", System.StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>(mass g, speed m/s) of a fragment from this grenade after travelling distance metres.</summary>
        internal static (float mass, float speed) At(string weaponName, float distance)
        {
            var (mass, v0) = Profile(weaponName ?? "");
            float length = 12f * Mathf.Pow(mass / 0.4f, 1f / 3f);
            return (mass, v0 * Mathf.Exp(-Mathf.Max(0f, distance) / length));
        }

        private static (float mass, float v0) Profile(string name)
        {
            string n = name.ToUpperInvariant();
            if (n.Contains("F-1") || n.Contains("F1")) return (2.0f, 730f);      // cast iron body, ~290 pieces
            if (n.Contains("M67")) return (0.35f, 1500f);                          // notched steel
            if (n.Contains("RGD")) return (0.4f, 1200f);                            // RGD-5
            if (n.Contains("RGN") || n.Contains("RGO")) return (0.4f, 1300f);       // two-layer bodies
            if (n.Contains("VOG")) return (0.3f, 1100f);                             // grenade-launcher rounds
            if (n.Contains("V40")) return (0.15f, 1100f);                            // mini grenade, small balls
            return (0.4f, 1100f);
        }
    }
}
