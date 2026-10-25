using EFT;
using RealTargets.Localization;

namespace RealTargets.Autopsy
{
    /// <summary>Plain-language names for the autopsy report: colliders, body parts, roles, causes of death.</summary>
    internal static class AutopsyText
    {
        internal static string Part(EBodyPart part) => part switch
        {
            EBodyPart.Head => Loc.T("part.Head"),
            EBodyPart.Chest => Loc.T("part.Chest"),
            EBodyPart.Stomach => Loc.T("part.Stomach"),
            EBodyPart.LeftArm => Loc.T("part.LeftArm"),
            EBodyPart.RightArm => Loc.T("part.RightArm"),
            EBodyPart.LeftLeg => Loc.T("part.LeftLeg"),
            EBodyPart.RightLeg => Loc.T("part.RightLeg"),
            _ => part.ToString(),
        };

        /// <summary>Where on the body a collider is ("upper chest"); capital = sentence start.</summary>
        internal static string Collider(EBodyPartColliderType collider, bool capital = false)
        {
            string s = collider switch
            {
                EBodyPartColliderType.HeadCommon => Loc.T("place.HeadCommon"),
                EBodyPartColliderType.ParietalHead => Loc.T("place.ParietalHead"),
                EBodyPartColliderType.BackHead => Loc.T("place.BackHead"),
                EBodyPartColliderType.Ears => Loc.T("place.Ears"),
                EBodyPartColliderType.Eyes => Loc.T("place.Eyes"),
                EBodyPartColliderType.Jaw => Loc.T("place.Jaw"),
                EBodyPartColliderType.NeckFront => Loc.T("place.NeckFront"),
                EBodyPartColliderType.NeckBack => Loc.T("place.NeckBack"),
                EBodyPartColliderType.RibcageUp => Loc.T("place.RibcageUp"),
                EBodyPartColliderType.RibcageLow => Loc.T("place.RibcageLow"),
                EBodyPartColliderType.SpineTop => Loc.T("place.SpineTop"),
                EBodyPartColliderType.SpineDown => Loc.T("place.SpineDown"),
                EBodyPartColliderType.RightSideChestUp => Loc.T("place.RightSideChestUp"),
                EBodyPartColliderType.LeftSideChestUp => Loc.T("place.LeftSideChestUp"),
                EBodyPartColliderType.RightSideChestDown => Loc.T("place.RightSideChestDown"),
                EBodyPartColliderType.LeftSideChestDown => Loc.T("place.LeftSideChestDown"),
                EBodyPartColliderType.Pelvis => Loc.T("place.Pelvis"),
                EBodyPartColliderType.PelvisBack => Loc.T("place.PelvisBack"),
                EBodyPartColliderType.LeftUpperArm => Loc.T("place.LeftUpperArm"),
                EBodyPartColliderType.LeftForearm => Loc.T("place.LeftForearm"),
                EBodyPartColliderType.RightUpperArm => Loc.T("place.RightUpperArm"),
                EBodyPartColliderType.RightForearm => Loc.T("place.RightForearm"),
                EBodyPartColliderType.LeftThigh => Loc.T("place.LeftThigh"),
                EBodyPartColliderType.LeftCalf => Loc.T("place.LeftCalf"),
                EBodyPartColliderType.RightThigh => Loc.T("place.RightThigh"),
                EBodyPartColliderType.RightCalf => Loc.T("place.RightCalf"),
                _ => collider.ToString(),
            };
            return capital && s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;
        }

        /// <summary>EFT body part a collider belongs to (exits only carry the collider).</summary>
        internal static EBodyPart PartOf(EBodyPartColliderType collider) => collider switch
        {
            EBodyPartColliderType.HeadCommon or EBodyPartColliderType.ParietalHead or EBodyPartColliderType.BackHead or
                EBodyPartColliderType.Ears or EBodyPartColliderType.Eyes or EBodyPartColliderType.Jaw or
                EBodyPartColliderType.NeckFront or EBodyPartColliderType.NeckBack => EBodyPart.Head,
            EBodyPartColliderType.Pelvis or EBodyPartColliderType.PelvisBack or EBodyPartColliderType.SpineDown or
                EBodyPartColliderType.RibcageLow or EBodyPartColliderType.RightSideChestDown or
                EBodyPartColliderType.LeftSideChestDown => EBodyPart.Stomach,
            EBodyPartColliderType.LeftUpperArm or EBodyPartColliderType.LeftForearm => EBodyPart.LeftArm,
            EBodyPartColliderType.RightUpperArm or EBodyPartColliderType.RightForearm => EBodyPart.RightArm,
            EBodyPartColliderType.LeftThigh or EBodyPartColliderType.LeftCalf => EBodyPart.LeftLeg,
            EBodyPartColliderType.RightThigh or EBodyPartColliderType.RightCalf => EBodyPart.RightLeg,
            _ => EBodyPart.Chest,
        };

        /// <summary>"PMC USEC", "Scav", "Boss Killa", "Raider"...</summary>
        internal static string Role(EPlayerSide side, WildSpawnType role)
        {
            if (side == EPlayerSide.Usec) return Loc.T("role.usec");
            if (side == EPlayerSide.Bear) return Loc.T("role.bear");
            string name = role.ToString();
            switch (role)
            {
                case WildSpawnType.assault:
                case WildSpawnType.cursedAssault:
                case WildSpawnType.assaultGroup:
                    return Loc.T("role.scav");
                case WildSpawnType.marksman:
                    return Loc.T("role.scav_sniper");
                case WildSpawnType.pmcBot:
                    return Loc.T("role.raider");
                case WildSpawnType.exUsec:
                    return Loc.T("role.rogue");
            }
            if (name.StartsWith("boss")) return Loc.F("role.boss", ("name", Title(name.Substring(4))));
            if (name.StartsWith("follower")) return Loc.T("role.guard");
            if (name.StartsWith("sectant")) return Loc.T("role.cultist");
            return Title(name);
        }

        private static string Title(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>What EFT says finished the body (Player.LastDamageType), for deaths without a recorded killing projectile.</summary>
        internal static string DeathType(EDamageType type) => type switch
        {
            EDamageType.Bullet or EDamageType.Sniper => Loc.T("death.gunshot"),
            EDamageType.Explosion or EDamageType.ThermobaricExplosion or EDamageType.Artillery => Loc.T("death.explosion"),
            EDamageType.GrenadeFragment => Loc.T("death.grenade_fragments"),
            EDamageType.Landmine => Loc.T("death.landmine"),
            EDamageType.LightBleeding => Loc.T("death.light_bleeding"),
            EDamageType.HeavyBleeding => Loc.T("death.heavy_bleeding"),
            EDamageType.Fall => Loc.T("death.fall"),
            EDamageType.Melee => Loc.T("death.melee"),
            EDamageType.Blunt => Loc.T("death.blunt"),
            EDamageType.Barbed => Loc.T("death.barbed_wire"),
            EDamageType.Flame or EDamageType.HotGases => Loc.T("death.burns"),
            EDamageType.Btr => Loc.T("death.btr"),
            EDamageType.Dehydration => Loc.T("death.dehydration"),
            EDamageType.Exhaustion => Loc.T("death.exhaustion"),
            EDamageType.Poison or EDamageType.LethalToxin => Loc.T("death.poison"),
            EDamageType.RadExposure => Loc.T("death.radiation"),
            _ => type.ToString().ToLowerInvariant(),
        };

        /// <summary>Health effect class name to a short label; null = not worth listing.</summary>
        internal static string Effect(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            if (typeName.Contains("HeavyBleeding")) return Loc.T("effect.heavy_bleed");
            if (typeName.Contains("LightBleeding")) return Loc.T("effect.light_bleed");
            if (typeName.Contains("Fracture")) return Loc.T("effect.fracture");
            return null;
        }

        internal static string Ago(System.TimeSpan span)
        {
            if (span.TotalSeconds < 60) return Loc.F("time.ago_seconds", ("s", span.TotalSeconds.ToString("F0")));
            if (span.TotalMinutes < 60) return Loc.F("time.ago_minutes", ("min", (int)span.TotalMinutes));
            return Loc.F("time.ago_hours", ("h", (int)span.TotalHours), ("min", span.Minutes));
        }
    }
}
