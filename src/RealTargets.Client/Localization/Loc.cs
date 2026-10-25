using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using Newtonsoft.Json;

namespace RealTargets.Localization
{
    /// <summary>
    /// The mod's own UI text by key (1.2.0, roadmap "Localization"). English is built in (`locales/en.json`, embedded in
    /// the DLL); another language is `BepInEx/plugins/RealTargets/locales/&lt;code&gt;.json` with the same keys, code =
    /// EFT's language code (`ru`, `ge`, `fr`...). Language: the game's own (EFT's language setting) unless F12
    /// `General / Language` names one. A key missing from the language file shows in English (logged once).
    /// Slots are named (`{hits}`); plurals are `key.one` / `key.few` / `key.many` / `key.other` by the language's rules.
    /// F12 descriptions: `config.&lt;Section&gt;.&lt;Key&gt;` replaces a setting's description (never its name: renaming
    /// would reset everyone's cfg); English for those stays in Plugin.cs.
    /// </summary>
    internal static class Loc
    {
        internal const string Auto = "Auto";
        private const string ResourceName = "RealTargets.Localization.en.json";

        private static Dictionary<string, string> _english = new Dictionary<string, string>();
        private static Dictionary<string, string> _active;
        private static string _activeCode = "en";
        private static string _folder;
        private static ConfigEntry<string> _setting;
        private static string _startupCode;
        private static readonly HashSet<string> Reported = new HashSet<string>();

        /// <summary>The language in use ("en" when its file is missing or broken).</summary>
        internal static string Code => Refresh();

        /// <summary>Loads English and the language chosen at start (F12, else the game's saved language).</summary>
        internal static void Init(string pluginFolder, ConfigEntry<string> setting)
        {
            _folder = Path.Combine(pluginFolder ?? ".", "locales");
            _setting = setting;
            _english = LoadEnglish();
            _startupCode = SavedGameLanguage();
            Refresh();
        }

        internal static string T(string key) => Lookup(key) ?? key;

        /// <summary>The text with its named slots filled: F("x", ("hits", 3)) on "{hits} hits".</summary>
        internal static string F(string key, params (string name, object value)[] slots) => Fill(Lookup(key) ?? key, slots);

        /// <summary>
        /// A counted text: `key.one` / `.few` / `.many` / `.other` by <paramref name="n"/> and the language's plural rule,
        /// slot {n} = the count.
        /// </summary>
        internal static string P(string key, int n, params (string name, object value)[] slots)
        {
            string code = Refresh();
            string text = PluralText(_active, key, Category(code, n)) ?? PluralText(_english, key, Category("en", n));
            if (text == null)
            {
                Report(key, "missing in English");
                text = key;
            }
            var all = new (string, object)[slots.Length + 1];
            all[0] = ("n", n);
            Array.Copy(slots, 0, all, 1, slots.Length);
            return Fill(text, all);
        }

        /// <summary>First letter upper case (a place name starting a sentence).</summary>
        internal static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

        /// <summary>Replaces the F12 descriptions with the language's `config.Section.Key` texts (once, at start).</summary>
        internal static void LocalizeConfig(ConfigFile config)
        {
            if (_active == null) return;
            var field = typeof(ConfigEntryBase).GetField("<Description>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                Plugin.Log.LogWarning("[Loc] F12 descriptions stay English: BepInEx's description field not found");
                return;
            }
            int done = 0;
            foreach (var entry in ((IDictionary<ConfigDefinition, ConfigEntryBase>)config).Values)
            {
                if (!_active.TryGetValue(ConfigKey(entry.Definition), out var text) || string.IsNullOrEmpty(text)) continue;
                var old = entry.Description;
                field.SetValue(entry, new ConfigDescription(text, old.AcceptableValues, old.Tags));
                done++;
            }
            Plugin.Log.LogInfo($"[Loc] F12 descriptions in '{_activeCode}': {done} of {config.Count}");
        }

#if !PUBLIC
        /// <summary>
        /// Dev: writes `locales/en.json` next to the DLL = every key a translator needs (the built-in English plus the
        /// F12 descriptions from Plugin.cs), sorted; logs whether the repo's copy is behind.
        /// </summary>
        internal static void Export(ConfigFile config)
        {
            try
            {
                var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in _english)
                {
                    if (!pair.Key.StartsWith("config.", StringComparison.Ordinal)) all[pair.Key] = pair.Value;
                }
                foreach (var entry in ((IDictionary<ConfigDefinition, ConfigEntryBase>)config).Values)
                {
                    if (!ConfigLayout.DevOnly(entry.Definition)) all[ConfigKey(entry.Definition)] = entry.Description.Description;
                }
                int stale = all.Count(p => !_english.TryGetValue(p.Key, out var e) || e != p.Value) +
                            _english.Keys.Count(k => !all.ContainsKey(k));
                Directory.CreateDirectory(_folder);
                var ordered = new Dictionary<string, string>
                {
                    ["_comment"] = "RealTargets text, English. Translate the values into <code>.json (EFT's language code: ru, ge, fr...); " +
                                   "keep the keys, the {slots} and the <tags>. Keys ending .one/.few/.many/.other are plural forms.",
                };
                foreach (var pair in all) ordered[pair.Key] = pair.Value;
                File.WriteAllText(Path.Combine(_folder, "en.json"), JsonConvert.SerializeObject(ordered, Formatting.Indented) + "\n",
                    new UTF8Encoding(false));
                Plugin.Log.LogInfo($"[Loc] exported locales/en.json: {all.Count} keys; the built-in copy differs in {stale}" +
                                   (stale > 0 ? " (copy it to src/RealTargets.Client/locales/en.json)" : string.Empty));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Loc] en.json export failed: {ex.Message}");
            }
        }
#endif

        private static string ConfigKey(ConfigDefinition d) => $"config.{ConfigLayout.Plain(d.Section)}.{d.Key}";

        private static string Lookup(string key)
        {
            Refresh();
            if (_active != null && _active.TryGetValue(key, out var text) && text != null) return text;
            if (_english.TryGetValue(key, out text)) return text;
            Report(key, "missing in English");
            return null;
        }

        private static string PluralText(Dictionary<string, string> table, string key, string category)
        {
            if (table == null) return null;
            if (table.TryGetValue($"{key}.{category}", out var text)) return text;
            if (table.TryGetValue($"{key}.other", out text)) return text;
            if (table.TryGetValue($"{key}.many", out text)) return text;
            return null;
        }

        /// <summary>CLDR plural category (cardinal, whole numbers) for EFT's language codes.</summary>
        internal static string Category(string code, int n)
        {
            n = Math.Abs(n);
            int ten = n % 10, hundred = n % 100;
            // A regional code ("es-mx") plays by its language's rules.
            int dash = code?.IndexOf('-') ?? -1;
            switch (dash > 0 ? code.Substring(0, dash) : code)
            {
                case "ru":
                    if (ten == 1 && hundred != 11) return "one";
                    if (ten >= 2 && ten <= 4 && (hundred < 12 || hundred > 14)) return "few";
                    return "many";
                case "pl":
                    if (n == 1) return "one";
                    if (ten >= 2 && ten <= 4 && (hundred < 12 || hundred > 14)) return "few";
                    return "many";
                case "cz":
                case "sk":
                    return n == 1 ? "one" : n >= 2 && n <= 4 ? "few" : "other";
                case "fr":
                    return n <= 1 ? "one" : "other";
                case "ch":
                case "jp":
                case "kr":
                    return "other";
                default:
                    return n == 1 ? "one" : "other";
            }
        }

        private static string Fill(string text, (string name, object value)[] slots)
        {
            if (slots == null || slots.Length == 0 || text.IndexOf('{') < 0) return text;
            var sb = new StringBuilder(text);
            foreach (var (name, value) in slots) sb.Replace("{" + name + "}", value?.ToString() ?? string.Empty);
            return sb.ToString();
        }

        /// <summary>The wanted language (F12 or the game's), its file loaded when it changes.</summary>
        private static string Refresh()
        {
            string wanted = Wanted();
            if (wanted == _activeCode) return _activeCode;
            _activeCode = wanted;
            _active = wanted == "en" ? null : LoadLanguage(wanted);
            return _activeCode;
        }

        private static string Wanted()
        {
            string chosen = _setting?.Value;
            if (!string.IsNullOrWhiteSpace(chosen) && !chosen.Equals(Auto, StringComparison.OrdinalIgnoreCase)) return chosen.Trim().ToLowerInvariant();
            string game = GameLanguage();
            return !string.IsNullOrEmpty(game) ? game : _startupCode ?? "en";
        }

        private static string GameLanguage()
        {
            try
            {
                // Culture starts as "en" until EFT applies its settings (_currentApplicationCulture set then): before that,
                // the saved Game.ini language.
                var manager = EFT.LocalizationManager.Instance;
                return manager != null && !string.IsNullOrEmpty(manager._currentApplicationCulture) ? manager.Culture : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// EFT's language before the game loads its settings (F12 descriptions are bound at start): SPT keeps the client's
        /// Game.ini under user/sptSettings ("Language": "en").
        /// </summary>
        private static string SavedGameLanguage()
        {
            try
            {
                foreach (var dir in new[] { Path.Combine(BepInEx.Paths.GameRootPath, "SPT_Runtime", "user", "sptSettings"),
                                            Path.Combine(BepInEx.Paths.GameRootPath, "user", "sptSettings") })
                {
                    var file = Path.Combine(dir, "Game.ini");
                    if (!File.Exists(file)) continue;
                    var settings = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(file));
                    if (settings != null && settings.TryGetValue("Language", out var value) && value is string code && code.Length > 0) return code;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Loc] game language not read from Game.ini: {ex.Message}");
            }
            return null;
        }

        private static Dictionary<string, string> LoadEnglish()
        {
            try
            {
                using var stream = typeof(Loc).Assembly.GetManifestResourceStream(ResourceName);
                if (stream == null) throw new InvalidOperationException("resource " + ResourceName + " not found");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return Parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Loc] built-in English failed to load, the UI shows keys: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        private static Dictionary<string, string> LoadLanguage(string code)
        {
            var file = Path.Combine(_folder ?? "locales", code + ".json");
            if (!File.Exists(file))
            {
                Plugin.Log.LogInfo($"[Loc] language '{code}': no locales/{code}.json, English");
                return null;
            }
            try
            {
                var table = Parse(File.ReadAllText(file, Encoding.UTF8));
                int missing = _english.Keys.Count(k => !k.StartsWith("config.", StringComparison.Ordinal) && !table.ContainsKey(k) &&
                                                       !IsPluralForm(k, table));
                Plugin.Log.LogInfo($"[Loc] language '{code}': {table.Count} texts from locales/{code}.json" +
                                   (missing > 0 ? $", {missing} missing (shown in English)" : string.Empty));
                return table;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Loc] locales/{code}.json unreadable, English: {ex.Message}");
                return null;
            }
        }

        // An English plural form (key.one) counts as present when the language has any form of that key.
        private static bool IsPluralForm(string key, Dictionary<string, string> table)
        {
            int dot = key.LastIndexOf('.');
            if (dot < 0) return false;
            string tail = key.Substring(dot + 1);
            if (tail != "one" && tail != "few" && tail != "many" && tail != "other") return false;
            string stem = key.Substring(0, dot);
            return table.ContainsKey(stem + ".one") || table.ContainsKey(stem + ".few") || table.ContainsKey(stem + ".many") ||
                   table.ContainsKey(stem + ".other");
        }

        private static Dictionary<string, string> Parse(string json)
        {
            var raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in raw)
            {
                if (pair.Key.StartsWith("_", StringComparison.Ordinal) || pair.Value == null) continue;
                table[pair.Key] = pair.Value;
            }
            return table;
        }

        private static void Report(string key, string why)
        {
            if (Reported.Add(key)) Plugin.Log.LogWarning($"[Loc] '{key}' {why}");
        }
    }
}
