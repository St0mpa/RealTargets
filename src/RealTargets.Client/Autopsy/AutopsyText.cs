using EFT;

namespace RealTargets.Autopsy
{
    /// <summary>Plain-language names for the autopsy report: colliders, body parts, roles, causes of death.</summary>
    internal static class AutopsyText
    {
        internal static string Part(EBodyPart part) => part switch
        {
            EBodyPart.Head => "Head",
            EBodyPart.Chest => "Thorax",
            EBodyPart.Stomach => "Stomach",
            EBodyPart.LeftArm => "Left arm",
            EBodyPart.RightArm => "Right arm",
            EBodyPart.LeftLeg => "Left leg",
            EBodyPart.RightLeg => "Right leg",
            _ => part.ToString(),
        };

        /// <summary>Where on the body a collider is ("upper chest"); capital = sentence start.</summary>
        internal static string Collider(EBodyPartColliderType collider, bool capital = false)
        {
            string s = collider switch
            {
                EBodyPartColliderType.HeadCommon => "head",
                EBodyPartColliderType.ParietalHead => "top of the head",
                EBodyPartColliderType.BackHead => "back of the head",
                EBodyPartColliderType.Ears => "ear",
                EBodyPartColliderType.Eyes => "eyes",
                EBodyPartColliderType.Jaw => "jaw",
                EBodyPartColliderType.NeckFront => "throat",
                EBodyPartColliderType.NeckBack => "back of the neck",
                EBodyPartColliderType.RibcageUp => "upper chest",
                EBodyPartColliderType.RibcageLow => "lower chest",
                EBodyPartColliderType.SpineTop => "upper back",
                EBodyPartColliderType.SpineDown => "lower back",
                EBodyPartColliderType.RightSideChestUp => "right side (upper)",
                EBodyPartColliderType.LeftSideChestUp => "left side (upper)",
                EBodyPartColliderType.RightSideChestDown => "right side (lower)",
                EBodyPartColliderType.LeftSideChestDown => "left side (lower)",
                EBodyPartColliderType.Pelvis => "pelvis",
                EBodyPartColliderType.PelvisBack => "buttocks",
                EBodyPartColliderType.LeftUpperArm => "left upper arm",
                EBodyPartColliderType.LeftForearm => "left forearm",
                EBodyPartColliderType.RightUpperArm => "right upper arm",
                EBodyPartColliderType.RightForearm => "right forearm",
                EBodyPartColliderType.LeftThigh => "left thigh",
                EBodyPartColliderType.LeftCalf => "left calf",
                EBodyPartColliderType.RightThigh => "right thigh",
                EBodyPartColliderType.RightCalf => "right calf",
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
            if (side == EPlayerSide.Usec) return "PMC USEC";
            if (side == EPlayerSide.Bear) return "PMC BEAR";
            string name = role.ToString();
            switch (role)
            {
                case WildSpawnType.assault:
                case WildSpawnType.cursedAssault:
                case WildSpawnType.assaultGroup:
                    return "Scav";
                case WildSpawnType.marksman:
                    return "Scav sniper";
                case WildSpawnType.pmcBot:
                    return "Raider";
                case WildSpawnType.exUsec:
                    return "Rogue";
            }
            if (name.StartsWith("boss")) return "Boss " + Title(name.Substring(4));
            if (name.StartsWith("follower")) return "Guard";
            if (name.StartsWith("sectant")) return "Cultist";
            return Title(name);
        }

        private static string Title(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>What EFT says finished the body (Player.LastDamageType), for deaths without a recorded killing projectile.</summary>
        internal static string DeathType(EDamageType type) => type switch
        {
            EDamageType.Bullet or EDamageType.Sniper => "gunshot",
            EDamageType.Explosion or EDamageType.ThermobaricExplosion or EDamageType.Artillery => "explosion",
            EDamageType.GrenadeFragment => "grenade fragments",
            EDamageType.Landmine => "landmine",
            EDamageType.LightBleeding => "bled out (light bleeding)",
            EDamageType.HeavyBleeding => "bled out (heavy bleeding)",
            EDamageType.Fall => "fall",
            EDamageType.Melee => "melee",
            EDamageType.Blunt => "blunt trauma",
            EDamageType.Barbed => "barbed wire",
            EDamageType.Flame or EDamageType.HotGases => "burns",
            EDamageType.Btr => "BTR fire",
            EDamageType.Dehydration => "dehydration",
            EDamageType.Exhaustion => "exhaustion",
            EDamageType.Poison or EDamageType.LethalToxin => "poison",
            EDamageType.RadExposure => "radiation",
            _ => type.ToString().ToLowerInvariant(),
        };

        /// <summary>Health effect class name to a short label; null = not worth listing.</summary>
        internal static string Effect(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            if (typeName.Contains("HeavyBleeding")) return "heavy bleed";
            if (typeName.Contains("LightBleeding")) return "light bleed";
            if (typeName.Contains("Fracture")) return "fracture";
            return null;
        }

        internal static string Ago(System.TimeSpan span)
        {
            if (span.TotalSeconds < 60) return $"{span.TotalSeconds:F0} s ago";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            return $"{(int)span.TotalHours} h {span.Minutes} min ago";
        }
    }
}
