using System.Collections.Generic;
using System.Text;
using Comfort.Common;
using EFT;
using RealTargets.Gore;
using RealTargets.Hits;
using RealTargets.Lab;
using UnityEngine;

namespace RealTargets.Diagnostics
{
    /// <summary>
    /// In-raid debug HUD (IMGUI), shown while [Debug] Debug mode is on. Tabs: Hits (recent
    /// projectiles) and Blood (the focus bot's bleed sites, 0.10.0). Focus = bot under the
    /// crosshair (focus key or middle click), else the last bot hit. Non-interactive: it never
    /// takes the mouse or keyboard from the game.
    /// </summary>
    internal sealed class DebugOverlay : MonoBehaviour
    {
        internal enum Tab { Hits, Blood }

        private const float Width = 660f;

        internal static Player Focus { get; private set; }
        internal static Tab CurrentTab { get; private set; } = Tab.Hits;

        private static bool _visibleStatic = true;
        private readonly LinkedList<string> _rows = new LinkedList<string>();
        private GUIStyle _box;
        private GUIStyle _title;
        private GUIStyle _row;
        private Texture2D _background;
        private float _nextPanelRefresh;
        private string _panelText = "";

        private void Awake()
        {
            HitCollector.PublishedVolley += OnVolley;
        }

        private void OnDestroy()
        {
            HitCollector.PublishedVolley -= OnVolley;
            if (_background != null)
            {
                Destroy(_background);
            }
        }

        private void OnVolley(HitVolley volley)
        {
            if (!Plugin.DebugMode.Value)
            {
                return;
            }

            var target = volley.Target;
            if (target != null && target.IsAI)
            {
                Focus = target;
            }

            _rows.AddFirst(HitFormat.OverlayText(volley));
            while (_rows.Count > Plugin.OverlayRows.Value)
            {
                _rows.RemoveLast();
            }
        }

        private void Update()
        {
            if (!InRaid())
            {
                // Start each raid clean.
                _rows.Clear();
                Focus = null;
                return;
            }

            if (!Plugin.DebugMode.Value)
            {
                return;
            }

            if (Plugin.OverlayToggleKey.Value.IsDown())
            {
                _visibleStatic = !_visibleStatic;
            }

            if (Plugin.TabKey.Value.IsDown())
            {
                CurrentTab = CurrentTab == Tab.Hits ? Tab.Blood : Tab.Hits;
            }

            if (Plugin.FocusKey.Value.IsDown() && PickFocus())
            {
                _visibleStatic = true;
            }

            if (Plugin.SelectKey.Value.IsDown() && PickFocus())
            {
                CurrentTab = Tab.Blood;
                _visibleStatic = true;
            }

            if (Focus != null && Focus.gameObject == null)
            {
                Focus = null;
            }
        }

        private void OnGUI()
        {
            if (!Plugin.DebugMode.Value || !_visibleStatic || !InRaid())
            {
                return;
            }

            EnsureStyles();
            DrawDummyLabels();

            GUILayout.BeginArea(new Rect(Plugin.OverlayX.Value, Plugin.OverlayY.Value, Width, Screen.height));
            GUILayout.BeginVertical(_box);
            GUILayout.Label(
                $"<b>RealTargets {PluginInfo.Version}</b> | {TabNames()} | " +
                $"<color=#9a9a9a>{Plugin.OverlayToggleKey.Value} hide, {Plugin.TabKey.Value} tab, {Plugin.FocusKey.Value}/middle click focus</color>" +
                (Plugin.GhostMode.Value ? " | <color=#7fb2ff>GHOST</color>" : ""),
                _title);

            if (!string.IsNullOrEmpty(TestLab.Status))
            {
                GUILayout.Label($"<color=#9a9a9a>{TestLab.Status}</color>", _row);
            }

            switch (CurrentTab)
            {
                case Tab.Hits:
                    if (_rows.Count == 0)
                    {
                        GUILayout.Label("<color=#9a9a9a>No hits yet.</color>", _row);
                    }
                    foreach (var row in _rows)
                    {
                        GUILayout.Label(row, _row);
                    }
                    break;

                case Tab.Blood:
                    // Rebuilding the text every OnGUI call is wasteful; 4x per second is plenty.
                    if (Time.unscaledTime >= _nextPanelRefresh)
                    {
                        _nextPanelRefresh = Time.unscaledTime + 0.25f;
                        _panelText = BloodText(Focus);
                    }
                    GUILayout.Label(_panelText, _row);
                    break;
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        /// <summary>The focus bot's bleeding: state, blood lost, heart, every site.</summary>
        private static string BloodText(Player player)
        {
            if (player == null)
            {
                return "<color=#9a9a9a>No focus bot (shoot one or middle click it).</color>";
            }

            var b = BleedSystem.Find(player);
            if (b == null)
            {
                return $"<b>{player.Profile?.Nickname}</b>: <color=#9a9a9a>not bleeding</color>";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"<b>{b.Name}</b>  <color=#ffa94d>{b.StateText}</color>  lost <b>{b.BloodLost:F0} ml</b> ({b.LossFraction:P0})  " +
                          $"now {b.TotalRate:F0} ml/min  BP {b.Pressure:P0}  HR {b.HeartRate:F0}" +
                          (b.MouthBlood > 0.05f ? $"  mouth {b.MouthBlood:P0}" : "") +
                          $"  pool {b.PoolRadius * 100f:F0} cm ({b.PoolVolume:F0} ml)");
            int n = 0;
            foreach (var s in b.Sites)
            {
                n++;
                string kind = s.Kind == BleedKind.Arterial ? $"<color=#ff5050>{s.Kind}</color>" : s.Kind.ToString();
                sb.AppendLine($"#{n} {s.Collider} {kind}{(s.HasExit ? " through" : "")}{(s.Treated ? " <color=#7fd07f>treated</color>" : "")}: " +
                              $"{s.Rate:F0} ml/min (flow {s.Flow:F0}), out {s.Drained:F0} ml, stains {s.Stains}  " +
                              $"<color=#9a9a9a>{s.Weapon} {s.Energy:F0} J</color>");
            }
            foreach (var e in b.Events)
            {
                sb.AppendLine($"<color=#9a9a9a>{e}</color>");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Floating "Scav T2 cl4 ..." labels above lab dummies.</summary>
        private void DrawDummyLabels()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            foreach (var dummy in TestLab.All)
            {
                var player = dummy.Player;
                if (player == null || player.PlayerBones == null || player.PlayerBones.Head == null)
                {
                    continue;
                }

                // Back-row (PMC) labels sit higher so the two rows' labels don't overlap.
                var world = player.PlayerBones.Head.Original.position + Vector3.up * (dummy.IsPmc ? 0.75f : 0.35f);
                var screen = cam.WorldToScreenPoint(world);
                if (screen.z <= 0f)
                {
                    continue;
                }

                var b = BleedSystem.Find(player);
                string state = b != null ? $" <color=#ffa94d>{b.StateText}</color>\n-{b.BloodLost:F0} ml, {b.TotalRate:F0} ml/min" : "";
                var text = $"<b>{dummy.Label}</b>{state}";
                var size = _row.CalcSize(new GUIContent(text));
                GUI.Label(new Rect(screen.x - size.x / 2f, Screen.height - screen.y - size.y, size.x, size.y), text, _row);
            }
        }

        private static string TabNames()
        {
            string Mark(Tab t, string name) => t == CurrentTab ? $"<b>{name}</b>" : $"<color=#9a9a9a>{name}</color>";
            return $"{Mark(Tab.Hits, "Hits")} {Mark(Tab.Blood, "Blood")}";
        }

        private static bool InRaid() => Singleton<GameWorld>.Instantiated;

        /// <summary>Focus the bot under the crosshair (main camera ray, 300 m).</summary>
        private static bool PickFocus()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                return false;
            }

            var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, 300f);
            float best = float.MaxValue;
            Player found = null;
            foreach (var hit in hits)
            {
                var bpc = hit.collider != null ? hit.collider.GetComponent<BodyPartCollider>() : null;
                var player = bpc != null ? bpc.Player as Player : hit.collider?.GetComponentInParent<Player>();
                if (player != null && !player.IsYourPlayer && hit.distance < best)
                {
                    best = hit.distance;
                    found = player;
                }
            }

            if (found != null)
            {
                Focus = found;
                Plugin.Log.LogInfo($"Debug focus: {found.Profile?.Nickname}");
            }
            return found != null;
        }

        private void EnsureStyles()
        {
            if (_box != null)
            {
                return;
            }

            _background = new Texture2D(1, 1);
            _background.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.7f));
            _background.Apply();

            _box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(8, 8, 6, 6) };
            _box.normal.background = _background;
            _box.alignment = TextAnchor.UpperLeft;

            _title = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14, wordWrap = false };
            _title.normal.textColor = Color.white;

            _row = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                fontSize = 13,
                wordWrap = true,
                margin = new RectOffset(0, 0, 3, 3),
            };
            _row.normal.textColor = Color.white;
        }
    }
}
