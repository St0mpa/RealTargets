using System;
using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using RealTargets.Patches;
using UnityEngine;

namespace RealTargets.Hits
{
    /// <summary>Pre-hit state captured by ApplyShotPatch's prefix.</summary>
    internal struct ShotSnapshot
    {
        public DamageInfo DamageInfo;
        public bool TargetAlive;
        /// <summary>The worn armour piece covering the hit collider (null = none), and its durability before the hit.</summary>
        public ArmorComponent Armour;
        public float DurabilityBefore;
    }

    /// <summary>
    /// Groups the per-collider ApplyShot calls of one projectile into a HitEvent (published at
    /// end of frame), and the projectiles of one cartridge on one target into a HitVolley.
    /// A shell's pellets can land a frame apart at range, and a single bullet can cross a plate
    /// in one frame and the body in the next (both seen 2026-10-11), so a volley is published
    /// once all of a shell's pellets have arrived, or after [Hit model] Volley window seconds.
    /// </summary>
    internal static class HitCollector
    {
        /// <summary>
        /// False once ApplyShotPatch's armour lookup failed: segments then carry no armour info, and the
        /// armour-stop tests fall back to the damage ratio alone (HitSegment.ThroughArmour).
        /// </summary>
        internal static bool ArmourLookupWorks = true;

        /// <summary>Raised once per projectile per target, at the end of the frame it hit.</summary>
        public static event Action<HitEvent> Published;

        /// <summary>Raised once per cartridge per target, when the volley is complete.</summary>
        public static event Action<HitVolley> PublishedVolley;

        // ShotId keeps its parts private; read them without boxing. If a game update renames
        // them, volleys fall back to one projectile each instead of breaking hit capture.
        private static readonly AccessTools.StructFieldRef<ShotId, string> AmmoIdRef;
        private static readonly AccessTools.StructFieldRef<ShotId, int> FragmentRef;

        private static readonly Dictionary<(int targetId, ShotId shot), HitEvent> Pending =
            new Dictionary<(int, ShotId), HitEvent>();
        private static readonly List<HitEvent> Order = new List<HitEvent>();
        // Each projectile's last finished hit per body, briefly: a later frame's hit by the same round continues it.
        private static readonly Dictionary<(int targetId, ShotId shot), (HitEvent hit, float time)> Recent =
            new Dictionary<(int, ShotId), (HitEvent, float)>();
        private const float ContinueWindow = 0.25f;
        private static readonly List<(int, ShotId)> Stale = new List<(int, ShotId)>();

        private sealed class OpenVolley
        {
            public HitVolley Volley;
            public float LastHitTime;
        }

        private static readonly Dictionary<(int targetId, string source), OpenVolley> Open =
            new Dictionary<(int, string), OpenVolley>();
        private static readonly List<(int, string)> ReadyKeys = new List<(int, string)>();

        static HitCollector()
        {
            try
            {
                AmmoIdRef = AccessTools.StructFieldRefAccess<ShotId, string>("_ammoId");
                FragmentRef = AccessTools.StructFieldRefAccess<ShotId, int>("_fragmentIndex");
            }
            catch (Exception ex)
            {
                AmmoIdRef = null;
                FragmentRef = null;
                Plugin.Log.LogWarning($"ShotId fields not found; hits won't group into volleys: {ex.Message}");
            }
        }

        internal static void Record(
            Player target,
            in ShotSnapshot before,
            in DamageInfo after,
            EBodyPart bodyPart,
            EBodyPartColliderType collider,
            EArmorPlateCollider plate,
            ShotId shotId,
            PlayerHitInfo result)
        {
            var info = before.DamageInfo;
            var health = target.HealthController;
            string sourceId = AmmoIdRef != null ? AmmoIdRef(ref shotId) : null;

            // The live Shot, if this hit is being resolved by GameWorld.ShotDelegate.
            var shot = ShotContext.Current;
            if (shot != null && (shot.Ammo == null || shot.Ammo.Id != sourceId))
            {
                shot = null;
            }

            var segment = new HitSegment(
                bodyPart,
                collider,
                plate,
                info.HitPoint,
                info.Damage,
                after.Damage,
                info.PenetrationPower,
                result != null && result.Penetrated,
                health != null && health.IsAlive,
                shot != null ? shot.VelocityMagnitude : 0f,
                shot != null && shot.Direction.sqrMagnitude > 0f ? shot.Direction.normalized : info.Direction.normalized,
                ArmourOf(before, after));

            // Only bullets share a ShotId across colliders; anything else stands alone.
            bool groupable = info.DamageType == EDamageType.Bullet;
            var key = (target.Id, shotId);

            // Grenade fragments come as Bullet damage with ONE ShotId per grenade (ExplosionSharedMethods fires them all
            // from one CreateFragment() ammo): two fragments landing in the same frame are two projectiles, unless the
            // earlier one went through its collider (1.1.0-legs test: an RGD-5 record LeftCalf | Pelvis | RightCalf,
            // all penetrated=False, became one round crossing both legs).
            bool fragment = GrenadeFragments.IsFragment(shot?.Ammo != null ? ItemName(shot.Ammo) : null, info.DamageType);
            if (groupable && Pending.TryGetValue(key, out HitEvent existing) &&
                (!fragment || existing.Segments[existing.Segments.Count - 1].Penetrated))
            {
                existing.Segments.Add(segment);
                return;
            }

            var hit = new HitEvent
            {
                Target = target,
                TargetName = target.Profile?.Nickname ?? "?",
                IsAI = target.IsAI,
                IsLocalPlayer = target.IsYourPlayer,
                ShotId = shotId,
                SourceId = sourceId,
                FragmentIndex = FragmentRef != null ? FragmentRef(ref shotId) : -1,
                DamageType = info.DamageType,
                Weapon = info.Weapon,
                WeaponName = ItemName(info.Weapon),
                Origin = info.MasterOrigin,
                Direction = info.Direction,
                Distance = Vector3.Distance(info.MasterOrigin, info.HitPoint),
                WallTime = DateTime.Now,
                WasAlive = before.TargetAlive,
            };
            SetShooter(hit, info);
            var head = target.PlayerBones?.Head?.Original;
            if (head != null)
            {
                hit.HasHeadPose = true;
                hit.HeadPosition = head.position;
                hit.HeadRotation = head.rotation;
            }
            hit.Segments.Add(segment);
            // A later frame's hit by the same bullet goes on from inside; a grenade's fragments share one id, so a later
            // fragment is a new piece of steel, never the earlier one continuing.
            if (groupable && !fragment && Recent.TryGetValue(key, out var recent) && Time.time - recent.time <= ContinueWindow)
            {
                hit.ContinuesFrom = recent.hit;
            }

            if (shot != null)
            {
                hit.BulletMassGram = shot.BulletMassGram;
                hit.MuzzleSpeed = shot.InitialSpeed;
                if (shot.Ammo is Ammo ammo)
                {
                    hit.AmmoName = ItemName(ammo);
                    hit.Caliber = ammo.Caliber;
                    hit.ProjectileCount = Mathf.Max(1, ammo.ProjectileCount);
                }

                // EFT's pellet masses aren't physical (7 mm buckshot = 0.09 g, real ~2 g), so for
                // multi-projectile ammo use at least a lead sphere of the pellet's diameter.
                if (hit.ProjectileCount > 1 && shot.BulletDiameterMilimeters > 0f)
                {
                    hit.BulletMassGram = Mathf.Max(hit.BulletMassGram, LeadSphereGram(shot.BulletDiameterMilimeters));
                }
            }

            // Grenade shrapnel: physical fragment mass and speed (EFT's are ~0 J).
            if (GrenadeFragments.IsFragment(hit.AmmoName, info.DamageType))
            {
                var (mass, speed) = GrenadeFragments.At(hit.WeaponName, hit.Distance);
                hit.BulletMassGram = mass;
                hit.FragmentSpeed = speed;
            }

            if (groupable)
            {
                Pending[key] = hit;
            }
            Order.Add(hit);
        }

        /// <summary>
        /// A projectile leaving a body collider (EFT's backward hit). Attached to the projectile's
        /// HitEvent from this frame; an exit without a matching entry this frame is dropped.
        /// </summary>
        internal static void RecordExit(Player target, ShotId shotId, EBodyPartColliderType collider, bool plate, Vector3 point)
        {
            if (target != null && Pending.TryGetValue((target.Id, shotId), out HitEvent hit))
            {
                hit.Exits.Add((collider, plate, point));
            }
        }

        private static readonly List<ArmorComponent> WornArmour = new List<ArmorComponent>(8);

        /// <summary>
        /// The worn armour piece EFT's Player.ProceedDamageThroughArmor hits first for this collider: the one that
        /// blocked or deflected the round if any (DamageInfo.BlockedBy/DeflectedBy), else the first that covers it.
        /// Called from ApplyShotPatch's prefix so the durability before the hit can be kept.
        /// </summary>
        internal static ArmorComponent FindArmour(Player target, in DamageInfo info, EBodyPartColliderType collider, EArmorPlateCollider plate)
        {
            var inventory = target != null ? target.Inventory : null;
            if (inventory == null) return null;
            WornArmour.Clear();
            inventory.GetPutOnArmorsNonAlloc(WornArmour);
            ArmorComponent first = null;
            foreach (var armour in WornArmour)
            {
                if (armour?.Item == null || !armour.ShotMatches(collider, plate)) continue;
                var id = armour.Item.Id;
                if ((info.BlockedBy.HasValue && info.BlockedBy.Value == id) || (info.DeflectedBy.HasValue && info.DeflectedBy.Value == id))
                {
                    return armour;
                }
                first ??= armour;
            }
            return first;
        }

        private static ArmourHit ArmourOf(in ShotSnapshot before, in DamageInfo after)
        {
            var armour = before.Armour;
            if (armour?.Item == null) return default;
            var id = armour.Item.Id;
            var outcome = after.BlockedBy.HasValue && after.BlockedBy.Value == id ? ArmourOutcome.Stopped
                : after.DeflectedBy.HasValue && after.DeflectedBy.Value == id ? ArmourOutcome.Deflected
                : ArmourOutcome.Penetrated;
            var repairable = armour.Repairable;
            return new ArmourHit(ItemName(armour.Item), id.ToString(), armour.ArmorClass, outcome, before.DurabilityBefore,
                repairable != null ? repairable.Durability : before.DurabilityBefore, repairable != null ? repairable.MaxDurability : 0f);
        }

        private static void SetShooter(HitEvent hit, in DamageInfo info)
        {
            try
            {
                var shooter = info.Player?.iPlayer;
                if (shooter == null) return;
                hit.ShooterProfileId = shooter.ProfileId;
                hit.ShooterIsYou = shooter.IsYourPlayer;
                var profile = shooter.Profile;
                hit.ShooterName = profile?.Nickname ?? "?";
                hit.ShooterSide = shooter.Side;
                if (profile?.Info?.Settings != null) hit.ShooterRole = profile.Info.Settings.Role;
            }
            catch (Exception)
            {
                // A shooter that has despawned mid-flight: leave it unknown.
            }
        }

        /// <summary>
        /// Splits the projectile's way through flesh into legs (HitEvent.Legs) and fills in exits EFT didn't report.
        /// Entries (flesh segments) and exits (backward hits) are ordered along the shot. An exit closes a leg; a
        /// later entry opens a new one only where the round really was outside the body in between (a limb into
        /// the torso or the other leg, >= 3 cm apart: 1.1.0 test, TAC-X through a forearm into the chest got no
        /// hole on the chest); overlapping colliders inside one region (RibcageUp, SpineTop) stay one leg. A limb
        /// left without a reported exit gets one on its own collider. The last leg, if still open, gets the old
        /// inference: EFT doesn't always report the backward hit (a 12 ga slug crossed ParietalHead then BackHead at
        /// 465 m/s with no exit, 0.9.1 test; a 7.62x51 through a naked chest reported one collider, 0.10.0).
        /// </summary>
        private static void BuildLegs(HitEvent hit)
        {
            hit.Legs.Clear();
            int first = hit.Segments.FindIndex(s => !s.HasPlate);
            if (first < 0) return;
            var origin = hit.Segments[first].Point;
            var dir0 = DirectionOf(hit, hit.Segments[first]);

            var events = new List<(float along, bool exit, int index)>(hit.Segments.Count + hit.Exits.Count);
            for (int i = first; i < hit.Segments.Count; i++)
            {
                if (!hit.Segments[i].HasPlate) events.Add((Vector3.Dot(hit.Segments[i].Point - origin, dir0), false, i));
            }
            for (int j = 0; j < hit.Exits.Count; j++)
            {
                if (!hit.Exits[j].plate) events.Add((Vector3.Dot(hit.Exits[j].point - origin, dir0), true, j));
            }
            // Along the shot; at the same spot the entry first.
            events.Sort((a, b) => a.along != b.along ? a.along.CompareTo(b.along) : a.exit.CompareTo(b.exit));

            bool open = false;
            var leg = default(HitLeg);
            int last = first;
            float closedAt = 0f;
            foreach (var ev in events)
            {
                if (!ev.exit)
                {
                    var seg = hit.Segments[ev.index];
                    if (!open && hit.Legs.Count > 0)
                    {
                        // Out of the body and in again, or just the next collider of the same region?
                        if (ev.along - closedAt >= 0.03f && Apart(hit.Segments[last].Collider, seg.Collider))
                        {
                            leg = NewLeg(hit, ev.index, false);
                        }
                        else
                        {
                            leg = hit.Legs[hit.Legs.Count - 1];
                            hit.Legs.RemoveAt(hit.Legs.Count - 1);
                            leg.Exited = false;
                            leg.InferredExit = false;
                        }
                        open = true;
                    }
                    else if (!open)
                    {
                        leg = NewLeg(hit, ev.index, StartsInside(hit, seg));
                        open = true;
                    }
                    else if (Apart(hit.Segments[last].Collider, seg.Collider) && ev.along - Vector3.Dot(hit.Segments[last].Point - origin, dir0) >= 0.03f)
                    {
                        // Left a limb with no exit reported: its exit on its own collider, then the next part. Only when
                        // it really got out: a bullet still >= 150 m/s entering the next part (EFT reports
                        // penetrated=False on every corpse hit, so that flag alone can't say), a fragment only where EFT
                        // says it went through (1.1.0-legs: 53-76 m/s fragments got exits on both calves). Else that
                        // stretch ended inside the limb.
                        var limb = hit.Segments[last];
                        bool through = hit.IsFragment ? limb.Penetrated && hit.FragmentSpeed >= 150f : seg.ImpactSpeed >= 150f || seg.ImpactSpeed <= 0f;
                        if (through) CloseOnCollider(hit, ref leg, limb, seg.Point);
                        hit.Legs.Add(leg);
                        leg = NewLeg(hit, ev.index, false);
                    }
                    last = ev.index;
                }
                else
                {
                    var e = hit.Exits[ev.index];
                    // A limb's exit while this stretch is elsewhere (a forearm's far side lying past the chest entry,
                    // arm against the chest) doesn't end it; torso regions join, so their exits do.
                    var exitPart = PartOf(e.collider);
                    if (open && IsLimb(exitPart) && exitPart != PartOf(hit.Segments[last].Collider) &&
                        exitPart != PartOf(hit.Segments[leg.EntrySegment].Collider))
                    {
                        continue;
                    }
                    if (open)
                    {
                        leg.Exited = true;
                        leg.End = e.point;
                        leg.ExitCollider = e.collider;
                        hit.Legs.Add(leg);
                        open = false;
                        closedAt = ev.along;
                    }
                    else if (hit.Legs.Count > 0 && ev.along > closedAt && ev.along - closedAt < 0.8f)
                    {
                        // A second exit of the same stretch (overlapping colliders): the farther one is where it left.
                        var l = hit.Legs[hit.Legs.Count - 1];
                        l.End = e.point;
                        l.ExitCollider = e.collider;
                        hit.Legs[hit.Legs.Count - 1] = l;
                        closedAt = ev.along;
                    }
                }
            }

            if (open)
            {
                InferLegExit(hit, ref leg, last);
                hit.Legs.Add(leg);
            }

            // Older readers look at Exits: inferred ones go there too.
            foreach (var l in hit.Legs)
            {
                if (l.Exited && l.InferredExit) hit.Exits.Add((l.ExitCollider, false, l.End));
            }
        }

        private static Vector3 DirectionOf(HitEvent hit, HitSegment seg) =>
            seg.Direction.sqrMagnitude > 0.5f ? seg.Direction.normalized : hit.Direction.normalized;

        private static HitLeg NewLeg(HitEvent hit, int segment, bool fromInside)
        {
            var seg = hit.Segments[segment];
            return new HitLeg
            {
                EntrySegment = segment,
                Start = seg.Point,
                Direction = DirectionOf(hit, seg),
                ExitCollider = seg.Collider,
                FromInside = fromInside,
                Speed = seg.ImpactSpeed > 0f ? seg.ImpactSpeed : hit.ImpactSpeed,
            };
        }

        /// <summary>
        /// The same round hit this body in an earlier frame and this collider is in the body part it was last in, close
        /// by: it goes on from inside (no new hole). A round never leaves a part and comes back into it; an exit the
        /// earlier frame recorded there was an inner collider's far side (1.1.0 test: a 9x33R through the eyes hit
        /// BackHead 24 ms later at 209 m/s and got an entry hole on the back of the head).
        /// </summary>
        private static bool StartsInside(HitEvent hit, HitSegment seg)
        {
            var before = hit.ContinuesFrom;
            if (before == null || before.Legs.Count == 0) return false;
            var lastSeg = before.Segments[before.Segments.Count - 1];
            if (lastSeg.BodyPart != seg.BodyPart) return false;
            return (seg.Point - lastSeg.Point).sqrMagnitude <= 0.35f * 0.35f;
        }

        /// <summary>
        /// Two colliders with skin between them along a shot: a limb and anything else, except a thigh into the
        /// pelvis (one piece of flesh).
        /// </summary>
        private static bool Apart(EBodyPartColliderType a, EBodyPartColliderType b)
        {
            var pa = PartOf(a);
            var pb = PartOf(b);
            if (pa == pb) return false;
            bool limbA = IsLimb(pa), limbB = IsLimb(pb);
            if (!limbA && !limbB) return false;
            bool thighA = a == EBodyPartColliderType.LeftThigh || a == EBodyPartColliderType.RightThigh;
            bool thighB = b == EBodyPartColliderType.LeftThigh || b == EBodyPartColliderType.RightThigh;
            if ((thighA && pb == EBodyPart.Stomach) || (thighB && pa == EBodyPart.Stomach)) return false;
            return true;
        }

        private static bool IsLimb(EBodyPart p) =>
            p == EBodyPart.LeftArm || p == EBodyPart.RightArm || p == EBodyPart.LeftLeg || p == EBodyPart.RightLeg;

        // Same mapping as AutopsyText.PartOf (exits only carry the collider).
        private static EBodyPart PartOf(EBodyPartColliderType c) => c switch
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

        /// <summary>Head, torso (chest + stomach: one cavity) or one limb.</summary>
        private static EBodyPart Region(EBodyPartColliderType c)
        {
            var part = PartOf(c);
            return part == EBodyPart.Stomach ? EBodyPart.Chest : part;
        }

        /// <summary>The collider belongs to that region; a thigh's stretch may also leave through the pelvis (one piece of flesh).</summary>
        private static bool SameRegion(EBodyPart region, EBodyPartColliderType c)
        {
            if (Region(c) == region) return true;
            bool pelvis = c == EBodyPartColliderType.Pelvis || c == EBodyPartColliderType.PelvisBack;
            return pelvis && (region == EBodyPart.LeftLeg || region == EBodyPart.RightLeg);
        }

        /// <summary>A limb's far side along the shot: its collider seen from the next entry back towards the shooter.</summary>
        private static void CloseOnCollider(HitEvent hit, ref HitLeg leg, HitSegment limb, Vector3 next)
        {
            var dir = leg.Direction;
            var end = Vector3.Lerp(limb.Point, next, 0.5f);
            var colliders = hit.Target != null && hit.Target.PlayerBones != null ? hit.Target.PlayerBones.BodyPartCollidersDictionary : null;
            if (colliders != null && colliders.TryGetValue(limb.Collider, out var bpc) && bpc?.Collider != null)
            {
                float span = Vector3.Distance(limb.Point, next);
                if (bpc.Collider.Raycast(new Ray(next, -dir), out var info, span)) end = info.point;
            }
            leg.Exited = true;
            leg.InferredExit = true;
            leg.End = end + dir * 0.005f;
            leg.ExitCollider = limb.Collider;
        }

        /// <summary>The last leg, still open: did it go out the far side?</summary>
        private static void InferLegExit(HitEvent hit, ref HitLeg leg, int lastSegment)
        {
            var seg = hit.Segments[lastSegment];
            // Carried on from an earlier frame and EFT says this collider stopped it: it stayed in.
            if (leg.FromInside && !seg.Penetrated) return;
            // Still fast entering a later collider of this stretch: it has gone out the far side.
            if (lastSegment > leg.EntrySegment && seg.Penetrated && seg.ImpactSpeed >= 150f)
            {
                leg.Exited = true;
                leg.InferredExit = true;
                leg.End = seg.Point + DirectionOf(hit, seg) * 0.03f;
                leg.ExitCollider = seg.Collider;
                return;
            }
            if (InferExitThroughBody(hit, hit.Segments[leg.EntrySegment], leg, out var exit, out var collider))
            {
                leg.Exited = true;
                leg.InferredExit = true;
                leg.End = exit;
                leg.ExitCollider = collider;
            }
        }

        /// <summary>
        /// The body's far side along the leg is found by raycasting its own colliders backwards from 1 m past the
        /// leg's entry; if that thickness is within what the projectile can still cross in tissue it went through:
        /// fragments ~8 cm, a buckshot pellet ~25 cm, bullets 20 cm + 1 cm per 25 J (9x19 ~40 cm, rifles up to
        /// 90 cm). Energy = what it had going into this leg (its speed there) past any armour on it.
        /// </summary>
        private static bool InferExitThroughBody(HitEvent hit, HitSegment entry, HitLeg leg, out Vector3 exit, out EBodyPartColliderType exitCollider)
        {
            exit = default;
            exitCollider = entry.Collider;
            var colliders = hit.Target != null && hit.Target.PlayerBones != null ? hit.Target.PlayerBones.BodyPartCollidersDictionary : null;
            if (colliders == null || !hit.HasBallistics) return false;
            // Slow and blunt (0.10.5 test: an unarmed 40 mm grenade at 76 m/s read "through" the pelvis):
            // nothing under ~150 m/s crosses a body; flares, unarmed grenades and spent rounds stay in.
            float speed = hit.IsFragment ? hit.ImpactSpeed : leg.Speed;
            if (speed < 150f) return false;

            bool firstLeg = leg.EntrySegment == hit.Segments.FindIndex(s => !s.HasPlate);
            float flesh = firstLeg ? hit.FleshFraction
                : entry.ThroughArmour ? HitModel.FleshFraction(entry.DamageIn, entry.DamageOut) : 1f;
            float energy = HitModel.Energy(hit.BulletMassGram, speed) * Mathf.Max(flesh, 0.05f);
            // Pellets by energy (0.10.1: a 54 J #4 pellet "went through" a chest): 1 cm per 5 J, 4-30 cm.
            float reach = hit.IsFragment ? 0.08f : hit.ProjectileCount > 1 ? Mathf.Clamp(energy / 500f, 0.04f, 0.3f)
                : Mathf.Clamp(0.2f + energy / 2500f, 0.2f, 0.9f);

            // Only the region this stretch is in: an arm beside the chest is no exit of the chest (1.1.0-legs:
            // RibcageUp -> LeftUpperArm (inferred)). A round that went on into the arm hits it, and EFT reports that.
            var region = Region(entry.Collider);
            var dir = leg.Direction;
            var ray = new Ray(entry.Point + dir * 1f, -dir);
            float best = float.MaxValue;
            foreach (var pair in colliders)
            {
                var bpc = pair.Value;
                if (bpc == null || bpc.Collider == null || bpc is ArmorPlateCollider || !SameRegion(region, pair.Key)) continue;
                if (bpc.Collider.Raycast(ray, out var info, 1f) && info.distance < best)
                {
                    best = info.distance;
                    exit = info.point;
                    exitCollider = pair.Key;
                }
            }

            if (best == float.MaxValue) return false;
            float thickness = 1f - best;
            if (thickness < 0.03f || thickness > reach) return false;
            exit += dir * 0.005f;
            return true;
        }

        /// <summary>Drops everything in flight; called while no raid is running.</summary>
        internal static void Reset()
        {
            if (Order.Count > 0 || Open.Count > 0 || Recent.Count > 0)
            {
                Order.Clear();
                Pending.Clear();
                Open.Clear();
                Recent.Clear();
            }
        }

        /// <summary>Called every frame in raid from Plugin.LateUpdate.</summary>
        internal static void Flush()
        {
            if (Order.Count > 0)
            {
                // Copy first: a subscriber may cause new hits while we publish.
                var finished = Order.ToArray();
                Order.Clear();
                Pending.Clear();

                try
                {
                    Finish(finished);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"HitEvent finish failed: {ex}");
                    return;
                }

                foreach (var hit in finished)
                {
                    Publish(Published, hit);
                    AddToVolley(hit);
                    if (hit.Target != null && hit.DamageType == EDamageType.Bullet) Recent[(hit.Target.Id, hit.ShotId)] = (hit, Time.time);
                }
            }

            if (Recent.Count > 0)
            {
                Stale.Clear();
                foreach (var pair in Recent)
                {
                    if (Time.time - pair.Value.time > ContinueWindow) Stale.Add(pair.Key);
                }
                foreach (var key in Stale) Recent.Remove(key);
            }

            PublishReadyVolleys();
        }

        private static void AddToVolley(HitEvent hit)
        {
            // No id to group by (non-bullet damage): the projectile is its own volley.
            if (string.IsNullOrEmpty(hit.SourceId) || hit.Target == null)
            {
                var single = new HitVolley();
                single.Projectiles.Add(hit);
                Publish(PublishedVolley, single);
                return;
            }

            var key = (hit.Target.Id, hit.SourceId);
            if (!Open.TryGetValue(key, out OpenVolley open))
            {
                open = new OpenVolley { Volley = new HitVolley() };
                Open[key] = open;
            }

            open.Volley.Projectiles.Add(hit);
            open.LastHitTime = Time.time;
        }

        private static void PublishReadyVolleys()
        {
            if (Open.Count == 0)
            {
                return;
            }

            float now = Time.time;
            float window = Plugin.VolleyWindow.Value;
            ReadyKeys.Clear();

            foreach (var pair in Open)
            {
                var volley = pair.Value.Volley;
                // A shell is complete once every pellet has landed. A single bullet can't be known
                // complete: it may cross a plate in one frame and the body in the next (.50 BMG
                // slug, 2026-10-11), so it always waits for the window.
                int expected = volley.First.ProjectileCount;
                bool complete = expected > 1 && volley.Count >= expected;
                if (complete || now - pair.Value.LastHitTime >= window)
                {
                    ReadyKeys.Add(pair.Key);
                }
            }

            foreach (var key in ReadyKeys)
            {
                var volley = Open[key].Volley;
                Open.Remove(key);
                Publish(PublishedVolley, volley);
            }
        }

        /// <summary>End-of-frame state: exact status per projectile and body-part health.</summary>
        private static void Finish(HitEvent[] hits)
        {
            foreach (var hit in hits)
            {
                BuildLegs(hit);

                hit.Status = !hit.WasAlive ? HitStatus.Corpse
                    : hit.AliveAfter ? HitStatus.Alive
                    : HitStatus.Killed;

                var health = hit.Target != null ? hit.Target.HealthController : null;
                if (health == null)
                {
                    continue;
                }

                foreach (var segment in hit.Segments)
                {
                    if (!hit.HealthAfter.ContainsKey(segment.BodyPart))
                    {
                        var value = health.GetBodyPartHealth(segment.BodyPart, true);
                        hit.HealthAfter[segment.BodyPart] = (value.Current, value.Maximum);
                    }
                }
            }

            // If death is applied after ApplyShot returns (seen on the local player), no
            // projectile saw the target die; credit the last one that hit it alive.
            var credited = new HashSet<int>();
            for (int i = hits.Length - 1; i >= 0; i--)
            {
                var hit = hits[i];
                if (hit.Target == null || credited.Contains(hit.Target.Id))
                {
                    continue;
                }

                credited.Add(hit.Target.Id);
                var health = hit.Target.HealthController;
                bool deadNow = health != null && !health.IsAlive;
                if (!deadNow || HasKiller(hits, hit.Target.Id))
                {
                    continue;
                }

                for (int j = i; j >= 0; j--)
                {
                    if (hits[j].Target == hit.Target && hits[j].WasAlive)
                    {
                        hits[j].Status = HitStatus.Killed;
                        break;
                    }
                }
            }
        }

        private static bool HasKiller(HitEvent[] hits, int targetId)
        {
            foreach (var hit in hits)
            {
                if (hit.Target != null && hit.Target.Id == targetId && hit.Status == HitStatus.Killed)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Publish<T>(Action<T> handler, T item)
        {
            if (handler == null || !Plugin.ModEnabled.Value)
            {
                return;
            }

            // Isolate subscribers so one failing feature doesn't starve the others.
            foreach (Action<T> subscriber in handler.GetInvocationList())
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    subscriber(item);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"{typeof(T).Name} subscriber failed: {ex}");
                }
                // 0.10.14 (0.10.13 raid: a 68 ms `Hits` frame the logged bakes don't explain): name the slow feature.
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms >= 8.0 && Plugin.PerfLogging)
                {
                    Plugin.Log.LogInfo($"[Perf] slow hit handler {ms:F1} ms: {subscriber.Method.DeclaringType?.Name}.{subscriber.Method.Name}");
                }
            }
        }

        /// <summary>Mass of a lead sphere (11.34 g/cm3) of the given diameter, grams.</summary>
        private static float LeadSphereGram(float diameterMm)
        {
            float r = diameterMm / 20f; // mm diameter -> cm radius
            return 11.34f * 4f / 3f * Mathf.PI * r * r * r;
        }

        internal static string ItemName(Item item)
        {
            if (item == null)
            {
                return "none";
            }

            try
            {
                // Some armour components (helmet inserts) have no short name: the autopsy listed "(class 3)" with no name.
                string name = LocalizationExtensions.LocalizedShortName(item);
                if (string.IsNullOrWhiteSpace(name)) name = LocalizationExtensions.LocalizedName(item);
                return string.IsNullOrWhiteSpace(name) ? item.TemplateId.ToString() : name;
            }
            catch (Exception)
            {
                return item.TemplateId.ToString();
            }
        }
    }
}
