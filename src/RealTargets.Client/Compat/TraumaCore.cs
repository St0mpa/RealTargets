using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace RealTargets.Compat
{
    /// <summary>F12 [Death screen] Report: whose report shows on the raid results after a death.</summary>
    internal enum DeathReportMode
    {
        /// <summary>Ours: the AUTOPSY switch; TraumaCore's hit markers are hidden.</summary>
        RealTargets,
        /// <summary>Only TraumaCore's hit markers and labels; no AUTOPSY switch.</summary>
        TraumaCore,
        /// <summary>TraumaCore's markers over the model and our AUTOPSY switch.</summary>
        Both,
    }

    /// <summary>
    /// TraumaCore (com.hysocs.traumacore, decompiled 1.7.0): its death-screen report. `ShowDeathScreenPatch` (postfix on
    /// SessionResultExitStatus.Show, gated by `Feature Toggles > DeathScreenReport`) moves the level into the name
    /// (`_levelPanel` hidden, name "Nick (Lv. N)") at once; a coroutine waits for the model, then parents everything it draws
    /// to ONE container `TraumaCoreHitMarkers` under the preview RawImage (per-part labels, wound trajectories, the anatomy
    /// lines, a clear Image with left-drag model rotation) and hides EFT's `_bodyPartLabel`. Its other death-screen patches
    /// only add bleed lines to EFT's damage tooltips. We read its toggle through BepInEx and hide or show that container;
    /// we never change its config or call its code.
    /// </summary>
    internal static class TraumaCore
    {
        internal const string Guid = "com.hysocs.traumacore";
        internal const string MarkersName = "TraumaCoreHitMarkers";
        private const string Section = "Feature Toggles";
        private const string ReportKey = "DeathScreenReport";

        private static bool _resolved;
        private static bool _installed;
        private static ConfigEntry<bool> _report;

        internal static bool Installed
        {
            get
            {
                Resolve();
                return _installed;
            }
        }

        /// <summary>TraumaCore is there and its death-screen report is on (it drew on this screen).</summary>
        internal static bool ReportOn => Installed && (_report == null || _report.Value);

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info?.Instance == null) return;
            _installed = true;
            string version = info.Metadata.Version.ToString();
            if (!info.Instance.Config.TryGetEntry(Section, ReportKey, out _report))
            {
                Plugin.Log.LogWarning($"[Compat] TraumaCore {version}: no '{Section} > {ReportKey}' setting; assuming its death report is on");
            }
            Plugin.Log.LogInfo($"[Compat] TraumaCore {version}: its death report {(_report == null || _report.Value ? "on" : "off")}; " +
                               $"[Death screen] Report = {Plugin.DeathScreenReport.Value}");
        }
    }
}
