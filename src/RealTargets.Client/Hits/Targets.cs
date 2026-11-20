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

        /// <summary>
        /// This machine simulates the body (its ragdoll and health): false for Fika's ObservedPlayer copies, whose owner
        /// (the host for bots, each client for its own player) sends their pose and death. Physics on a body ask this
        /// (ADR 0014: never change another machine's copy).
        /// </summary>
        internal static bool OwnsBody(Player player) =>
            player != null && !Compat.Fika.IsObserved(player);

        /// <summary>
        /// Alive as this machine knows it. HealthController, not ActiveHealthController: the latter is null on every Fika
        /// ObservedPlayer (its ObservedHealthController is a NetworkHealthController), which read as "dead" (1.2.0 bug).
        /// </summary>
        internal static bool IsAlive(Player player) =>
            player != null && player.HealthController != null && player.HealthController.IsAlive;
    }
}
