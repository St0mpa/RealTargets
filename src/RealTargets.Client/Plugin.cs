using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using RealTargets.Diagnostics;
using RealTargets.Features;
using RealTargets.Gore;
using RealTargets.Hits;
#if !PUBLIC
using RealTargets.Lab;
#endif
using RealTargets.Patches;
using UnityEngine;

namespace RealTargets
{
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    [BepInDependency(PluginInfo.SptCoreGuid, PluginInfo.SptMinVersion)]
#if !PUBLIC
    [BepInDependency(PluginInfo.BigBrainGuid)]
#endif
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

#if PUBLIC
        // Public build (Forge release): no test tools, no performance tools, chatty logs off by default.
        private const bool Dev = false;
        internal static bool PerfLogging => false;
#else
        private const bool Dev = true;
        internal static bool PerfLogging => PerfLog.Value;
#endif

        // [General]
        internal static ConfigEntry<bool> ModEnabled;
#if !PUBLIC
        internal static ConfigEntry<bool> PerfLog;
        internal static ConfigEntry<KeyboardShortcut> BenchmarkKey;
#endif

        // [Blood]
        internal static ConfigEntry<float> BloodVolume;
        internal static ConfigEntry<float> BleedMultiplier;
        internal static ConfigEntry<float> ArterialChance;
        internal static ConfigEntry<float> AgonalSeconds;
        internal static ConfigEntry<float> PostMortemHalfLife;

        // [Visuals]
        internal static ConfigEntry<bool> VisualsEnabled;
        internal static ConfigEntry<bool> VisualsDrips;
        internal static ConfigEntry<bool> VisualsSpurts;
        internal static ConfigEntry<bool> VisualsPools;
        internal static ConfigEntry<bool> VisualsFootprints;
        internal static ConfigEntry<bool> VisualsWounds;
        internal static ConfigEntry<bool> VisualsBodyStains;
        internal static ConfigEntry<bool> VisualsExitSpray;
        internal static ConfigEntry<bool> VisualsMist;
        internal static ConfigEntry<bool> VisualsSpatter;
        internal static ConfigEntry<bool> VisualsMouth;
        internal static ConfigEntry<float> VisualsDistance;
        internal static ConfigEntry<DecalShaderMode> DecalShader;

        // [Gore]
        internal static ConfigEntry<bool> GoreEnabled;
        internal static ConfigEntry<bool> GoreGibs;
        internal static ConfigEntry<float> ShotgunRange;
        internal static ConfigEntry<int> HeadPellets;
        internal static ConfigEntry<float> HeadLargeCalibreEnergy;
        internal static ConfigEntry<float> HeadAnyEnergy;
        internal static ConfigEntry<float> LimbEnergy;
        internal static ConfigEntry<float> BlastLimbRange;
        internal static ConfigEntry<float> SkullBurstEnergy;
        internal static ConfigEntry<float> HeadCumulativeEnergy;
        internal static ConfigEntry<int> HeadCumulativeHits;

        // [Impacts]
        internal static ConfigEntry<bool> ImpactsEnabled;
        internal static ConfigEntry<float> PushStrength;
        internal static ConfigEntry<bool> PushCorpses;
        internal static ConfigEntry<bool> BlastEnabled;

        // [Hit model]
        internal static ConfigEntry<float> VolleyWindow;
        internal static ConfigEntry<float> ReferenceEnergy;
        internal static ConfigEntry<float> ArmourTransfer;
        internal static ConfigEntry<float> ArmourStopFraction;
        internal static ConfigEntry<float> CavityScale;
        internal static ConfigEntry<float> HeavyEnergy;
        internal static ConfigEntry<float> MassiveEnergy;

        // [Flinch]
        internal static ConfigEntry<bool> FlinchEnabled;
        internal static ConfigEntry<float> FlinchStrength;
        internal static ConfigEntry<float> FlinchMin;
        internal static ConfigEntry<float> FlinchMax;
        internal static ConfigEntry<float> FlinchHoldSeconds;

        // [Debug]
        internal static ConfigEntry<bool> LogWounds;
        internal static ConfigEntry<bool> LogHits;
        internal static ConfigEntry<bool> LogBlood;
#if !PUBLIC
        internal static ConfigEntry<bool> DebugMode;
        internal static ConfigEntry<KeyboardShortcut> LabMenuKey;
        internal static ConfigEntry<KeyboardShortcut> OverlayToggleKey;
        internal static ConfigEntry<KeyboardShortcut> TabKey;
        internal static ConfigEntry<KeyboardShortcut> FocusKey;
        internal static ConfigEntry<KeyboardShortcut> SelectKey;
        internal static ConfigEntry<int> OverlayRows;
        internal static ConfigEntry<int> OverlayX;
        internal static ConfigEntry<int> OverlayY;
        internal static ConfigEntry<bool> GhostMode;

        // [Test lab]
        internal static ConfigEntry<KeyboardShortcut> LabSpawnKey;
        internal static ConfigEntry<KeyboardShortcut> LabClearKey;
        internal static ConfigEntry<float> LabDistance;
        internal static ConfigEntry<bool> TestKitAuto;
        internal static ConfigEntry<KeyboardShortcut> TestKitResendKey;
        internal static ConfigEntry<string> TestKitCommandoId;
#endif

        private bool _raidReset = true;

        private void Awake()
        {
            Log = Logger;
            BindConfig();

            ShotContext.Enable();
            new ApplyShotPatch().Enable();
            Blast.Enable();
#if !PUBLIC
            GhostModePatches.Enable();
            ModalInputPatches.Enable();
            DummyLayer.Register();
#endif

            // Order matters: features set results on a volley before the overlay formats it.
            Flinch.Enable();
            HitLogger.Enable();
            gameObject.AddComponent<BleedSystem>();
            gameObject.AddComponent<BloodParticles>();
            gameObject.AddComponent<BloodMist>();
            gameObject.AddComponent<GroundBlood>();
            gameObject.AddComponent<Footprints>();
            gameObject.AddComponent<ImpactBlood>();
            gameObject.AddComponent<BleedVisuals>();
            gameObject.AddComponent<WoundDecals>();
            gameObject.AddComponent<Dismemberment>();
            gameObject.AddComponent<Impacts>();
#if !PUBLIC
            gameObject.AddComponent<DebugOverlay>();
            gameObject.AddComponent<Perf>();
            gameObject.AddComponent<TestLab>();
            gameObject.AddComponent<TestKit>().Init(Path.GetDirectoryName(Info.Location) ?? ".");
#endif

            Log.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loaded");
        }

        private void LateUpdate()
        {
            // Warm-up runs on the loading screen: outside the timing scope (0.10.12 logged its 200 ms as a `Hits` frame).
            if (_raidReset && Singleton<GameWorld>.Instantiated) WarmUp();
            using var perf = RealTargets.Diagnostics.Perf.Measure(RealTargets.Diagnostics.PerfArea.Hits);
            if (Singleton<GameWorld>.Instantiated)
            {
                _raidReset = false;
                HitCollector.Flush();
                return;
            }

            if (!_raidReset)
            {
                _raidReset = true;
                HitCollector.Reset();
                GetComponent<BleedSystem>()?.ResetRaid();
                GetComponent<BleedVisuals>()?.ResetRaid();
                GetComponent<WoundDecals>()?.ResetRaid();
                GetComponent<Dismemberment>()?.ResetRaid();
                GetComponent<BloodParticles>()?.ResetRaid();
                GetComponent<BloodMist>()?.ResetRaid();
                GetComponent<GroundBlood>()?.ResetRaid();
                GetComponent<Footprints>()?.ResetRaid();
                GetComponent<Impacts>()?.ResetRaid();
#if !PUBLIC
                GetComponent<TestLab>()?.ResetRaid();
#endif
                SceneLight.ResetRaid();
            }
        }

        /// <summary>
        /// First frame of a raid (still the loading screen): builds every texture, material and particle
        /// system now. 0.10.9 benchmark: they were built on the first hit, one 48 ms frame in the first fight.
        /// Each step is idempotent (made once per game start; the light list once per raid).
        /// </summary>
        private void WarmUp()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                GetComponent<BloodParticles>()?.Warm();
                GetComponent<BloodMist>()?.Warm();
                GetComponent<GroundBlood>()?.Warm();
                GetComponent<WoundDecals>()?.Warm();
                GoreMeshes.Warm();
                SceneLight.Warm();
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"Warm-up: {ex.Message}");
            }
            Log.LogInfo($"Warm-up at raid load: {watch.Elapsed.TotalMilliseconds:F0} ms");
        }

        private void BindConfig()
        {
            ModEnabled = Config.Bind("General", "Enabled", true,
                "Master switch: off = no new blood, wounds, gore, corpse motion or footprints (what is already in the raid stays). For FPS comparisons and quick disabling.");
#if !PUBLIC
            PerfLog = Config.Bind("General", "Performance log", true,
                "Every 10 s in a raid, one [Perf] log line: FPS (average, 1 % low), RealTargets' own CPU time per frame and how many effects exist.");
            BenchmarkKey = Config.Bind("General", "Benchmark key", new KeyboardShortcut(KeyCode.F7),
                "In a raid: measure the next 60 s and log one [Perf] BENCHMARK summary (for comparing Enabled on and off).");
#endif

            const string blood = "Blood";
            BloodVolume = Config.Bind(blood, "Blood volume (ml)", 5000f,
                new ConfigDescription("Blood in a body. At most ~65 % of it can run out (pools stop growing there).",
                    new AcceptableValueRange<float>(3000f, 7000f)));
            BleedMultiplier = Config.Bind(blood, "Bleed rate multiplier", 1f,
                new ConfigDescription("Scales how fast every wound bleeds (2 = twice as fast). Visual only.",
                    new AcceptableValueRange<float>(0.1f, 5f)));
            ArterialChance = Config.Bind(blood, "Arterial chance", 1f,
                new ConfigDescription("Scales the chance that a hit where a big artery runs (neck, groin, thigh, upper arm; smaller " +
                    "ones in forearm, calf, face) cuts it and spurts. Base: neck 55 %, thigh 22 %, groin 20 %, upper arm 15 %, " +
                    "x energy (a pistol round about half as likely as a rifle round).", new AcceptableValueRange<float>(0f, 3f)));
            AgonalSeconds = Config.Bind(blood, "Agonal pumping (s)", 8f,
                new ConfigDescription("After death the heart gives a few last, weakening beats: spurts and flow fade out over this " +
                    "long before only gravity drains the wounds. 0 = stops at once.", new AcceptableValueRange<float>(0f, 30f)));
            PostMortemHalfLife = Config.Bind(blood, "Post-mortem drain half-life (s)", 90f,
                new ConfigDescription("After death, open wounds keep draining by gravity (more if facing down), tapering with this " +
                    "half-life. 0 = bleeding stops at death.", new AcceptableValueRange<float>(0f, 600f)));

            const string visuals = "Visuals";
            VisualsEnabled = Config.Bind(visuals, "Enabled", true, "All our blood and wound visuals.");
            VisualsDrips = Config.Bind(visuals, "Drips", true, "Blood droplets welling and pouring from every open wound.");
            VisualsSpurts = Config.Bind(visuals, "Arterial spurts", true, "Pulsing droplet jets from cut arteries at heart rate, weakening after death.");
            VisualsWounds = Config.Bind(visuals, "Entry and exit wounds", true,
                "Visible bullet wounds on the clothing/skin surface where each round went in and out (exit larger, ragged).");
            VisualsBodyStains = Config.Bind(visuals, "Blood running down the body", true,
                "Bleeding wounds stain the clothing below them, further down as more blood runs out.");
            VisualsExitSpray = Config.Bind(visuals, "Exit spray", true,
                "Droplets thrown out of the exit wound along the shot; they land as splats on whatever is behind.");
            VisualsMist = Config.Bind(visuals, "Blood mist", true, "A short-lived cloud of fine blood at exit wounds (bigger with energy).");
            VisualsSpatter = Config.Bind(visuals, "Wall spatter", true,
                "A splash on the wall or floor behind a through-shot (within ~2.5 m), placed at once.");
            VisualsMouth = Config.Bind(visuals, "Mouth blood", true, "Chest wounds: coughing blood while standing, dribbling from the mouth when lying or dead.");
            VisualsFootprints = Config.Bind(visuals, "Bloody footprints", true,
                "Anyone who walks through a wet pool (you or a bot) leaves boot prints after it, fainter with every step.");
            VisualsPools = Config.Bind(visuals, "Pools", true,
                "Blood pools spread under the wounds of dead (or prone) bodies, sized by the blood that ran out there, drying darker.");
            DecalShader = Config.Bind(visuals, "Decal shader", DecalShaderMode.Lit,
                "How wounds and ground blood are drawn. Lit: darken pass + a colour pass with a real lit shader (the map's lights " +
                "and exposure decide the brightness, wet sheen). Hybrid (0.10.2): the colour pass is unlit, scaled by our own light " +
                "estimate (glowed in dark rooms). Multiply: darken/tint only (invisible on black cloth). AlphaBlended: unlit colour " +
                "pass only. Switches live (stumps and gibs follow Lit only at the start of a raid).");
            DecalShader.SettingChanged += (_, __) => GroundBlood.ApplyDecalShader();
            VisualsDistance = Config.Bind(visuals, "Max distance (m)", 80f,
                new ConfigDescription("No blood visuals beyond this distance from the camera.", new AcceptableValueRange<float>(10f, 300f)));

            const string gore = "Gore";
            GoreEnabled = Config.Bind(gore, "Dismemberment", true,
                "Heads and limbs come apart on the killing shot or on a corpse (never on a living bot), with realistic thresholds below.");
            GoreGibs = Config.Bind(gore, "Gibs", true, "Flesh and skull pieces fly when a head or limb comes apart.");
            ShotgunRange = Config.Bind(gore, "Shotgun range (m)", 5f,
                new ConfigDescription("Buckshot destroys a head only within this distance (a tight pattern).", new AcceptableValueRange<float>(1f, 20f)));
            HeadPellets = Config.Bind(gore, "Head pellets", 5,
                new ConfigDescription("Pellets of one shell that must enter the head to destroy it.", new AcceptableValueRange<int>(1, 20)));
            HeadLargeCalibreEnergy = Config.Bind(gore, "Large-calibre energy (J)", 1200f,
                new ConfigDescription("A single projectile of 12 mm or more (slug, 23 mm, 12.7 mm rifle/MG; not .50 AE pistol) with this energy in the head destroys it.",
                    new AcceptableValueRange<float>(300f, 10000f)));
            HeadAnyEnergy = Config.Bind(gore, "Any-calibre energy (J)", 5000f,
                new ConfigDescription("Any projectile with this energy in the head destroys it (.338 LM ~6500 J; 7.62x51 ~2600 J does not).",
                    new AcceptableValueRange<float>(1000f, 20000f)));
            LimbEnergy = Config.Bind(gore, "Heavy-calibre limb energy (J)", 1200f,
                new ConfigDescription("A 12.7 mm or larger rifle/MG round with this energy severs the limb it hits (shotguns, rifles and the .50 AE pistol never do). 12.7x55 PS12 ~1490 J, PS12B ~2900 J.",
                    new AcceptableValueRange<float>(500f, 20000f)));
            BlastLimbRange = Config.Bind(gore, "Blast range scale", 1f,
                new ConfigDescription("Scales how close a grenade must be to take a head (~0.3 m) or limb (~0.3-0.5 m), for an RGD-5.",
                    new AcceptableValueRange<float>(0f, 3f)));
            SkullBurstEnergy = Config.Bind(gore, "Skull burst energy (J)", 2300f,
                new ConfigDescription("A single bullet with this energy in the head (below the destruction rules) bursts the skull open at the exit " +
                    "(head stays on): 7.62x51 ~3300 J, 7.62x54R ~3700, .50 AE Hawk JSP ~2850 and 7.62x39 FMJ/SP ~2450 do; .366 FMJ ~2200 and 7.62x39 PS/BP (~2000-2100) " +
                    "only get the big exit. Kills and corpses only.", new AcceptableValueRange<float>(1000f, 10000f)));
            HeadCumulativeEnergy = Config.Bind(gore, "Cumulative head energy (J)", 6000f,
                new ConfigDescription("A dead body's head comes apart once the rounds that entered it add up to this much energy (and at least " +
                    "`Cumulative head hits`): 9x19 ~13 hits, 5.45 ~5, 5.56 ~4, 7.62x39 3. A shotgun shell counts as one hit.",
                    new AcceptableValueRange<float>(1000f, 30000f)));
            HeadCumulativeHits = Config.Bind(gore, "Cumulative head hits", 3,
                new ConfigDescription("Least number of rounds in the head before it can come apart from repeated hits.", new AcceptableValueRange<int>(1, 30)));

            const string impacts = "Impacts";
            ImpactsEnabled = Config.Bind(impacts, "Enabled", true, "Bullets push dead bodies (momentum at the hit bone).");
            PushStrength = Config.Bind(impacts, "Push strength", 2f,
                new ConfigDescription("Multiplier on the bullet momentum given to a corpse (1 = physical momentum transfer; " +
                    "a round that exits passes on ~35 % of it).", new AcceptableValueRange<float>(0f, 10f)));
            PushCorpses = Config.Bind(impacts, "Push corpses", true,
                "Shots move corpses (frozen corpses are woken for a few seconds, then settle again).");
            BlastEnabled = Config.Bind(impacts, "Blast injuries", true,
                "Grenade blasts throw corpses within 6 m away from the charge (by overpressure). Damage to the living is EFT's.");

            const string model = "Hit model";
            VolleyWindow = Config.Bind(model, "Volley window", 0.05f,
                new ConfigDescription("Seconds to wait for the rest of a cartridge's hits before reacting, e.g. shotgun pellets " +
                    "that land a frame apart at range, or a bullet that crosses a plate and the body in different " +
                    "frames, so each cartridge reacts once.",
                    new AcceptableValueRange<float>(0f, 0.25f)));
            ReferenceEnergy = Config.Bind(model, "Reference energy (J)", 1700f,
                new ConfigDescription("Effective impact energy that gives flinch strength x1 (about a 5.56 rifle round).",
                    new AcceptableValueRange<float>(100f, 5000f)));
            ArmourTransfer = Config.Bind(model, "Armour transfer", 0.5f,
                new ConfigDescription("Share of impact energy the body still feels when armour stops the round (0 = none, 1 = all).",
                    new AcceptableValueRange<float>(0f, 1f)));
            ArmourStopFraction = Config.Bind(model, "Armour stop threshold", 0.35f,
                new ConfigDescription("If less than this share of a hit's damage gets past armour/helmet, the round counts as stopped " +
                    "(no hole, no bleeding) instead of entering the body.", new AcceptableValueRange<float>(0.05f, 0.9f)));
            CavityScale = Config.Bind(model, "Cavity scale", 1f,
                new ConfigDescription("Size of the temporary cavity around the bullet path: how big exit wounds get with energy.",
                    new AcceptableValueRange<float>(0f, 3f)));
            HeavyEnergy = Config.Bind(model, "Heavy threshold (J)", 600f,
                new ConfigDescription("Effective energy from which a hit counts as HEAVY.", new AcceptableValueRange<float>(50f, 5000f)));
            MassiveEnergy = Config.Bind(model, "Massive threshold (J)", 1800f,
                new ConfigDescription("Effective energy from which a hit counts as MASSIVE.", new AcceptableValueRange<float>(100f, 10000f)));

            const string flinch = "Flinch";
            FlinchEnabled = Config.Bind(flinch, "Enabled", false,
                "Living bots flinch per limb via EFT's IK hit reaction (off by default: looks like a shake on top of the animation).");
            FlinchStrength = Config.Bind(flinch, "Strength", 1.5f,
                new ConfigDescription("Global multiplier on the flinch force (EFT's own reaction = 1).",
                    new AcceptableValueRange<float>(0f, 5f)));
            FlinchMin = Config.Bind(flinch, "Minimum", 0.3f,
                new ConfigDescription("Smallest flinch force for any hit that gets through.", new AcceptableValueRange<float>(0f, 3f)));
            FlinchMax = Config.Bind(flinch, "Maximum", 3f,
                new ConfigDescription("Largest flinch force (caps .50 cal / shotgun point blank).", new AcceptableValueRange<float>(0.5f, 8f)));
            FlinchHoldSeconds = Config.Bind(flinch, "Full IK hold (s)", 1f,
                new ConfigDescription("How long the bot's full-body IK stays in full-quality mode after a hit (EFT uses 0.6).",
                    new AcceptableValueRange<float>(0.3f, 3f)));

            const string debug = "Debug";
            LogWounds = Config.Bind(debug, "Log wound placement", Dev,
                "One log line per wound patch: where it was placed (skinned / flat) or why not. For tests and bug reports.");
            LogHits = Config.Bind(debug, "Log hits", Dev,
                "Write one line to the BepInEx log per projectile that hits a player or bot.");
            LogBlood = Config.Bind(debug, "Log blood", Dev,
                "Write each bleed event (new site, death, treatment, drain end), gore event and corpse push to the BepInEx log.");
#if !PUBLIC
            LabMenuKey = Config.Bind("Test lab", "Spawn menu key", new KeyboardShortcut(KeyCode.F3),
                "Opens the test lab spawn menu: spawn ONE Scav or PMC of a chosen armour tier in front of you.");
            DebugMode = Config.Bind(debug, "Debug mode", true,
                "Master switch for the in-raid debug tools: hit list and Blood tab.");
            OverlayToggleKey = Config.Bind(debug, "Overlay toggle key", new KeyboardShortcut(KeyCode.F9),
                "Show/hide the debug overlay while debug mode is on.");
            TabKey = Config.Bind(debug, "Tab key", new KeyboardShortcut(KeyCode.F10),
                "Switch the overlay tab: Hits, Blood.");
            FocusKey = Config.Bind(debug, "Focus key", new KeyboardShortcut(KeyCode.F11),
                "Focus the bot under the crosshair for the Blood tab (otherwise the last bot hit).");
            SelectKey = Config.Bind(debug, "Select bot (opens Blood tab)", new KeyboardShortcut(KeyCode.Mouse2),
                "Middle click: focus the bot under the crosshair and show its Blood tab. Only read, not taken from the game.");
            OverlayRows = Config.Bind(debug, "Overlay rows", 8,
                new ConfigDescription("How many recent hits the Hits tab lists.", new AcceptableValueRange<int>(1, 25)));
            OverlayX = Config.Bind(debug, "Overlay X", 20,
                new ConfigDescription("Overlay left edge, pixels.", new AcceptableValueRange<int>(0, 3840)));
            OverlayY = Config.Bind(debug, "Overlay Y", 120,
                new ConfigDescription("Overlay top edge, pixels.", new AcceptableValueRange<int>(0, 2160)));
            GhostMode = Config.Bind(debug, "Ghost mode", false,
                "Bots never register you as an enemy (testing only). Switch on before bots notice you; " +
                "bots that already know about you keep fighting.");

            const string lab = "Test lab";
            LabSpawnKey = Config.Bind(lab, "Spawn lineup key", new KeyboardShortcut(KeyCode.F6),
                "Spawn 10 standing dummies in front of you: front row 5 scavs, back row 5 PMCs, armour tiers T0-T4 left to right. " +
                "Best with raid setting AI amount: None. Needs Debug mode on.");
            LabClearKey = Config.Bind(lab, "Clear lineup key", new KeyboardShortcut(KeyCode.F5),
                "Despawn the living lab dummies (corpses stay).");
            LabDistance = Config.Bind(lab, "Distance (m)", 8f,
                new ConfigDescription("Distance from you to the front row.", new AcceptableValueRange<float>(3f, 40f)));
            TestKitAuto = Config.Bind(lab, "Mail test kit automatically", true,
                "When a new testkit.json ships with a build, mail its weapons, ammo and gear to you (Messenger > Commando) " +
                "once you're logged in and in the menus. Uses SPT's own 'spt give' command.");
            TestKitResendKey = Config.Bind(lab, "Resend test kit key", new KeyboardShortcut(KeyCode.F4),
                "In the menus: mail the whole current test kit again, including weapons and gear.");
            TestKitCommandoId = Config.Bind(lab, "Commando contact id", "6723fd51c5924c57ce0ca01e",
                "SPT's Commando chat-bot id (SPT_Data/configs/core.json, chatbotFeatures.ids.commando).");
#endif
        }
    }
}
