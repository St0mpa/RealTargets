using System;
using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using UnityEngine;

namespace RealTargets.Hits
{
    /// <summary>One collider a projectile passed through on its way through a target.</summary>
    public readonly struct HitSegment
    {
        public readonly EBodyPart BodyPart;
        public readonly EBodyPartColliderType Collider;
        /// <summary>Armour plate hit on this segment; 0 = none (the enum is bit flags from 1).</summary>
        public readonly EArmorPlateCollider Plate;
        public readonly Vector3 Point;
        /// <summary>DamageInfo.Damage before ApplyShot ran.</summary>
        public readonly float DamageIn;
        /// <summary>
        /// DamageInfo.Damage after ApplyShot ran: what got past armour. Was always 0 for the
        /// local player in early raids, real later; see docs.
        /// </summary>
        public readonly float DamageOut;
        public readonly float PenetrationIn;
        /// <summary>PlayerHitInfo.Penetrated. Not "beat the armour"; see docs.</summary>
        public readonly bool Penetrated;
        /// <summary>Target still alive right after this collider's ApplyShot.</summary>
        public readonly bool AliveAfter;
        /// <summary>Projectile speed at this collider (m/s), from the live Shot; 0 if unknown.</summary>
        public readonly float ImpactSpeed;
        /// <summary>Projectile direction at this collider (bullets deviate inside the body).</summary>
        public readonly Vector3 Direction;

        public HitSegment(
            EBodyPart bodyPart,
            EBodyPartColliderType collider,
            EArmorPlateCollider plate,
            Vector3 point,
            float damageIn,
            float damageOut,
            float penetrationIn,
            bool penetrated,
            bool aliveAfter,
            float impactSpeed,
            Vector3 direction)
        {
            BodyPart = bodyPart;
            Collider = collider;
            Plate = plate;
            Point = point;
            DamageIn = damageIn;
            DamageOut = damageOut;
            PenetrationIn = penetrationIn;
            Penetrated = penetrated;
            AliveAfter = aliveAfter;
            ImpactSpeed = impactSpeed;
            Direction = direction;
        }

        public bool HasPlate => Plate != 0;
    }

    public enum HitStatus
    {
        /// <summary>Target alive after this projectile.</summary>
        Alive,
        /// <summary>This projectile killed the target.</summary>
        Killed,
        /// <summary>Target already dead when this projectile arrived.</summary>
        Corpse,
    }

    /// <summary>
    /// One projectile hitting one target. A bullet that crosses several colliders (e.g.
    /// arm then chest) is one HitEvent with several segments; each shotgun pellet or
    /// grenade fragment is its own HitEvent. Per-projectile features (wounds, blood) use these.
    /// </summary>
    public sealed class HitEvent
    {
        public Player Target;
        public string TargetName;
        public bool IsAI;
        public bool IsLocalPlayer;
        public ShotId ShotId;
        /// <summary>Cartridge/grenade id from ShotId; shared by all pellets/fragments of it.</summary>
        public string SourceId;
        public int FragmentIndex;
        public EDamageType DamageType;
        public Item Weapon;
        public string WeaponName;
        /// <summary>Where the shot was fired from (DamageInfo.MasterOrigin).</summary>
        public Vector3 Origin;
        public Vector3 Direction;
        public float Distance;
        public DateTime WallTime;
        public bool WasAlive;
        public HitStatus Status;
        public readonly List<HitSegment> Segments = new List<HitSegment>(4);
        /// <summary>Health of each body part this projectile touched, read at end of frame.</summary>
        public readonly Dictionary<EBodyPart, (float current, float maximum)> HealthAfter =
            new Dictionary<EBodyPart, (float, float)>();

        // Ballistics from the live Shot (M1). Unknown values stay 0 / null.
        public string AmmoName;
        public string Caliber;
        public float BulletMassGram;
        public float MuzzleSpeed;
        /// <summary>Projectiles per cartridge (8 for buckshot, 1 for bullets).</summary>
        public int ProjectileCount = 1;

        /// <summary>
        /// Grenade fragment with physical mass/speed from GrenadeFragments (EFT's own: 0.09 g at
        /// ~80 m/s, i.e. ~0 J, while dealing 54 HP). 0 = use the Shot's speed.
        /// </summary>
        public float FragmentSpeed;
        public bool IsFragment => FragmentSpeed > 0f;
        public float ImpactSpeed => FragmentSpeed > 0f ? FragmentSpeed : Entry.ImpactSpeed;

        /// <summary>
        /// Where the projectile left colliders (EFT's backward hits, which never reach ApplyShot).
        /// Plate = the exit was through an armour plate collider.
        /// </summary>
        public readonly List<(EBodyPartColliderType collider, bool plate, Vector3 point)> Exits =
            new List<(EBodyPartColliderType, bool, Vector3)>(2);

        public HitSegment Entry => Segments[0];
        public bool AliveAfter => Segments[Segments.Count - 1].AliveAfter;
        public bool HasBallistics => BulletMassGram > 0f && ImpactSpeed > 0f;

        /// <summary>Kinetic energy at the first collider, joules.</summary>
        public float ImpactEnergy => HasBallistics ? HitModel.Energy(BulletMassGram, ImpactSpeed) : 0f;
        public float ImpactMomentum => HasBallistics ? HitModel.Momentum(BulletMassGram, ImpactSpeed) : 0f;
        /// <summary>
        /// Share of the damage that reached flesh. EFT splits a plate hit into the plate segment
        /// (damage out 0: it moves on) and the flesh behind it, so a round that beat the plate is
        /// measured at the first flesh segment against what struck the plate; a plate-only hit is
        /// stopped (0.7.1 fix: plate penetrations used to read as stopped, 2570 J -> 1285 J felt).
        /// </summary>
        public float FleshFraction
        {
            get
            {
                if (Entry.HasPlate)
                {
                    int flesh = Segments.FindIndex(s => !s.HasPlate);
                    if (flesh > 0 && Entry.DamageIn > 0.01f)
                    {
                        return Mathf.Clamp01(Segments[flesh].DamageOut / Entry.DamageIn);
                    }
                }
                return HitModel.FleshFraction(Entry.DamageIn, Entry.DamageOut);
            }
        }
        public float EffectiveEnergy => HitModel.EffectiveEnergy(ImpactEnergy, FleshFraction);
    }

    /// <summary>
    /// Everything one cartridge (or grenade) did to one target: a single bullet, or all
    /// pellets of a shotgun shell (which can land a frame apart). Whole-body reactions
    /// (flinch, impulse) use these so a shell is one impact, not eight.
    /// </summary>
    public sealed class HitVolley
    {
        public readonly List<HitEvent> Projectiles = new List<HitEvent>(8);

        /// <summary>Flinch strength applied by the Flinch feature; 0 = none.</summary>
        public float FlinchStrength;

        public HitEvent First => Projectiles[0];
        public Player Target => First.Target;
        public int Count => Projectiles.Count;

        public HitStatus Status
        {
            get
            {
                bool anyAlive = false;
                foreach (var p in Projectiles)
                {
                    if (p.Status == HitStatus.Killed)
                    {
                        return HitStatus.Killed;
                    }
                    anyAlive |= p.Status == HitStatus.Alive;
                }
                return anyAlive ? HitStatus.Alive : HitStatus.Corpse;
            }
        }

        public float DamageIn => Sum(s => s.DamageIn);
        public float DamageOut => Sum(s => s.DamageOut);

        public float ImpactEnergy => SumProjectiles(p => p.ImpactEnergy);
        public float EffectiveEnergy => SumProjectiles(p => p.EffectiveEnergy);
        public float ImpactMomentum => SumProjectiles(p => p.ImpactMomentum);

        public HitSeverity Severity => HitModel.Severity(EffectiveEnergy, DamageIn, DamageOut);

        /// <summary>The projectile that delivered the most energy (drives direction and hit point).</summary>
        public HitEvent Dominant
        {
            get
            {
                var best = First;
                foreach (var p in Projectiles)
                {
                    if (p.EffectiveEnergy > best.EffectiveEnergy)
                    {
                        best = p;
                    }
                }
                return best;
            }
        }

        private float Sum(Func<HitSegment, float> pick)
        {
            float total = 0f;
            foreach (var p in Projectiles)
            {
                foreach (var s in p.Segments)
                {
                    total += pick(s);
                }
            }
            return total;
        }

        private float SumProjectiles(Func<HitEvent, float> pick)
        {
            float total = 0f;
            foreach (var p in Projectiles)
            {
                total += pick(p);
            }
            return total;
        }
    }
}
