using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using EFT.Game.Spawning;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace RealTargets.Lab
{
    /// <summary>
    /// Test lab: spawns a fixed lineup of 10 dummies in front of the player (front row 5 scavs,
    /// back row 5 PMCs, armour tiers T0..T4 left to right) that stand still as pure targets
    /// (DummyLayer). Use with AI amount None so nothing else is on the map. Spawn recipe follows
    /// Ragdoll Kinetics' preview (Apache-2.0): SPT /client/game/bot/generate -> Profile -> gear
    /// edited on the profile -> load prefabs -> BotCreationData -> BotSpawner.method_10.
    /// Spawn menu (0.9.3, Stompa: "spawn only the one target I want to test"): F3 opens a small menu
    /// to spawn ONE Scav or PMC of a chosen tier at the lab distance in front of the player.
    /// </summary>
    internal sealed class TestLab : MonoBehaviour
    {
        internal sealed class Dummy
        {
            public Player Player;
            public BotOwner Bot;
            public int Tier;
            public bool IsPmc;
            public Vector3 Spot;
            public Vector3 LookAt;
            public string Label => $"{(IsPmc ? "PMC" : "Scav")} {LabGear.Tiers[Tier].Label}";
        }

        private static readonly Dictionary<int, Dummy> Dummies = new Dictionary<int, Dummy>();
        private bool _busy;
        private bool _menu;
        private bool _replace = true;
        private float _menuOpenedAt;
        private Rect _menuRect;
        private GUIStyle _menuTitle, _menuText, _menuButton;

        internal static bool MenuOpen { get; private set; }

        internal static string Status { get; private set; } = "";
        internal static IEnumerable<Dummy> All => Dummies.Values;

        internal static Dummy Find(Player player)
        {
            if (player == null) return null;
            Dummies.TryGetValue(player.Id, out var dummy);
            return dummy;
        }

        internal static bool IsDummy(BotOwner bot) => bot?.GetPlayer != null && Dummies.ContainsKey(bot.GetPlayer.Id);

        internal void ResetRaid()
        {
            Dummies.Clear();
            Status = "";
            _busy = false;
            SetMenu(false);
        }

        private void Update()
        {
            if (!Plugin.DebugMode.Value || !Singleton<GameWorld>.Instantiated)
            {
                return;
            }

            if (Plugin.LabSpawnKey.Value.IsDown() && !_busy)
            {
                _ = SpawnLineup();
            }

            if (Plugin.LabClearKey.Value.IsDown() && !_busy)
            {
                Clear();
            }

            // Opens on the bare key press (KeyboardShortcut.IsDown refuses while any other key is held,
            // e.g. W while walking: "F3 doesn't always open", 0.9.3). Closing is handled in OnGUI.
            if (!_menu && Input.GetKeyDown(Plugin.LabMenuKey.Value.MainKey))
            {
                SetMenu(true);
            }
            if (_menu)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void LateUpdate()
        {
            if (!_menu) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void SetMenu(bool open)
        {
            if (_menu == open) return;
            _menu = open;
            MenuOpen = open;
            if (open) _menuOpenedAt = Time.unscaledTime;
            if (!open)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void OnGUI()
        {
            if (!_menu) return;
            if (!Singleton<GameWorld>.Instantiated)
            {
                SetMenu(false);
                return;
            }

            // Close keys come from IMGUI events (always delivered); ignore the press that opened it.
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && Time.unscaledTime - _menuOpenedAt > 0.25f &&
                (e.keyCode == Plugin.LabMenuKey.Value.MainKey || e.keyCode == KeyCode.Escape))
            {
                e.Use();
                SetMenu(false);
                return;
            }

            if (_menuTitle == null)
            {
                _menuTitle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, richText = true };
                _menuText = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = true };
                _menuButton = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
            }
            if (_menuRect.width <= 0f)
            {
                _menuRect = new Rect(Screen.width * 0.5f - 260f, Screen.height * 0.5f - 250f, 520f, 500f);
            }

            GUI.depth = -900;
            GUI.color = new Color(1f, 1f, 1f, 0.97f);
            GUI.Box(_menuRect, GUIContent.none);
            GUI.Box(_menuRect, GUIContent.none);
            GUI.color = Color.white;

            float x = _menuRect.x + 16f, y = _menuRect.y + 12f, w = _menuRect.width - 32f;
            GUI.Label(new Rect(x, y, w - 120f, 24f), "TEST LAB  ·  spawn one target", _menuTitle);
            if (GUI.Button(new Rect(_menuRect.xMax - 16f - 110f, y, 110f, 26f), "Close  X"))
            {
                SetMenu(false);
                return;
            }
            y += 28f;
            GUI.Label(new Rect(x, y, w, 36f), $"<color=#aab4be>Spawns {Plugin.LabDistance.Value:F0} m in front of you, facing you, standing still.</color>", _menuText);
            y += 30f;

            float col = (w - 16f) / 2f;
            GUI.Label(new Rect(x, y, col, 20f), "<b>SCAV</b>", _menuText);
            GUI.Label(new Rect(x + col + 16f, y, col, 20f), "<b>PMC</b>", _menuText);
            y += 22f;
            for (int tier = 0; tier < LabGear.Tiers.Length; tier++)
            {
                string label = LabGear.Tiers[tier].Label;
                GUI.enabled = !_busy;
                if (GUI.Button(new Rect(x, y, col, 30f), $"  Scav {label}", _menuButton)) _ = SpawnSingle(false, tier);
                if (GUI.Button(new Rect(x + col + 16f, y, col, 30f), $"  PMC {label}", _menuButton)) _ = SpawnSingle(true, tier);
                GUI.enabled = true;
                y += 34f;
            }

            y += 6f;
            _replace = GUI.Toggle(new Rect(x, y, w, 22f), _replace, "  Remove the previous living target first");
            y += 26f;
            GUI.Label(new Rect(x, y, 140f, 24f), $"Distance {Plugin.LabDistance.Value:F0} m", _menuText);
            if (GUI.Button(new Rect(x + 140f, y, 36f, 24f), "-")) Plugin.LabDistance.Value = Mathf.Max(2f, Plugin.LabDistance.Value - 1f);
            if (GUI.Button(new Rect(x + 180f, y, 36f, 24f), "+")) Plugin.LabDistance.Value = Mathf.Min(40f, Plugin.LabDistance.Value + 1f);
            y += 32f;

            GUI.enabled = !_busy;
            if (GUI.Button(new Rect(x, y, col, 28f), $"  Full lineup ({Plugin.LabSpawnKey.Value})", _menuButton)) _ = SpawnLineup();
            GUI.enabled = true;
            if (GUI.Button(new Rect(x + col + 16f, y, col, 28f), $"  Clear living ({Plugin.LabClearKey.Value})", _menuButton)) Clear();
            y += 34f;
            // 0.10.2: proves ground blood and mist render, independent of bleeding (look at a wall, close the menu).
            if (GUI.Button(new Rect(x, y, w, 28f), "  Blood render test: splash where you look, pool 1.5 m ahead, gore to its right, mist", _menuButton))
            {
                var cam = Camera.main;
                var ground = Gore.GroundBlood.Instance;
                Status = cam != null && ground != null ? "Blood test: " + ground.RenderTest(cam.transform) : "Blood test: no camera";
            }
            y += 34f;
            GUI.Label(new Rect(x, y, w, 40f), $"<color=#aab4be>{(Status.Length > 0 ? Status : "")}\n{Plugin.LabMenuKey.Value.MainKey} or Esc closes. The game gets no input while this is open.</color>", _menuText);
        }

        private async Task SpawnSingle(bool pmc, int tier)
        {
            if (_busy) return;
            if (_replace) Clear();
            _busy = true;
            try
            {
                var me = Singleton<GameWorld>.Instance.MainPlayer;
                var controller = Singleton<IBotGame>.Instance?.BotsController;
                var spawner = controller?.GetSpawner();
                if (me == null || spawner == null)
                {
                    Status = "Lab: no bot spawner in this raid.";
                    Plugin.Log.LogWarning(Status);
                    return;
                }

                var forward = Vector3.ProjectOnPlane(me.Transform.forward, Vector3.up).normalized;
                var right = Vector3.Cross(Vector3.up, forward);
                var origin = me.Transform.position;
                var spot = Ground(origin + forward * Plugin.LabDistance.Value);
                // Someone still standing there: step aside.
                for (int i = 0; i < 6 && Dummies.Values.Any(d => d.Player != null && (d.Player.Position - spot).sqrMagnitude < 1.2f); i++)
                {
                    spot = Ground(spot + right * 1.6f);
                }

                string label = $"{(pmc ? "PMC" : "Scav")} {LabGear.Tiers[tier].Label}";
                Status = $"Lab: spawning {label}...";
                var role = pmc ? (tier % 2 == 0 ? WildSpawnType.pmcUSEC : WildSpawnType.pmcBEAR) : WildSpawnType.assault;
                var bot = await SpawnOne(spawner, controller, role, tier, spot);
                var player = bot?.GetPlayer;
                if (player == null)
                {
                    Status = $"Lab: {label} failed to spawn.";
                    return;
                }
                player.Teleport(spot);
                Dummies[player.Id] = new Dummy
                {
                    Player = player,
                    Bot = bot,
                    Tier = tier,
                    IsPmc = pmc,
                    Spot = spot,
                    LookAt = origin + Vector3.up * 1.5f,
                };
                Status = $"Lab: {label} spawned.";
                Plugin.Log.LogInfo(Status);
            }
            catch (Exception ex)
            {
                Status = $"Lab: spawn failed: {ex.Message}";
                Plugin.Log.LogError(Status);
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task SpawnLineup()
        {
            _busy = true;
            try
            {
                var me = Singleton<GameWorld>.Instance.MainPlayer;
                var controller = Singleton<IBotGame>.Instance?.BotsController;
                var spawner = controller?.GetSpawner();
                if (me == null || spawner == null)
                {
                    Status = "Lab: no bot spawner in this raid.";
                    Plugin.Log.LogWarning(Status);
                    return;
                }

                var forward = Vector3.ProjectOnPlane(me.Transform.forward, Vector3.up).normalized;
                var right = Vector3.Cross(Vector3.up, forward);
                var origin = me.Transform.position;
                float distance = Plugin.LabDistance.Value;

                int done = 0, failed = 0;
                for (int row = 0; row < 2; row++)
                {
                    bool pmc = row == 1;
                    for (int tier = 0; tier < LabGear.Tiers.Length; tier++)
                    {
                        Status = $"Lab: spawning {done + failed + 1}/10 ({(pmc ? "PMC" : "Scav")} {LabGear.Tiers[tier].Label})";
                        // 2.2 m apart; back row 4 m behind and shifted half a gap so it isn't hidden.
                        var spot = Ground(origin + forward * (distance + row * 4f) + right * ((tier - 2) * 2.2f + row * 1.1f));
                        try
                        {
                            var role = pmc ? (tier % 2 == 0 ? WildSpawnType.pmcUSEC : WildSpawnType.pmcBEAR) : WildSpawnType.assault;
                            var bot = await SpawnOne(spawner, controller, role, tier, spot);
                            var player = bot?.GetPlayer;
                            if (player == null)
                            {
                                failed++;
                                continue;
                            }

                            player.Teleport(spot);
                            Dummies[player.Id] = new Dummy
                            {
                                Player = player,
                                Bot = bot,
                                Tier = tier,
                                IsPmc = pmc,
                                Spot = spot,
                                LookAt = origin + Vector3.up * 1.5f,
                            };
                            done++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Plugin.Log.LogError($"Lab: spawn failed ({(pmc ? "PMC" : "Scav")} T{tier}): {ex.Message}");
                        }
                    }
                }

                Status = $"Lab: {done} dummies spawned{(failed > 0 ? $", {failed} failed (see log)" : "")}. {Plugin.LabClearKey.Value} clears.";
                Plugin.Log.LogInfo(Status);
            }
            finally
            {
                _busy = false;
            }
        }

        private static async Task<BotOwner> SpawnOne(BotSpawner spawner, BotsController controller, WildSpawnType role, int tier, Vector3 spot)
        {
            var profile = await RequestProfile(role);
            LabGear.Apply(profile, tier);

            // With AI amount None nothing preloaded bot models; load this profile's prefabs
            // (body, clothes, our armour) the same way EFT's BotSpawner does.
            await Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(
                ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Local,
                profile.GetAllPrefabPaths(false).ToArray(), JobYieldPriority.General);

            var spawnParams = new BotSpawnParams { Id_spawn = "realtargets-lab-" + Guid.NewGuid().ToString("N") };
            var data = BotCreationData.CreateWithoutProfile(new GetProfileDataParams(
                profile.Info.Side, profile.Info.Settings.Role, profile.Info.Settings.BotDifficulty, 5f, spawnParams));
            data.AddProfile(profile);

            if (!TryNearestZone(spawner, controller, data, spot, out var zone, out var point))
            {
                throw new InvalidOperationException("no bot zone on this map accepts this bot type");
            }

            data.AddPosition(spot, point.CorePointId);
            spawner.SetMaxBots(spawner.MaxBots + 1);
            spawner._inSpawnProcess++;

            var done = new TaskCompletionSource<BotOwner>();
            try
            {
                spawner.method_10(zone, data, bot => done.TrySetResult(bot), spawner.GetCancelToken());
            }
            catch
            {
                spawner._inSpawnProcess = Math.Max(0, spawner._inSpawnProcess - 1);
                throw;
            }

            var finished = await Task.WhenAny(done.Task, Task.Delay(30000));
            if (finished != done.Task)
            {
                throw new TimeoutException("bot activation timed out");
            }
            return await done.Task;
        }

        private static async Task<Profile> RequestProfile(WildSpawnType role)
        {
            string body = JsonConvert.SerializeObject(new
            {
                conditions = new[] { new { Role = role.ToString(), Limit = 1, Difficulty = BotDifficulty.normal.ToString() } },
            });

            string json = await RequestHandler.PostJsonAsync("/client/game/bot/generate", body);
            var token = JToken.Parse(json);
            if (token.Value<int?>("err").GetValueOrDefault() != 0)
            {
                throw new InvalidOperationException(token.Value<string>("errmsg") ?? "SPT bot generation failed");
            }

            var profiles = JsonConvert.DeserializeObject<ProfileDescriptor[]>((token["data"] ?? token).ToString());
            if (profiles == null || profiles.Length == 0)
            {
                throw new InvalidOperationException("SPT returned no bot profile");
            }
            return new Profile(profiles[0]);
        }

        private static bool TryNearestZone(BotSpawner spawner, BotsController controller, BotCreationData data, Vector3 spot,
            out BotZone zone, out ISpawnPoint point)
        {
            zone = null;
            point = null;
            float best = float.MaxValue;
            var zones = spawner._allBotZones;
            if (zones == null)
            {
                return false;
            }

            foreach (var candidate in zones)
            {
                if (candidate?.SpawnPoints == null || !data.CanAtZoneByType(candidate, controller.ZonesLeaveController))
                {
                    continue;
                }

                foreach (var sp in candidate.SpawnPoints)
                {
                    if (sp == null) continue;
                    float d = (sp.Position - spot).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        zone = candidate;
                        point = sp;
                    }
                }
            }
            return zone != null;
        }

        private static Vector3 Ground(Vector3 position)
        {
            if (Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out var hit, 6f, EFTHardSettings.Instance.ENVIRONMENT_HIT_MASK))
            {
                return hit.point;
            }
            return position;
        }

        private void Clear()
        {
            int removed = 0;
            var game = Singleton<IBotGame>.Instance;
            var despawn = game?.GetType().GetMethod("BotDespawn", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(BotOwner) }, null);
            foreach (var dummy in Dummies.Values.ToList())
            {
                try
                {
                    var health = dummy.Player != null ? dummy.Player.ActiveHealthController : null;
                    if (dummy.Bot != null && health != null && health.IsAlive && despawn != null)
                    {
                        despawn.Invoke(game, new object[] { dummy.Bot });
                        removed++;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Lab: despawn failed: {ex.Message}");
                }
            }

            Dummies.Clear();
            Status = $"Lab: cleared {removed} living dummies (corpses stay).";
            Plugin.Log.LogInfo(Status);
        }
    }
}
