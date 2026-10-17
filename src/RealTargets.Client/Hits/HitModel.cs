using UnityEngine;

namespace RealTargets.Hits
{
    public enum HitSeverity
    {
        /// <summary>Armour stopped it: no damage reached flesh.</summary>
        Blocked,
        Light,
        Heavy,
        Massive,
    }

    /// <summary>
    /// M1 hit model: turns ballistics into numbers every reaction scales from.
    /// Impact energy uses the live impact speed from the Shot (not muzzle speed), so distance
    /// and penetration losses are already in it. "Effective" energy is what the body feels:
    /// armour still transfers part of the blow (ArmourTransfer), flesh hits transfer all of it.
    /// </summary>
    internal static class HitModel
    {
        /// <summary>Kinetic energy in joules. Mass in grams, speed in m/s.</summary>
        internal static float Energy(float massGram, float speed) =>
            0.5f * (massGram / 1000f) * speed * speed;

        /// <summary>Momentum in kg*m/s.</summary>
        internal static float Momentum(float massGram, float speed) =>
            massGram / 1000f * speed;

        /// <summary>Share of the incoming damage that got past armour, 0..1.</summary>
        internal static float FleshFraction(float damageIn, float damageOut) =>
            damageIn > 0.01f ? Mathf.Clamp01(damageOut / damageIn) : 1f;

        internal static float EffectiveEnergy(float energy, float fleshFraction)
        {
            float transfer = Plugin.ArmourTransfer.Value;
            return energy * (transfer + (1f - transfer) * fleshFraction);
        }

        internal static HitSeverity Severity(float effectiveEnergy, float damageIn, float damageOut)
        {
            if (damageIn > 0.01f && damageOut < 1f)
            {
                return HitSeverity.Blocked;
            }

            if (effectiveEnergy >= Plugin.MassiveEnergy.Value)
            {
                return HitSeverity.Massive;
            }

            return effectiveEnergy >= Plugin.HeavyEnergy.Value ? HitSeverity.Heavy : HitSeverity.Light;
        }
    }
}
