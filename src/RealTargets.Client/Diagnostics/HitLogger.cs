using RealTargets.Hits;

namespace RealTargets.Diagnostics
{
    /// <summary>Writes one BepInEx log line per HitEvent when [Debug] Log hits is on.</summary>
    internal static class HitLogger
    {
        internal static void Enable()
        {
            HitCollector.Published += OnHit;
        }

        private static void OnHit(HitEvent hit)
        {
            if (Plugin.LogHits.Value)
            {
                Plugin.Log.LogInfo(HitFormat.LogLine(hit));
            }
        }
    }
}
