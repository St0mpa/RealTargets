using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;

namespace RealTargets
{
    /// <summary>
    /// F12 layout (1.2.0, Stompa: "clearly categorized with proper headings"): numbered sections in the order a player
    /// needs them, each a heading in the config manager, and the settings inside in the order Plugin.cs binds them (the
    /// manager sorts by its Order tag, then alphabetically). The sections were renamed from 1.1.0's, so <see cref="Migrate"/>
    /// carries every saved value over from its old section (and the one renamed key) and drops the old lines.
    /// </summary>
    internal static class ConfigLayout
    {
        internal const string General = "01. General";
        internal const string Bleeding = "02. Bleeding";
        internal const string BloodAndWounds = "03. Blood and wounds";
        internal const string BloodLook = "04. Blood look";
        internal const string Dismemberment = "05. Dismemberment";
        internal const string Impacts = "06. Impacts";
        internal const string Flinch = "07. Flinch";
        internal const string Autopsy = "08. Autopsy";
        internal const string DeathScreen = "09. Death screen";
        internal const string Compatibility = "10. Compatibility";
        internal const string HitModel = "11. Hit model (advanced)";
        internal const string Debug = "12. Debug";
        internal const string TestLab = "13. Test lab";

        /// <summary>1.1.0's section of each new one.</summary>
        private static readonly Dictionary<string, string> OldSection = new Dictionary<string, string>
        {
            [General] = "General", [Bleeding] = "Blood", [BloodAndWounds] = "Visuals", [BloodLook] = "Visuals",
            [Dismemberment] = "Gore", [Impacts] = "Impacts", [Flinch] = "Flinch", [Autopsy] = "Autopsy",
            [DeathScreen] = "Death screen", [Compatibility] = "HollywoodFX", [HitModel] = "Hit model", [Debug] = "Debug",
            [TestLab] = "Test lab",
        };

        /// <summary>Keys renamed with their section (new key -> 1.1.0 key).</summary>
        private static readonly Dictionary<string, string> OldKey = new Dictionary<string, string>
        {
            ["HollywoodFX: prefer its blood"] = "Prefer its blood",
        };

#if !PUBLIC
        /// <summary>
        /// Dev-build settings (the `#if !PUBLIC` binds in Plugin.cs): left out of the translators' template. A new dev
        /// setting goes here too.
        /// </summary>
        internal static bool DevOnly(ConfigDefinition d) => d.Section == TestLab || DevKeys.Contains(d.Section + "/" + d.Key);

        private static readonly HashSet<string> DevKeys = new HashSet<string>
        {
            General + "/Performance log", General + "/Benchmark key", General + "/Split benchmark key",
            Debug + "/Debug mode", Debug + "/Overlay toggle key", Debug + "/Tab key", Debug + "/Focus key",
            Debug + "/Select bot (opens Blood tab)", Debug + "/Overlay rows", Debug + "/Overlay X", Debug + "/Overlay Y",
            Debug + "/Ghost mode",
        };
#endif

        /// <summary>The section without its number ("General"): the translation keys use it.</summary>
        internal static string Plain(string section)
        {
            int dot = section.IndexOf(". ");
            return dot > 0 && dot <= 3 && section.Substring(0, dot).All(char.IsDigit) ? section.Substring(dot + 2) : section;
        }

        /// <summary>Saved 1.1.0 values into the renamed sections; then the old lines are dropped from the cfg.</summary>
        internal static void Migrate(ConfigFile config)
        {
            var orphans = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(config)
                as Dictionary<ConfigDefinition, string>;
            if (orphans == null)
            {
                Plugin.Log.LogWarning("[Config] old section values not carried over: BepInEx's orphaned entries not found");
                return;
            }
            if (orphans.Count == 0) return;
            int moved = 0;
            foreach (var entry in ((IDictionary<ConfigDefinition, ConfigEntryBase>)config).Values)
            {
                var def = entry.Definition;
                if (!OldSection.TryGetValue(def.Section, out var section)) continue;
                var old = new ConfigDefinition(section, OldKey.TryGetValue(def.Key, out var key) ? key : def.Key);
                if (!orphans.TryGetValue(old, out var value)) continue;
                entry.SetSerializedValue(value);
                orphans.Remove(old);
                moved++;
            }
            // 1.1.0 lines nothing took (a setting removed since): dropped too, so the cfg shows only live sections.
            var oldSections = new HashSet<string>(OldSection.Values);
            foreach (var def in orphans.Keys.Where(d => oldSections.Contains(d.Section)).ToList()) orphans.Remove(def);
            config.Save();
            Plugin.Log.LogInfo($"[Config] F12 sections renamed for 1.2.0: {moved} saved values carried over");
        }

        /// <summary>Order tags so the manager lists each section's settings in bind order, not alphabetically.</summary>
        internal static void Order(ConfigFile config)
        {
            var field = typeof(ConfigEntryBase).GetField("<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return;
            var entries = ((IDictionary<ConfigDefinition, ConfigEntryBase>)config).Values.ToList();
            // The manager sorts Order descending: the first bound gets the highest.
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var old = entry.Description;
                var tags = (old.Tags ?? new object[0]).Where(t => !(t is ConfigurationManagerAttributes)).ToList();
                tags.Add(new ConfigurationManagerAttributes { Order = entries.Count - i });
                field.SetValue(entry, new ConfigDescription(old.Description, old.AcceptableValues, tags.ToArray()));
            }
        }
    }

    /// <summary>Read by name by BepInEx's ConfigurationManager (F12): only the fields used here.</summary>
#pragma warning disable 0649
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
    }
#pragma warning restore 0649
}
