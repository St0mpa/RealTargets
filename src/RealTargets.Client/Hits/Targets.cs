using EFT;

namespace RealTargets.Hits
{
    internal static class Targets
    {
        /// <summary>
        /// A bot as this machine sees it: EFT's IsAI (a BotOwner runs it here), or under Fika a client's copy of a bot
        /// (no BotOwner there, so IsAI is false). Every "only bots" gate asks this, never IsAI alone (ADR 0014).
        /// </summary>
        internal static bool IsBot(Player player) =>
            player != null && (player.IsAI || Compat.Fika.IsObservedAI(player));
    }
}
