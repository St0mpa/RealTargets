using System.Collections.Generic;
using EFT;
using RealTargets.Hits;
using UnityEngine;

namespace RealTargets.Gore
{
    /// <summary>How a bleed site looks: drives cadence, spurting and clotting.</summary>
    internal enum BleedKind
    {
        /// <summary>Seeping: a drop now and then (grazes, pellets, fragments).</summary>
        Ooze,
        /// <summary>Steady trickle: a normal through-and-through or rifle wound.</summary>
        Flow,
        /// <summary>Pouring: big exit wounds, head/scalp, neck.</summary>
        Heavy,
        /// <summary>Spurting with the heartbeat: a cut artery (neck, groin, thigh, upper arm).</summary>
        Arterial,
    }

    /// <summary>
    /// One place blood leaves a body: a wound from one projectile (entry and, if it went through,
    /// exit), or later a stump (phase 2). Visual only: EFT's health decides everything else.
    /// </summary>
    internal sealed class BleedSite
    {
        public BleedKind Kind;
        /// <summary>How oxygenated its blood looks (1.2.0): scarlet artery, frothy lung, dark venous.</summary>
        public BloodTone Tone;
        public EBodyPart BodyPart;
        public EBodyPartColliderType Collider;
        public string Weapon;
        public float Energy;
        public float CreatedAt;

        // Where: entry (and exit) relative to the bone that carries them.
        public Transform Bone;
        public Vector3 LocalEntry;
        public Vector3 LocalOutward;
        public bool HasExit;
        public Vector3 LocalExit;
        public Vector3 LocalExitOutward;

        /// <summary>Flow at full blood pressure (ml/min); clots down over time unless arterial.</summary>
        public float Flow;
        /// <summary>Half-life of the flow while alive (s); 0 = doesn't clot (artery).</summary>
        public float ClotHalfLife;
        /// <summary>The bot treated this body part (EFT bleed effect removed): a trickle, no spurts.</summary>
        public bool Treated;
        /// <summary>After death: gravity drain at normal posture (ml/min), decays.</summary>
        public float DrainRate;

        /// <summary>What actually leaves now (ml/min), set by the tick.</summary>
        public float Rate;
        /// <summary>Blood that has left through this site (ml): feeds its pool and body stains.</summary>
        public float Drained;
        /// <summary>Stain patches laid on the body so far by both runs (blood running down from the wound).</summary>
        public int Stains;
        /// <summary>
        /// blood-5: blood runs down from the entry and from the exit, each its own run (blood-4 test: a pistol exit stayed dry
        /// while the entry ran; only the lower opening ran before).
        /// </summary>
        public readonly StainRun EntryRun = new StainRun(), ExitRun = new StainRun();
        /// <summary>Same cartridge (all pellets of one shell share it): close pellets join one site (blood-5).</summary>
        public string SourceId;

        public bool Spurts => Kind == BleedKind.Arterial && !Treated;

        public (Vector3 position, Vector3 outward) Entry =>
            (Bone.TransformPoint(LocalEntry), Bone.TransformDirection(LocalOutward).normalized);

        public (Vector3 position, Vector3 outward) Exit =>
            (Bone.TransformPoint(LocalExit), Bone.TransformDirection(LocalExitOutward).normalized);

        /// <summary>Entry or exit, whichever faces further down right now (where gravity drains it).</summary>
        public (Vector3 position, Vector3 outward) Lowest
        {
            get
            {
                var entry = Entry;
                if (!HasExit) return entry;
                var exit = Exit;
                return exit.outward.y < entry.outward.y ? exit : entry;
            }
        }

        /// <summary>Facing down = 1, sideways ~0.4, facing up 0.1 (blood stays in the body).</summary>
        public float Dependent => Mathf.Clamp(0.4f - 0.6f * Lowest.outward.y, 0.1f, 1f);
    }

    /// <summary>
    /// One stream of blood running down the clothing from an opening (blood-5, BleedVisuals.Stain): it extends a step at a
    /// time, steps over one bad spot (a pocket edge, a seam), and once it ends keeps getting fed (wider, darker, a soak
    /// spreading round the hole) while blood still flows. A run that ran off an edge (a jacket hem) drips from there.
    /// </summary>
    internal sealed class StainRun
    {
        public int Steps;
        public int Misses;
        public bool Done;
        public bool OffEdge;
        public int Fed;
        /// <summary>The last patch laid, relative to the site's bone (where a run off an edge drips from).</summary>
        public Vector3 LocalEnd;
        public bool HasEnd;
    }

    /// <summary>
    /// Everything that bleeds on one body: its sites, the blood it has lost, and a visual heart:
    /// pressure and heart rate while alive, a few weakening (agonal) beats after death, then nothing,
    /// so wounds drain by gravity only.
    /// </summary>
    internal sealed class Bleeder
    {
        public readonly Player Player;
        public readonly string Name;
        public readonly List<BleedSite> Sites = new List<BleedSite>();
        public readonly List<string> Events = new List<string>();

        public float BloodLost;
        public int Hits;
        /// <summary>
        /// Blood that ran out while the body lay (dead or prone), ml: ONE pool per body (0.10.2; per-wound
        /// pools of 0.10.0-0.10.1 were each small enough to hide under the body).
        /// </summary>
        public float PoolVolume;
        /// <summary>Pool radius shown now (m): follows PoolVolume as a ~1.5 mm film with a lag (blood spreads).</summary>
        public float PoolRadius;

        // blood-5 (blood-4 test: a dragged corpse left a stepping-stone line of big round pools, a new one at every pause, each
        // as big as the whole body's pool; a standing bleeder made no puddle at all): where the blood leaves the body now, and
        // since when it has been still. PoolVolume counts only what ran out while still at this place.
        public bool HasPlace;
        /// <summary>Where the pool of this place started (m, world); moving 0.5 m away starts a new place and volume.</summary>
        public Vector3 PlaceAnchor;
        public Vector3 PlaceLast;
        public float MovedAt;
        /// <summary>Still for 1.5 s (under 15 cm/s: a standing bot sways while it aims): blood collects under it (a pool); moving, it drips and smears instead.</summary>
        public bool Still => HasPlace && Time.time - MovedAt > 1.5f;
        /// <summary>Game time of death (EFT's), -1 while alive.</summary>
        public float DiedAt = -1f;
        public float PressureAtDeath = 1f;
        public float HeartRateAtDeath = 90f;
        /// <summary>0..1: blood in the airway (chest wounds): coughed while alive, dribbles when dead.</summary>
        public float MouthBlood;
        /// <summary>Airway blood from a lung wound is bright and frothy; from the face/skull darker (1.2.0).</summary>
        public BloodTone MouthTone;
        public bool DrainStarted;
        public bool DrainDone;
        /// <summary>The projectile that killed it (phase 2: head destruction decides from this).</summary>
        public HitEvent KillingHit;

        /// <summary>
        /// Blood collected inside the chest, belly or skull (ml) per body part: it runs out through
        /// that part's holes, slowly while alive, by gravity after death. Corpses' big pools come from here.
        /// </summary>
        public readonly Dictionary<EBodyPart, float> Cavity = new Dictionary<EBodyPart, float>();

        // Body parts on which EFT had a bleed effect: removing it later = the bot treated that part.
        public readonly HashSet<EBodyPart> EftBleeding = new HashSet<EBodyPart>();

        public Bleeder(Player player)
        {
            Player = player;
            Name = player.Profile?.Nickname ?? "?";
        }

        public bool IsDead => DiedAt >= 0f;
        public float SinceDeath => IsDead ? Time.time - DiedAt : 0f;
        public bool Agonal => IsDead && SinceDeath < Plugin.AgonalSeconds.Value;
        public float LossFraction => Mathf.Clamp01(BloodLost / Mathf.Max(1000f, Plugin.BloodVolume.Value));

        /// <summary>
        /// Visual blood pressure 0..1: holds until ~20 % loss, then falls; after death it decays over
        /// the agonal beats to nothing.
        /// </summary>
        public float Pressure
        {
            get
            {
                if (IsDead)
                {
                    float agonal = Mathf.Max(0.5f, Plugin.AgonalSeconds.Value);
                    return SinceDeath < agonal ? PressureAtDeath * Mathf.Exp(-3f * SinceDeath / agonal) : 0f;
                }
                return Mathf.Clamp01(1f - Mathf.Max(0f, LossFraction - 0.2f) / 0.4f);
            }
        }

        /// <summary>Beats per minute: up with blood loss and hits; after death slowing to ~30 and stopping.</summary>
        public float HeartRate
        {
            get
            {
                if (IsDead)
                {
                    float agonal = Mathf.Max(0.5f, Plugin.AgonalSeconds.Value);
                    return SinceDeath < agonal ? Mathf.Lerp(HeartRateAtDeath, 30f, SinceDeath / agonal) : 0f;
                }
                return Mathf.Min(175f, 85f + 15f * Mathf.Min(Hits, 3) + 90f * (1f - Pressure));
            }
        }

        public float TotalRate
        {
            get
            {
                float total = 0f;
                foreach (var s in Sites) total += s.Rate;
                return total;
            }
        }

        public string StateText =>
            !IsDead ? "alive" : Agonal ? "dying (agonal)" : DrainDone ? "dead, drained" : "dead, draining";

        public void Log(string message)
        {
            if (Plugin.LogBlood.Value)
            {
                Plugin.Log.LogInfo($"[Blood {System.DateTime.Now:HH:mm:ss.fff}] {Name}: {message} | lost {BloodLost:F0} ml, " +
                                   $"{TotalRate:F0} ml/min, BP {Pressure:P0}, HR {HeartRate:F0}");
            }
            Events.Add($"{Time.time:F0}s {message}");
            if (Events.Count > 10) Events.RemoveAt(0);
        }
    }
}
