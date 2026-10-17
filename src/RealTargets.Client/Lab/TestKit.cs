using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using SPT.Reflection.Utils;
using UnityEngine;

namespace RealTargets.Lab
{
    /// <summary>
    /// Mails the current test kit to the player before tests. Each test plan ships a testkit.json
    /// next to the DLL; when the player is logged in and in the menus (not in raid), the mod sends
    /// SPT's own chat-bot command "spt give &lt;template&gt; &lt;count&gt;" to the Commando contact for every
    /// item (POST /client/mail/msg/send, the same request the game's messenger makes), and the
    /// items arrive as messenger mail. Every new kitId mails the whole kit (Stompa loses it in each
    /// test raid); the legacy "once" flag is ignored. Delivered kit ids are kept in kit-state.json.
    /// </summary>
    internal sealed class TestKit : MonoBehaviour
    {
        private sealed class Kit
        {
            [JsonProperty("kitId")] public string KitId = null;
            [JsonProperty("note")] public string Note = null;
            [JsonProperty("items")] public List<KitItem> Items = new List<KitItem>();
        }

        private sealed class KitItem
        {
            [JsonProperty("tpl")] public string Tpl = null;
            [JsonProperty("count")] public int Count = 1;
            [JsonProperty("name")] public string Name = null;
            [JsonProperty("once")] public bool Once = false;
        }

        private sealed class KitState
        {
            [JsonProperty("deliveredKits")] public List<string> DeliveredKits = new List<string>();
            [JsonProperty("onceDelivered")] public List<string> OnceDelivered = new List<string>();
        }

        private string _kitPath;
        private string _statePath;
        private bool _sending;
        private float _menuSince = -1f;

        internal static string Status { get; private set; } = "";

        internal void Init(string pluginFolder)
        {
            _kitPath = Path.Combine(pluginFolder, "testkit.json");
            _statePath = Path.Combine(pluginFolder, "kit-state.json");
        }

        private void Update()
        {
            if (_sending || _kitPath == null || Singleton<GameWorld>.Instantiated || !LoggedIn())
            {
                _menuSince = -1f;
                return;
            }

            if (_menuSince < 0f)
            {
                _menuSince = Time.realtimeSinceStartup;
            }

            if (Plugin.TestKitResendKey.Value.IsDown())
            {
                _ = Deliver(force: true);
                return;
            }

            // Give the menu a few seconds to settle after login or a raid.
            if (Plugin.TestKitAuto.Value && Time.realtimeSinceStartup - _menuSince > 5f)
            {
                _ = Deliver(force: false);
            }
        }

        private static bool LoggedIn()
        {
            try
            {
                return ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile != null;
            }
            catch
            {
                return false;
            }
        }

        private async Task Deliver(bool force)
        {
            _sending = true;
            try
            {
                if (!File.Exists(_kitPath))
                {
                    return;
                }

                var kit = JsonConvert.DeserializeObject<Kit>(File.ReadAllText(_kitPath));
                var state = File.Exists(_statePath)
                    ? JsonConvert.DeserializeObject<KitState>(File.ReadAllText(_statePath)) ?? new KitState()
                    : new KitState();

                if (kit == null || string.IsNullOrEmpty(kit.KitId) || (!force && state.DeliveredKits.Contains(kit.KitId)))
                {
                    return;
                }

                int sent = 0, failed = 0;
                foreach (var item in kit.Items)
                {
                    if (string.IsNullOrEmpty(item.Tpl) || item.Count <= 0) continue;

                    if (await Give(item))
                    {
                        sent++;
                    }
                    else
                    {
                        failed++;
                    }
                    await Task.Delay(300); // let the server answer each command in order
                }

                if (!state.DeliveredKits.Contains(kit.KitId)) state.DeliveredKits.Add(kit.KitId);
                File.WriteAllText(_statePath, JsonConvert.SerializeObject(state, Formatting.Indented));

                Status = $"Test kit '{kit.KitId}': {sent} items mailed (Messenger > Commando){(failed > 0 ? $", {failed} failed" : "")}.";
                Plugin.Log.LogInfo(Status + (string.IsNullOrEmpty(kit.Note) ? "" : $" {kit.Note}"));
                Notify(Status);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Test kit delivery failed: {ex}");
            }
            finally
            {
                _sending = false;
            }
        }

        private static async Task<bool> Give(KitItem item)
        {
            string body = JsonConvert.SerializeObject(new
            {
                dialogId = Plugin.TestKitCommandoId.Value,
                type = 1,
                text = $"spt give {item.Tpl} {item.Count}",
                replyTo = "",
            });

            try
            {
                string response = await RequestHandler.PostJsonAsync("/client/mail/msg/send", body);
                var token = JToken.Parse(response);
                int err = token.Value<int?>("err").GetValueOrDefault();
                if (err != 0)
                {
                    Plugin.Log.LogWarning($"Test kit: give {item.Name ?? item.Tpl} x{item.Count} -> error {err}: {token.Value<string>("errmsg")}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Test kit: give {item.Name ?? item.Tpl} failed: {ex.Message}");
                return false;
            }
        }

        private static void Notify(string message)
        {
            try
            {
                EFT.Communications.NotificationManager.DisplayMessageNotification(message);
            }
            catch
            {
                // Notification is a nicety; the log line is the record.
            }
        }
    }
}
