using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>
    /// How oxygenated the blood leaving a wound is (1.2.0 blood realism pass, Stompa: "different color for lung shots etc
    /// where blood is more oxygenated"). Visual only; picked per bleed site from where the round went.
    /// </summary>
    internal enum BloodTone
    {
        /// <summary>Most wounds: venous and capillary blood (~75 % saturated), dark red with a slight blue cast.</summary>
        Venous,
        /// <summary>A cut artery (~98 % saturated): bright scarlet with a touch of orange, only while it is fresh.</summary>
        Arterial,
        /// <summary>Lung wounds and coughed airway blood: bright red and frothy (air whipped into it), pinkish with bubbles.</summary>
        Pulmonary,
    }

    /// <summary>
    /// One place for every blood colour (1.2.0). Before this, each texture had its own reds and the wound side ran
    /// 50-100 % brighter than the pools (entry halo 0.42, stain 0.45, exit edge 0.62/0.16/0.15 vs pool 0.30): a Forge user
    /// called it "fluorescent red", Stompa's shotgun photo (2026-10-17) "bright paint-red discs".
    /// Values are albedo under the lit shaders. EFT renders in **Gamma** colour space (blood-1 log): lighting runs on these
    /// values as they are, so a torch or a bright probe lifts a mid red far more than in a linear pipeline. blood-1 test:
    /// films at 0.25-0.45 R read as paint on bodies and floors while the 0.16-0.245 pool looked right, so films sit at
    /// pool level now. What drives them:
    /// - thickness (Beer-Lambert): a thin film lets light through the red end only and reads bright and saturated; a
    ///   pool a few mm deep absorbs nearly everything and reads near-black maroon with highlights;
    /// - oxygenation: arterial and lung blood scarlet, venous darker and a little purple (blue a hair above green);
    /// - age: fresh and wet; clotting over 5-10 min (darker, a little duller); thin parts drying from ~15 min to
    ///   red-brown and then brown-black (haemoglobin to methaemoglobin); a pool's middle stays wet for hours;
    /// - substrate: soaked fabric is darker and browner than blood on a hard floor.
    /// No channel is exactly 0: pure (R, 0, 0) is what reads as paint or neon on screen.
    /// </summary>
    internal static class BloodPalette
    {
        // ---- Films and drops (marks, stains, drips) ----------------------------------------------
        internal static readonly Color VenousFilm = new Color(0.185f, 0.021f, 0.024f);
        internal static readonly Color ArterialFilm = new Color(0.29f, 0.034f, 0.029f);
        internal static readonly Color PulmonaryFilm = new Color(0.30f, 0.045f, 0.042f);
        /// <summary>Froth bubbles: a paler red where the light catches the foam (blood-1: 0.66/0.36 read salmon).</summary>
        internal static readonly Color Froth = new Color(0.42f, 0.17f, 0.16f);

        // ---- Pools -------------------------------------------------------------------------------
        /// <summary>A pool's deep middle, fresh.</summary>
        internal static readonly Color PoolThick = new Color(0.128f, 0.014f, 0.017f);
        /// <summary>A pool's thin edge, fresh.</summary>
        internal static readonly Color PoolThin = new Color(0.195f, 0.022f, 0.024f);
        /// <summary>Clotted (after ~10 min): darker, a little browner. blood-5c: was 0.115 R (with EFT's shader an old pool went light).</summary>
        internal static readonly Color Clotted = new Color(0.088f, 0.011f, 0.012f);
        /// <summary>
        /// Dried thin film (edge of a pool, a drip after ~30 min): brown-black. blood-5c: was (0.120, 0.046, 0.034), which EFT's
        /// shader lit into salmon/orange (Stompa: an old pool "looks really fake").
        /// </summary>
        internal static readonly Color DriedThin = new Color(0.066f, 0.026f, 0.021f);

        // ---- On fabric ----------------------------------------------------------------------------
        internal static readonly Color SoakWet = new Color(0.125f, 0.013f, 0.015f);
        internal static readonly Color SoakEdge = new Color(0.165f, 0.019f, 0.019f);

        // ---- Wounds -------------------------------------------------------------------------------
        /// <summary>Looking into a bullet track: almost black, a little red.</summary>
        internal static readonly Color WoundTrack = new Color(0.045f, 0.008f, 0.009f);
        /// <summary>Margin of abrasion around a skin entry: fresh reddish-brown (scraped skin), darkening as it dries.</summary>
        internal static readonly Color Abrasion = new Color(0.31f, 0.115f, 0.085f);
        /// <summary>Torn muscle at an exit: dark red.</summary>
        internal static readonly Color Muscle = new Color(0.27f, 0.045f, 0.042f);
        /// <summary>Subcutaneous fat seen in a torn exit: dull yellow, sparse.</summary>
        internal static readonly Color Fat = new Color(0.62f, 0.52f, 0.30f);
        /// <summary>Everted skin edge at an exit, with blood on it.</summary>
        internal static readonly Color TornSkin = new Color(0.34f, 0.10f, 0.085f);
        /// <summary>Bullet wipe: the grey-black ring a bullet leaves on fabric (lubricant, lead, soot).</summary>
        internal static readonly Color BulletWipe = new Color(0.065f, 0.055f, 0.052f);

        /// <summary>Thin-film colour for a tone (droplets, marks, stains).</summary>
        internal static Color Film(BloodTone tone)
        {
            switch (tone)
            {
                case BloodTone.Arterial: return ArterialFilm;
                case BloodTone.Pulmonary: return PulmonaryFilm;
                default: return VenousFilm;
            }
        }

        /// <summary>
        /// Lit material tint for ground marks of a tone, relative to the venous atlas (the lit shader multiplies texture
        /// by _Color; above 1 brightens).
        /// </summary>
        internal static Color MarkTint(BloodTone tone)
        {
            var f = Film(tone);
            return new Color(f.r / VenousFilm.r, f.g / VenousFilm.g, f.b / VenousFilm.b, 1f);
        }

        /// <summary>
        /// A pool's fresh colour at <paramref name="thickness"/> (0 thin edge, 1 deep middle), shifted toward scarlet by
        /// <paramref name="oxygen"/> (0 venous, 1 arterial/lung). Pooled blood loses the arterial look within minutes
        /// (<paramref name="clot"/> 0..1 darkens it).
        /// </summary>
        internal static Color Pool(float thickness, float oxygen, float clot)
        {
            var c = Color.Lerp(PoolThin, PoolThick, Mathf.Clamp01(thickness));
            // Oxygenated: brighter red, less blue. Fades as the pool clots (deoxygenates at depth).
            float o = Mathf.Clamp01(oxygen) * (1f - Mathf.Clamp01(clot));
            c = new Color(c.r * (1f + 0.45f * o), c.g * (1f + 0.35f * o), c.b * (1f - 0.15f * o));
            // blood-5c: the clotted colour keeps the thickness gradient (a thin part stays a little lighter than the deep middle),
            // so a clotted pool isn't one flat colour.
            return Color.Lerp(c, Clotted * Mathf.Lerp(1.2f, 0.8f, Mathf.Clamp01(thickness)), Mathf.Clamp01(clot) * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(thickness)));
        }

        /// <summary>How oxygenated blood of a tone looks (0 venous, 1 arterial).</summary>
        internal static float Oxygen(BloodTone tone) => tone == BloodTone.Venous ? 0f : tone == BloodTone.Arterial ? 1f : 0.8f;

        /// <summary>Particle start colour of a flying droplet (unlit particle shader: kept darker than the film).</summary>
        internal static Color Droplet(BloodTone tone)
        {
            var f = Film(tone) * 1.1f;
            f.a = 0.95f;
            return f;
        }

        /// <summary>Mist cloud colour (fine droplets scatter light: lighter, a little less saturated than a film).</summary>
        internal static Color Mist(BloodTone tone)
        {
            switch (tone)
            {
                case BloodTone.Pulmonary: return new Color(0.46f, 0.11f, 0.10f, 1f);
                case BloodTone.Arterial: return new Color(0.44f, 0.055f, 0.05f, 1f);
                default: return new Color(0.38f, 0.042f, 0.042f, 1f);
            }
        }

        /// <summary>Tone of blood leaving through this collider when no artery is cut: lung for the chest, else venous.</summary>
        internal static BloodTone ToneFor(EBodyPartColliderType collider, bool arterial)
        {
            if (arterial) return BloodTone.Arterial;
            return WoundPath.IsChest(collider) ? BloodTone.Pulmonary : BloodTone.Venous;
        }
    }
}
