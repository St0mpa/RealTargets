using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;

namespace RealTargets.Compat
{
    /// <summary>F12 [HollywoodFX] Prefer its blood: which of our hit-moment blood gives way to HollywoodFX's.</summary>
    internal enum HfxBloodMode
    {
        /// <summary>Both mods' blood (the 2026-10-17 compatibility verdict).</summary>
        Off,
        /// <summary>Skip only what HollywoodFX/EFT already draw: entry-side mist (backspatter, head vent), wall spatter.</summary>
        Overlaps,
        /// <summary>Overlaps + our exit spray, exit mist and corpse squeeze: only HollywoodFX's blood at the hit.</summary>
        All,
    }

    /// <summary>
    /// HollywoodFX (com.janky.hollywoodfx, decompiled 3.0.0): detection and the two settings that decide whether its
    /// blood shows. Per hit it draws blood only at the ENTRY (puffs, spray or a 2 s squirt along the hit normal, toward
    /// the shooter; nothing at the exit), gated live by `05. Gore Emission (RESTART) > Enable Gore Effects`; EFT draws
    /// the splash on the wall behind a through-shot, which HollywoodFX re-textures when `07. Gore Decals > Blood
    /// Splatter on Environment` is on. We only read its config through BepInEx (no calls into its code), so a
    /// HollywoodFX update can only change setting names: then we assume its defaults (both on) and log it.
    /// Everything it lacks stays ours: wounds, bleeding over time, stains, pools, footprints, mouth blood, gore.
    /// </summary>
    internal static class HollywoodFx
    {
        internal const string Guid = "com.janky.hollywoodfx";
        private const string GoreSection = "05. Gore Emission (RESTART)";
        private const string GoreKey = "Enable Gore Effects";
        private const string DecalSection = "07. Gore Decals";
        private const string SplatterKey = "Blood Splatter on Environment";
        private const string BodyDecalKey = "Wound Decals on Bodies";

        private static GameWorld _world;
        private static bool _loaded;
        private static string _version;
        private static ConfigEntry<bool> _gore;
        private static ConfigEntry<bool> _splatter;
        private static ConfigEntry<bool> _bodyDecals;

        /// <summary>
        /// HollywoodFX paints EFT's texture wound decals on corpses too (`07. Gore Decals > Wound Decals on Bodies`;
        /// vanilla EFT paints them on living bots only). Then a corpse hit on armour or gear already has a hole drawn.
        /// </summary>
        internal static bool PaintsCorpseWounds => Refresh() && (_bodyDecals == null || _bodyDecals.Value);
        private static int _loggedState = -1;

        /// <summary>Our backspatter and head-vent mist at the entry give way to HollywoodFX's puffs and sprays.</summary>
        internal static bool SkipEntryMist => Active(HfxBloodMode.Overlaps) && GoreOn;

        /// <summary>Our wall spatter gives way to EFT's environment blood with HollywoodFX's texture.</summary>
        internal static bool SkipWallSpatter => Active(HfxBloodMode.Overlaps) && SplatterOn;

        /// <summary>Our exit spray, exit mist and corpse squeeze give way too (mode All).</summary>
        internal static bool SkipExitBlood => Active(HfxBloodMode.All) && GoreOn;

        private static bool GoreOn => _gore == null || _gore.Value;
        private static bool SplatterOn => _splatter == null || _splatter.Value;

        private static bool Active(HfxBloodMode least)
        {
            // Refresh first, also with Off: the [Compat] line says HollywoodFX was found and what it would take over.
            return Refresh() && Plugin.HfxPreferBlood.Value >= least;
        }

        /// <summary>
        /// Looks HollywoodFX up once per raid: it binds its config 5 s after load and wipes and rebinds it when its
        /// config version changes, so entries cached before a raid could be stale. Logs the state when it changes.
        /// </summary>
        private static bool Refresh()
        {
            var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (!ReferenceEquals(world, _world))
            {
                _world = world;
                Resolve();
            }

            if (_loaded)
            {
                int state = (int)Plugin.HfxPreferBlood.Value * 4 + (GoreOn ? 2 : 0) + (SplatterOn ? 1 : 0);
                if (state != _loggedState)
                {
                    _loggedState = state;
                    Plugin.Log.LogInfo($"[Compat] HollywoodFX {_version}: its gore {(GoreOn ? "on" : "off")}, splatter " +
                        $"{(SplatterOn ? "on" : "off")}; prefer its blood = {Plugin.HfxPreferBlood.Value}: ours skips " +
                        $"{Describe()}");
                }
            }
            return _loaded;
        }

        private static void Resolve()
        {
            _loaded = false;
            _gore = null;
            _splatter = null;
            _bodyDecals = null;
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info?.Instance == null) return;

            _loaded = true;
            _version = info.Metadata.Version.ToString();
            var config = info.Instance.Config;
            if (!config.TryGetEntry(GoreSection, GoreKey, out _gore))
            {
                Plugin.Log.LogWarning($"[Compat] HollywoodFX {_version}: no '{GoreSection} > {GoreKey}' setting; assuming its gore is on");
            }
            if (!config.TryGetEntry(DecalSection, SplatterKey, out _splatter))
            {
                Plugin.Log.LogWarning($"[Compat] HollywoodFX {_version}: no '{DecalSection} > {SplatterKey}' setting; assuming its splatter is on");
            }
            if (!config.TryGetEntry(DecalSection, BodyDecalKey, out _bodyDecals))
            {
                Plugin.Log.LogWarning($"[Compat] HollywoodFX {_version}: no '{DecalSection} > {BodyDecalKey}' setting; assuming it paints corpses");
            }
            _loggedState = -1;
        }

        private static string Describe()
        {
            var mode = Plugin.HfxPreferBlood.Value;
            if (mode == HfxBloodMode.Off) return "nothing";
            var parts = new System.Collections.Generic.List<string>(3);
            if (GoreOn) parts.Add("entry mist");
            if (SplatterOn) parts.Add("wall spatter");
            if (mode == HfxBloodMode.All && GoreOn) parts.Add("exit spray, exit mist, corpse squeeze");
            return parts.Count == 0 ? "nothing (its gore and splatter are off)" : string.Join(", ", parts);
        }
    }
}
