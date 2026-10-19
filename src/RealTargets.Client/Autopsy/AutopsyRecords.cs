using System;
using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using RealTargets.Gore;
using RealTargets.Hits;
using UnityEngine;

namespace RealTargets.Autopsy
{
    /// <summary>A point on the autopsy diagram: canonical standing body, metres (x = body right, y = up from the pelvis).</summary>
    internal struct DiagramMark
    {
        public Vector2 Point;
        public bool Back;
        public bool Valid;
    }

    /// <summary>One projectile in a body, with its diagram marks (computed at hit time, from the pose it had then).</summary>
    internal sealed class AutopsyProjectile
    {
        public HitEvent Hit;
        public DiagramMark Entry;
        public readonly List<DiagramMark> Exits = new List<DiagramMark>(2);
        /// <summary>"from the front", "from behind", "from the left side"...</summary>
        public string From;
        /// <summary>The same points relative to the bone they struck, for the 3D view in the death pose.</summary>
        public BoneMark Entry3D;
        public readonly List<BoneMark> Exits3D = new List<BoneMark>(2);
        /// <summary>Every stretch through flesh (HitEvent.Legs via WoundPath), its own hit and later frames' included.</summary>
        public readonly List<LegMark> Legs3D = new List<LegMark>(2);
        /// <summary>The same round's hits on this body in later frames (it was still inside): one projectile.</summary>
        public readonly List<HitEvent> Continued = new List<HitEvent>(1);
    }

    /// <summary>One stretch through flesh for the 3D view: in at Start, out at End (if it went out).</summary>
    internal struct LegMark
    {
        public HitEvent Hit;
        public int Leg;
        public BoneMark Start;
        public BoneMark End;
        public bool Exited, FromInside, InferredExit;
        /// <summary>Energy it carried into this stretch, past armour (J).</summary>
        public float Energy;
    }

    /// <summary>A point (and the projectile's direction) in a bone's own space, taken when it hit.</summary>
    internal struct BoneMark
    {
        public Transform Bone;
        public Vector3 Local;
        public Vector3 LocalDirection;

        public bool Valid => Bone != null;

        /// <summary>World point with the bone as posed now; its own scale ignored (Dismemberment collapses bones to ~0).</summary>
        public Vector3 World => Bone.position + Bone.rotation * Local;
        public Vector3 WorldDirection => Bone.rotation * LocalDirection;

        public static BoneMark At(Transform bone, Vector3 world, Vector3 direction) => bone == null ? default : new BoneMark
        {
            Bone = bone,
            Local = Quaternion.Inverse(bone.rotation) * (world - bone.position),
            LocalDirection = Quaternion.Inverse(bone.rotation) * direction,
        };
    }

    /// <summary>One cartridge or grenade: a bullet, all pellets of a shell, all fragments of a grenade.</summary>
    internal sealed class AutopsyShot
    {
        public int Number;
        public string SourceId;
        public float Time;
        public readonly List<AutopsyProjectile> Projectiles = new List<AutopsyProjectile>(1);

        public HitEvent First => Projectiles[0].Hit;
        public bool Killing => Credited || KilledOutright;
        public bool KilledOutright => Projectiles.Exists(p => p.Hit.Status == HitStatus.Killed || p.Continued.Exists(c => c.Status == HitStatus.Killed));
        /// <summary>
        /// The body died within <see cref="AutopsyRecords.CreditWindow"/> s of this shot from something EFT counts apart
        /// (a bleed tick, 1.1.0 gore retest 4: a Deagle round through the forearm into the chest, dead 0.26 s later, read
        /// "bled out (light bleeding)"): the shot gets the kill.
        /// </summary>
        public bool Credited;
        /// <summary>Seconds from this shot to the death it is credited with.</summary>
        public float CreditDelay;
        /// <summary>EFT's explosion damage (no projectile): one row per blast that hurt this body.</summary>
        public bool IsBlast => First.IsBlast;
        public bool PostMortem => Projectiles.TrueForAll(p => p.Hit.Status == HitStatus.Corpse);
        public bool ByYou => First.ShooterIsYou;
    }

    internal sealed class GoreNote
    {
        public string What;
        public string Why;
        /// <summary>The shot that did it (0 = none recorded close in time, e.g. a blast without fragment hits).</summary>
        public int Shot;
    }

    /// <summary>Everything recorded about one body this raid.</summary>
    internal sealed class BodyRecord
    {
        public string EquipmentId;
        /// <summary>The body itself (the corpse keeps the Player object); null once EFT destroys it.</summary>
        public Player Player;
        public DeathPose Pose;
        public string ProfileId;
        public string Name;
        public string Role;
        public int Level;
        public readonly List<AutopsyShot> Shots = new List<AutopsyShot>();
        public int Projectiles;
        public int Dropped;

        public bool Dead;
        public DateTime DiedAt;
        /// <summary>Time.time of the death (shots keep Time.time too).</summary>
        public float DiedTime;
        public EDamageType DeathType;
        public EBodyPart LethalPart;
        public string KillerName;
        public bool KillerIsYou;
        public string KillerRole;
        public readonly Dictionary<EBodyPart, (float current, float maximum, bool destroyed)> HealthAtDeath =
            new Dictionary<EBodyPart, (float, float, bool)>();
        public readonly Dictionary<EBodyPart, List<string>> EffectsAtDeath = new Dictionary<EBodyPart, List<string>>();
        public readonly List<GoreNote> Gore = new List<GoreNote>();

        /// <summary>Bumped on every change so an open report knows to rebuild.</summary>
        public int Version;

        public AutopsyShot KillingShot => Shots.Find(s => s.Killing);
    }

    /// <summary>
    /// Per-raid hit history for every body (bots; your own PMC apart, as <see cref="Local"/>), keyed by the equipment item the
    /// loot screen gets: EFT's Corpse reuses the dead player's own Equipment (Player.CreateCorpse), so its id
    /// finds the body again. Fed by HitCollector.Published, Player.OnPlayerDeadStatic and Dismemberment.GoreDone.
    /// </summary>
    internal static class AutopsyRecords
    {
        private const int MaxProjectilesPerBody = 300;
        /// <summary>
        /// A death this soon after the last shot that hit the living body is that shot's (rc-1 test: a Saiga Piranha
        /// volley into the chest, dead 1.4 s later from bleeding, no shot after it). Dismemberment holds limbs 0.5 s.
        /// </summary>
        internal const float CreditWindow = 3f;

        /// <summary>
        /// No shot killed outright but the last one that hit the body alive came within <see cref="CreditWindow"/> s
        /// before the death: credit it. Runs at the death and when a shot is recorded later (volleys are published
        /// 50 ms+ after the hit, so a death right after one can come first).
        /// </summary>
        private static void CreditLateDeath(BodyRecord record)
        {
            if (!record.Dead || record.Shots.Exists(s => s.KilledOutright || s.Credited)) return;
            for (int i = record.Shots.Count - 1; i >= 0; i--)
            {
                var s = record.Shots[i];
                if (s.IsBlast || s.PostMortem) continue;
                // Shot.Time is when the volley was published, up to ~0.1 s after the hit itself.
                float delay = record.DiedTime - s.Time;
                if (delay < -0.15f) continue;
                if (delay > CreditWindow) return;
                s.Credited = true;
                s.CreditDelay = Mathf.Max(0f, delay);
                record.Version++;
                return;
            }
        }
        private const int MaxBodies = 600;

        private static readonly Dictionary<string, BodyRecord> ByEquipment = new Dictionary<string, BodyRecord>();
        private static readonly Dictionary<int, BodyRecord> ByPlayer = new Dictionary<int, BodyRecord>();
        private static readonly List<BodyRecord> Order = new List<BodyRecord>();

        private static BodyFrame _frame;
        private static int _frameTarget;
        private static int _frameNumber = -1;

        internal static readonly EBodyPart[] Parts =
        {
            EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach, EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg,
        };

        internal static void Enable()
        {
            HitCollector.Published += OnHit;
            Player.OnPlayerDeadStatic += OnDead;
            Dismemberment.GoreDone += OnGore;
        }

        internal static void ResetRaid()
        {
            ByEquipment.Clear();
            ByPlayer.Clear();
            Order.Clear();
            _frame = null;
            _frameNumber = -1;
        }

        internal static BodyRecord Find(Item equipment)
        {
            if (equipment == null) return null;
            return ByEquipment.TryGetValue(equipment.Id.ToString(), out var record) ? record : null;
        }

        /// <summary>
        /// Your own PMC this raid (1.1.0 death screen: the AUTOPSY switch on the raid results). Kept apart from the bodies
        /// (never found by equipment, never dropped for space) and past the raid end, until the next raid's player replaces it.
        /// </summary>
        internal static BodyRecord Local { get; private set; }

        /// <summary>A new raid: forget the last raid's PMC (unless it already is this raid's player).</summary>
        internal static void ForgetLocal(GameWorld world)
        {
            var you = world != null ? world.MainPlayer : null;
            if (Local != null && (you == null || !ReferenceEquals(Local.Player, you))) Local = null;
        }

        /// <summary>Gore our death-screen rules gave your PMC (DeathScreen; nothing changes on you in the raid).</summary>
        internal static void NoteLocalGore(string what, string why)
        {
            if (Local != null) AddGore(Local, what, why);
        }

        private static BodyRecord Get(Player player)
        {
            if (player == null || !Plugin.AutopsyEnabled.Value) return null;
            if (player.IsYourPlayer)
            {
                if (Local == null || !ReferenceEquals(Local.Player, player)) Local = NewRecord(player);
                return Local;
            }
            if (ByPlayer.TryGetValue(player.Id, out var record)) return record;

            record = NewRecord(player);
            if (record == null) return null;

            // Bound the history: drop the oldest bodies first (a long raid with many fights).
            if (Order.Count >= MaxBodies)
            {
                var oldest = Order[0];
                Order.RemoveAt(0);
                ByEquipment.Remove(oldest.EquipmentId);
                foreach (var pair in ByPlayer)
                {
                    if (pair.Value == oldest)
                    {
                        ByPlayer.Remove(pair.Key);
                        break;
                    }
                }
            }

            ByPlayer[player.Id] = record;
            ByEquipment[record.EquipmentId] = record;
            Order.Add(record);
            return record;
        }

        private static BodyRecord NewRecord(Player player)
        {
            var equipment = player.Inventory?.Equipment;
            if (equipment == null) return null;
            var profile = player.Profile;
            return new BodyRecord
            {
                EquipmentId = equipment.Id.ToString(),
                Player = player,
                ProfileId = player.ProfileId,
                Name = profile?.Nickname ?? "?",
                Role = AutopsyText.Role(player.Side, profile?.Info?.Settings != null ? profile.Info.Settings.Role : WildSpawnType.assault),
                Level = profile?.Info != null ? profile.Info.Level : 0,
            };
        }

        private static void OnHit(HitEvent hit)
        {
            var record = Get(hit.Target);
            if (record == null || hit.Segments.Count == 0) return;
            if (record.Projectiles >= MaxProjectilesPerBody)
            {
                record.Dropped++;
                return;
            }

            // The same round hitting this body again in a later frame (still inside, or after its helmet stopped or
            // deflected it): it continues that projectile, not a new shot. Grenade fragments never get ContinuesFrom
            // (HitCollector). Gore build test: limiting this to stretches from inside split a P90 round stopped by a
            // helmet into "2 of 2 pellets".
            if (hit.ContinuesFrom != null)
            {
                var earlier = FindProjectile(record, hit.ContinuesFrom);
                if (earlier != null)
                {
                    earlier.Continued.Add(hit);
                    AddLegs(earlier, hit);
                    record.Version++;
                    return;
                }
            }

            var projectile = new AutopsyProjectile { Hit = hit };
            var frame = Frame(hit.Target);
            if (frame != null)
            {
                var entry = hit.Entry;
                projectile.Entry = Map(frame, entry.Collider, entry.BodyPart, entry.Point);
                foreach (var exit in hit.Exits)
                {
                    projectile.Exits.Add(Map(frame, exit.collider, AutopsyText.PartOf(exit.collider), exit.point));
                }
                projectile.From = From(frame, entry.Direction.sqrMagnitude > 0.5f ? entry.Direction : hit.Direction);
            }
            try
            {
                var e = hit.Entry;
                var dir = (e.Direction.sqrMagnitude > 0.5f ? e.Direction : hit.Direction).normalized;
                projectile.Entry3D = BoneMark.At(BoneFor(hit.Target, e.Collider, e.BodyPart, e.Point), e.Point, dir);
                foreach (var exit in hit.Exits)
                {
                    projectile.Exits3D.Add(BoneMark.At(BoneFor(hit.Target, exit.collider, AutopsyText.PartOf(exit.collider), exit.point), exit.point, dir));
                }
                AddLegs(projectile, hit);
            }
            catch (Exception)
            {
                // No bone marks: this projectile is left out of the 3D view.
            }

            // Pellets of a shell and fragments of a grenade share the cartridge/grenade id: one shot.
            AutopsyShot shot = null;
            if (!string.IsNullOrEmpty(hit.SourceId))
            {
                for (int i = record.Shots.Count - 1; i >= 0 && i >= record.Shots.Count - 4; i--)
                {
                    var s = record.Shots[i];
                    if (s.SourceId == hit.SourceId && Time.time - s.Time < 1.5f)
                    {
                        shot = s;
                        break;
                    }
                }
            }
            if (shot == null)
            {
                shot = new AutopsyShot { Number = record.Shots.Count + 1, SourceId = hit.SourceId, Time = Time.time };
                record.Shots.Add(shot);
            }
            shot.Projectiles.Add(projectile);
            record.Projectiles++;
            record.Version++;
            if (record.Dead && hit.Status == HitStatus.Alive) CreditLateDeath(record);
        }

        /// <summary>
        /// A blast wave hurt this body (Features.Blast, EFT's explosion damage): its own BLAST row, no marks on the chart or
        /// the 3D model (no wound channel). The grenade's fragments arrive later as their own row.
        /// </summary>
        internal static void OnBlast(HitEvent blast)
        {
            var record = Get(blast.Target);
            if (record == null || blast.Segments.Count == 0) return;
            if (record.Projectiles >= MaxProjectilesPerBody)
            {
                record.Dropped++;
                return;
            }
            var projectile = new AutopsyProjectile { Hit = blast };
            var frame = Frame(blast.Target);
            if (frame != null) projectile.From = From(frame, blast.Direction);
            var shot = new AutopsyShot { Number = record.Shots.Count + 1, SourceId = null, Time = Time.time };
            shot.Projectiles.Add(projectile);
            record.Shots.Add(shot);
            record.Projectiles++;
            record.Version++;
        }

        private static readonly List<WoundPath> Paths = new List<WoundPath>(2);

        /// <summary>Bone marks for each stretch through flesh, taken now (the pose at hit time).</summary>
        private static void AddLegs(AutopsyProjectile projectile, HitEvent hit)
        {
            WoundPath.ResolveAll(hit, Paths);
            foreach (var path in Paths)
            {
                var leg = new LegMark
                {
                    Hit = hit,
                    Leg = path.Leg,
                    Start = BoneMark.At(BoneFor(hit.Target, path.Entry.Collider, path.Entry.BodyPart, path.Start), path.Start, path.Direction),
                    Exited = path.Exited,
                    FromInside = path.FromInside,
                    InferredExit = path.Leg < hit.Legs.Count && hit.Legs[path.Leg].InferredExit,
                    Energy = path.Energy,
                };
                if (path.Exited)
                {
                    leg.End = BoneMark.At(BoneFor(hit.Target, path.ExitCollider, AutopsyText.PartOf(path.ExitCollider), path.End), path.End, path.Direction);
                }
                if (leg.Start.Valid) projectile.Legs3D.Add(leg);
            }
        }

        private static AutopsyProjectile FindProjectile(BodyRecord record, HitEvent hit)
        {
            for (int i = record.Shots.Count - 1; i >= 0 && i >= record.Shots.Count - 4; i--)
            {
                foreach (var p in record.Shots[i].Projectiles)
                {
                    if (p.Hit == hit || p.Continued.Contains(hit)) return p;
                }
            }
            return null;
        }

        private static void OnDead(Player player, IPlayer aggressor, DamageInfo damageInfo, EBodyPart part)
        {
            try
            {
                var record = Get(player);
                if (record == null) return;
                record.Dead = true;
                try
                {
                    record.Pose = DeathPose.Capture(player);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Autopsy] death pose not captured: {ex.Message}");
                }
                record.DiedAt = DateTime.Now;
                record.DiedTime = Time.time;
                record.DeathType = player.LastDamageType;
                record.LethalPart = part;
                if (aggressor != null)
                {
                    record.KillerName = aggressor.Profile?.Nickname ?? "?";
                    record.KillerIsYou = aggressor.IsYourPlayer;
                    record.KillerRole = AutopsyText.Role(aggressor.Side,
                        aggressor.Profile?.Info?.Settings != null ? aggressor.Profile.Info.Settings.Role : WildSpawnType.assault);
                }

                // Vitals at the moment of death: EFT has already applied the killing damage here.
                var health = player.HealthController;
                if (health != null)
                {
                    foreach (var p in Parts)
                    {
                        var value = health.GetBodyPartHealth(p, true);
                        record.HealthAtDeath[p] = (value.Current, value.Maximum, health.IsBodyPartDestroyed(p));
                        var effects = new List<string>(2);
                        foreach (IHealthEffect effect in health.GetAllActiveEffects(p))
                        {
                            string label = AutopsyText.Effect(effect?.Type?.Name);
                            if (label != null && !effects.Contains(label)) effects.Add(label);
                        }
                        record.EffectsAtDeath[p] = effects;
                    }
                }
                CreditLateDeath(record);
                record.Version++;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Autopsy] death record failed: {ex.Message}");
            }
        }

        private static void OnGore(Player player, string what, string why)
        {
            var record = Get(player);
            if (record != null) AddGore(record, what, why);
        }

        private static void AddGore(BodyRecord record, string what, string why)
        {
            // Gore follows its shot within a volley window; a blast without fragment hits stays unlinked.
            int shot = 0;
            if (record.Shots.Count > 0)
            {
                var last = record.Shots[record.Shots.Count - 1];
                if (Time.time - last.Time < 0.6f) shot = last.Number;
            }
            record.Gore.Add(new GoreNote { What = what, Why = why, Shot = shot });
            record.Version++;
        }

        private static BodyFrame Frame(Player player)
        {
            if (player == null) return null;
            if (_frameNumber == Time.frameCount && _frameTarget == player.Id) return _frame;
            _frameNumber = Time.frameCount;
            _frameTarget = player.Id;
            try
            {
                _frame = BodyFrame.Build(player);
            }
            catch (Exception)
            {
                _frame = null;
            }
            return _frame;
        }

        /// <summary>
        /// The bone a hit point belongs to: its own (non-plate) collider's transform, else the nearest collider of the
        /// same body part (plate hits: the plate collider is not on the skeleton the pose drives).
        /// </summary>
        internal static Transform BoneFor(Player player, EBodyPartColliderType collider, EBodyPart part, Vector3 point)
        {
            var colliders = player != null && player.PlayerBones != null ? player.PlayerBones.BodyPartCollidersDictionary : null;
            if (colliders == null) return null;
            if (colliders.TryGetValue(collider, out var own) && own != null && !(own is ArmorPlateCollider) && own.Collider != null)
            {
                return own.Collider.transform;
            }
            Transform best = null;
            float bestDistance = float.MaxValue;
            foreach (var pair in colliders)
            {
                var bpc = pair.Value;
                if (bpc == null || bpc.Collider == null || bpc is ArmorPlateCollider || AutopsyText.PartOf(pair.Key) != part) continue;
                float d = (bpc.Collider.bounds.center - point).sqrMagnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = bpc.Collider.transform;
                }
            }
            return best;
        }

        private static string From(BodyFrame frame, Vector3 travel)
        {
            var from = -travel.normalized;
            float f = Vector3.Dot(from, frame.Forward), r = Vector3.Dot(from, frame.Right), u = Vector3.Dot(from, frame.Up);
            if (u > 0.75f) return "from above";
            if (u < -0.75f) return "from below";
            if (f > 0.5f) return "from the front";
            if (f < -0.5f) return "from behind";
            return r > 0f ? "from the right side" : "from the left side";
        }

        /// <summary>
        /// A world hit point to the canonical diagram. Limbs: the share along the limb's capsule (proximal ->
        /// distal) laid on the canonical limb, so a raised arm still lands on the arm. Torso and head: the
        /// frame-local offset from the pelvis, heights scaled to the canonical neck, kept inside the part.
        /// </summary>
        internal static DiagramMark Map(BodyFrame frame, EBodyPartColliderType collider, EBodyPart part, Vector3 world)
        {
            var mark = new DiagramMark { Valid = true };
            if (CanonicalBody.TryLimb(collider, out var c0, out var c1, out float cr) && frame.Limbs.TryGetValue(collider, out var limb))
            {
                var axis = limb.Distal - limb.Proximal;
                float length = Mathf.Max(axis.magnitude, 0.01f);
                axis /= length;
                float t = Mathf.Clamp01(Vector3.Dot(world - limb.Proximal, axis) / length);
                var offset = world - (limb.Proximal + axis * t * length);
                var side = Vector3.ProjectOnPlane(frame.Right, axis);
                float lateral = side.sqrMagnitude > 1e-6f ? Vector3.Dot(offset, side.normalized) / Mathf.Max(limb.Radius, 0.02f) : 0f;
                mark.Point = Vector2.Lerp(c0, c1, t) + new Vector2(Mathf.Clamp(lateral, -1f, 1f) * cr * 0.75f, 0f);
                mark.Back = Vector3.Dot(offset, limb.Forward) < 0f;
                return mark;
            }

            var local = frame.ToLocal("pelvis", world);
            float neck = frame.Anchors.TryGetValue("neck", out var n) ? Vector3.Dot(n - frame.Anchors["pelvis"], frame.Up) : 0.55f;
            float yScale = neck > 0.2f ? CanonicalBody.NeckY / neck : 1f;
            var p = new Vector2(local.x, local.y * yScale);

            string anchor = part == EBodyPart.Head ? "col:HeadCommon" : part == EBodyPart.Stomach ? "col:Pelvis" : "col:RibcageUp";
            if (!frame.Anchors.TryGetValue(anchor, out var centre))
            {
                centre = frame.Anchors.TryGetValue(part == EBodyPart.Head ? "head" : part == EBodyPart.Stomach ? "pelvis" : "ribcage", out var bone)
                    ? bone : frame.Anchors["pelvis"];
            }
            mark.Back = Vector3.Dot(world - centre, frame.Forward) < 0f;

            if (collider == EBodyPartColliderType.NeckFront || collider == EBodyPartColliderType.NeckBack)
            {
                mark.Back = collider == EBodyPartColliderType.NeckBack;
                mark.Point = CanonicalBody.ClampNeck(p);
            }
            else
            {
                mark.Point = CanonicalBody.ClampInto(part, p);
            }
            return mark;
        }
    }
}
