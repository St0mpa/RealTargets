namespace RealTargets
{
    internal static class PluginInfo
    {
        public const string Guid = "com.stompa.realtargets";
        // Forge rule: `Username-ModName` (Stompa's Forge user is `stompa`, 2026-10-17). Also the BepInEx log source.
        public const string Name = "stompa-RealTargets";
        // Keep in sync with <Version> in RealTargets.Client.csproj.
        public const string Version = "1.1.0";

        public const string SptCoreGuid = "com.SPT.core";
        public const string SptMinVersion = "4.1.3";
#if !PUBLIC
        public const string BigBrainGuid = "xyz.drakia.bigbrain";
#endif
    }
}
