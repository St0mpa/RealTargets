using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Bootstrap;
using EFT;
using HarmonyLib;

namespace RealTargets.Compat
{
    /// <summary>
    /// Project Fika (com.fika.core, source read at v2.4.6; docs/modding/compatibility.md "Fika 2.4.6"): co-op where every
    /// machine re-simulates every bullet but only one applies its damage. A soft dependency: everything here is found by
    /// name, so without Fika (or after a Fika update renames something) the mod runs as in offline SPT and logs what it
    /// could not find. Players: FikaPlayer : LocalPlayer (you), FikaBot (a bot on the host), ObservedPlayer (everyone
    /// else's copy, bots included on a client: those have AIData without a BotOwner, so Player.IsAI is false and Fika's
    /// own FikaPlayer.IsObservedAI says "bot").
    /// </summary>
    internal static class Fika
    {
        internal const string Guid = "com.fika.core";
        private const string Players = "Fika.Core.Main.Players.";

        internal static bool Installed { get; private set; }
        internal static string Version { get; private set; }

        private static Type _fikaPlayer;
        private static Func<Player, bool> _observedAI;
        private static Func<bool> _headless;

        /// <summary>Called once from Plugin.Awake (Fika loads first: soft BepInDependency).</summary>
        internal static void Init()
        {
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out var info)) return;
            Installed = true;
            Version = info.Metadata.Version.ToString();
            try
            {
                _fikaPlayer = AccessTools.TypeByName(Players + "FikaPlayer");
                var field = _fikaPlayer != null ? AccessTools.Field(_fikaPlayer, "IsObservedAI") : null;
                if (field != null && field.FieldType == typeof(bool))
                {
                    var p = Expression.Parameter(typeof(Player), "p");
                    _observedAI = Expression.Lambda<Func<Player, bool>>(
                        Expression.Field(Expression.Convert(p, _fikaPlayer), field), p).Compile();
                }

                var utils = AccessTools.TypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
                var getter = utils != null ? AccessTools.PropertyGetter(utils, "IsHeadless") : null;
                if (getter != null && getter.IsStatic && getter.ReturnType == typeof(bool))
                {
                    _headless = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Compat] Fika {Version}: reflection failed: {ex.Message}");
            }

            Plugin.Log.LogInfo($"[Compat] Fika {Version}: bots by IsObservedAI {(_observedAI != null ? "ok" : "NOT FOUND")}, " +
                               $"headless check {(_headless != null ? "ok" : "NOT FOUND")}");
        }

        /// <summary>
        /// This game is Fika's headless host (no camera, no player of its own): it draws nothing, so we capture nothing.
        /// Read live: the headless plugin sets it at start-up.
        /// </summary>
        internal static bool IsHeadless => _headless != null && _headless();

        /// <summary>A Fika player that Fika says is a bot (an ObservedPlayer copy of a bot on a client).</summary>
        internal static bool IsObservedAI(Player player) =>
            _observedAI != null && player != null && _fikaPlayer.IsInstanceOfType(player) && _observedAI(player);

        /// <summary>
        /// Fika's ApplyShot overrides that apply a hit's damage and can be called without a body collider (rockets, flares,
        /// backblast): the record-or-fill patch goes on them. None calls base Player.ApplyShot.
        /// </summary>
        internal static IEnumerable<MethodBase> ApplyShotOverrides()
        {
            if (!Installed) yield break;
            foreach (var name in new[] { "FikaBot", "ObservedPlayer" })
            {
                var method = Find(Players + name, "ApplyShot");
                if (method != null) yield return method;
            }
        }

        /// <summary>
        /// Methods that run a hit through the armour only on the machine that owns it, called from a body collider's
        /// bridge: ObservedPlayer.ApplyClientShot (your shot on someone else's copy), ObservedPlayer.HandleSniperShot,
        /// FikaPlayer.SimulatedApplyShot (AI, sniper or landmine hits on you; FikaPlayer.ApplyShot hands it a copy).
        /// Their first argument is the DamageInfo after armour by the time they return.
        /// </summary>
        internal static IEnumerable<MethodBase> AfterArmourMethods()
        {
            if (!Installed) yield break;
            var methods = new[] { (Players + "ObservedPlayer", "ApplyClientShot"), (Players + "ObservedPlayer", "HandleSniperShot"),
                                  (Players + "FikaPlayer", "SimulatedApplyShot") };
            foreach (var (type, name) in methods)
            {
                var method = Find(type, name);
                if (method != null) yield return method;
            }
        }

        /// <summary>FikaClientGameWorld.ShotDelegate: a client's own override that doesn't call base.</summary>
        internal static MethodBase ClientShotDelegate() =>
            Installed ? Find("Fika.Core.Main.ClientClasses.FikaClientGameWorld", "ShotDelegate") : null;

        private static MethodBase Find(string typeName, string method)
        {
            var type = AccessTools.TypeByName(typeName);
            var found = type != null ? AccessTools.DeclaredMethod(type, method) : null;
            if (found == null)
            {
                Plugin.Log.LogWarning($"[Compat] Fika {Version}: {typeName}.{method} not found; hits through it are not captured");
            }
            return found;
        }
    }
}
