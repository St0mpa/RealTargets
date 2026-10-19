using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using RealTargets.Hits;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RealTargets.Autopsy
{
    /// <summary>
    /// The AUTOPSY tab on a body's loot panel. EFT draws that panel with ComplexStashPanel.Show (header "LOOT",
    /// the body's equipment slots and containers); a postfix adds an AUTOPSY label next to the header. Clicking
    /// it covers the loot with the report (the loot is hidden and click-through off, not touched); clicking LOOT
    /// or closing the screen puts it back. Built at runtime from uGUI parts with the header's own font, so it
    /// reads like EFT. Any mismatch after a game update: the label simply doesn't appear (one warning).
    /// Host mode (<see cref="CreateHosted"/>, 1.1.0): the same report for your own death on the raid results (DeathReport).
    /// </summary>
    internal sealed class AutopsyView : MonoBehaviour
    {
        private const string Grey = "#9a9a9a";
        private const string Dim = "#6f726f";
        private const string Red = "#e0483a";
        private const string Orange = "#e8a14a";
        private const string ArmourBlue = "#8fb8ff";

        private static bool _failed;
#if !PUBLIC
        private static bool _dumped;
#endif

        private ComplexStashPanel _panel;
        private InventoryEquipment _equipment;
        private TMP_Text _header;
        private TMP_FontAsset _font;
        private Material _fontMaterial;

        private RectTransform _headerRoot;
        private GameObject _toggle;
        private Image _toggleBack;
        private TextMeshProUGUI _toggleText;
        private GameObject _lootHit;
        private RectTransform _root;
        private RectTransform _content;
        private ScrollRect _scroll;
        private readonly List<(CanvasGroup group, float alpha, bool raycasts, bool interactable, bool added)> _hidden =
            new List<(CanvasGroup, float, bool, bool, bool)>();
        private readonly List<RectTransform> _cover = new List<RectTransform>();

        private bool _showing;
        private BodyRecord _record;
        private int _builtVersion = -1;
        private AutopsyDiagram _diagram;
        private RawImage _diagramImage;
        private AutopsyModel _model;
        private bool _using3D;
        private static bool _3dFailed;
        private readonly List<(int shot, RectTransform label)> _modelLabels = new List<(int, RectTransform)>();
        private readonly List<AutopsyShot> _visible = new List<AutopsyShot>();
        private int _highlight;
        // The clicked shot: the 3D camera looks at it and its row shows the details (0 = none).
        private int _focused;
        private readonly List<(Image image, Color normal, int shot, GameObject detail)> _rows = new List<(Image, Color, int, GameObject)>();

        // Host mode (1.1.0 death screen): the report fills an area the caller places (the raid results' model spot), for a
        // record and a 3D model the caller made in the raid (the body is gone by the results screen). No loot panel.
        private RectTransform _hostArea;
        private bool _modelShared;
        private bool IsHosted => _hostArea != null;
        // Rendering threw on this view: the 2D chart from the next rebuild on.
        private bool _renderFailed;

        // ---------- patches ----------

        internal static void Enable()
        {
            new ShowPatch().Enable();
            new ClosePatch().Enable();
            Plugin.AutopsyOnlyMine.SettingChanged += (_, __) =>
            {
                foreach (var view in FindObjectsOfType<AutopsyView>()) view._builtVersion = -1;
            };
            // The 3D layers only need a redraw.
            EventHandler redraw = (_, __) =>
            {
                foreach (var view in FindObjectsOfType<AutopsyView>()) view._model?.Invalidate();
            };
            Plugin.AutopsySkeleton.SettingChanged += redraw;
            Plugin.AutopsyOrgans.SettingChanged += redraw;
            Plugin.AutopsyCavities.SettingChanged += redraw;
        }

        private sealed class ShowPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ComplexStashPanel), nameof(ComplexStashPanel.Show));

            [PatchPostfix]
            private static void Postfix(ComplexStashPanel __instance, InventoryEquipment equipment)
            {
                if (_failed) return;
                try
                {
                    var view = __instance.GetComponent<AutopsyView>() ?? __instance.gameObject.AddComponent<AutopsyView>();
                    view.Attach(__instance, equipment);
                }
                catch (Exception ex)
                {
                    _failed = true;
                    Plugin.Log.LogWarning($"[Autopsy] tab disabled (loot screen layout not as expected): {ex}");
                }
            }
        }

        private sealed class ClosePatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(ComplexStashPanel), nameof(ComplexStashPanel.Close));

            [PatchPrefix]
            private static void Prefix(ComplexStashPanel __instance)
            {
                try
                {
                    var view = __instance.GetComponent<AutopsyView>();
                    if (view != null) view.Detach();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Autopsy] close: {ex.Message}");
                }
            }
        }

        // ---------- lifecycle ----------

        private void Attach(ComplexStashPanel panel, InventoryEquipment equipment)
        {
            _panel = panel;
            _equipment = equipment;
            bool wanted = Plugin.AutopsyEnabled.Value && Singleton<GameWorld>.Instantiated && equipment != null &&
                          equipment.Parent?.GetOwner() is var owner && owner != null && !IsOwnInventory(owner);
            if (!wanted)
            {
                Detach();
                return;
            }

            _header = panel._complexGridName;
            if (_header == null) throw new InvalidOperationException("no _complexGridName");
            _font = _header.font != null ? _header.font : TMP_Settings.defaultFontAsset;
            _fontMaterial = _header.font != null ? _header.fontSharedMaterial : null;

#if !PUBLIC
            if (!_dumped)
            {
                _dumped = true;
                Dump(panel);
            }
#endif
            // The header text sits inside EFT's LOOT tab button (ToggleEFT "Header" with its own CanvasGroup and stripe
            // masks; 1.1.0 test: a label parented there came out faded, clipped and unclickable). Our tab is a sibling
            // of that button instead, placed beside it.
            _headerRoot = _header.transform as RectTransform;
            while (_headerRoot != null && _headerRoot.parent != panel.transform) _headerRoot = _headerRoot.parent as RectTransform;
            if (_headerRoot == null) throw new InvalidOperationException("header is not under the panel");

            BuildToggle();
            _toggle.SetActive(true);
            _toggle.transform.SetAsLastSibling();
            SetMode(false);
        }

        /// <summary>The loot screen of your own stash or gear never gets the tab, only other bodies.</summary>
        private static bool IsOwnInventory(IItemOwner owner)
        {
            var world = Singleton<GameWorld>.Instance;
            var you = world != null ? world.MainPlayer : null;
            return you != null && ReferenceEquals(owner, you.InventoryController);
        }

        private void Detach()
        {
            SetMode(false);
            if (_toggle != null) _toggle.SetActive(false);
            _equipment = null;
            _record = null;
        }

        private void OnDestroy()
        {
            if (_diagram?.Texture != null) Destroy(_diagram.Texture);
            if (!_modelShared) _model?.Dispose();
            _model = null;
        }

        // ---------- host mode ----------

        /// <summary>A report that fills <paramref name="area"/> (anchors it to the area's edges), in the font of <paramref name="fontFrom"/>.</summary>
        internal static AutopsyView CreateHosted(RectTransform area, TMP_Text fontFrom)
        {
            var view = area.gameObject.AddComponent<AutopsyView>();
            view._hostArea = area;
            view._font = fontFrom != null && fontFrom.font != null ? fontFrom.font : TMP_Settings.defaultFontAsset;
            view._fontMaterial = fontFrom != null && fontFrom.font != null ? fontFrom.fontSharedMaterial : null;
            return view;
        }

        /// <summary>Shows <paramref name="record"/>; <paramref name="model"/> (may be null or not ready: 2D chart) stays the caller's.</summary>
        internal void ShowHosted(BodyRecord record, AutopsyModel model)
        {
            if (!IsHosted) return;
            if (!_modelShared) _model?.Dispose();
            _model = model;
            _modelShared = true;
            _record = record;
            _highlight = 0;
            _focused = 0;
            _showing = true;
            BuildRoot();
            _root.gameObject.SetActive(true);
            if (_model != null && _model.Ready) _model.ResetView(false);
            Build();
            _scroll.verticalNormalizedPosition = 1f;
        }

        internal void HideHosted()
        {
            _showing = false;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        private void LateUpdate()
        {
            if (_toggle != null && _toggle.activeSelf) PlaceToggle();
            if (!_showing || _root == null) return;
            Align();
            if (_record != null && _record.Version != _builtVersion) Build();
            else if (_record == null && _builtVersion != 0) Build();
            Update3D();
        }

        // ---------- toggle ----------

        private static readonly Color TabBack = new Color(0.10f, 0.105f, 0.10f, 0.92f);
        private static readonly Color TabBackSelected = new Color(0.82f, 0.82f, 0.77f, 1f);
        private static readonly Color TabText = new Color(0.78f, 0.78f, 0.74f, 1f);
        private static readonly Color TabTextSelected = new Color(0.08f, 0.08f, 0.08f, 1f);

        private void BuildToggle()
        {
            if (_toggle != null) return;

            // AUTOPSY: a small tab of our own beside EFT's LOOT tab (sibling, not child: no inherited CanvasGroup/mask).
            _toggle = new GameObject("RealTargets.AutopsyTab", typeof(RectTransform));
            var rect = (RectTransform)_toggle.transform;
            rect.SetParent(_panel.transform, false);
            _toggle.AddComponent<LayoutElement>().ignoreLayout = true;
            _toggleBack = _toggle.AddComponent<Image>();
            _toggleBack.color = TabBack;
            var button = _toggle.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => SetMode(!_showing));

            var label = new GameObject("Text", typeof(RectTransform));
            var labelRect = (RectTransform)label.transform;
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            _toggleText = label.AddComponent<TextMeshProUGUI>();
            Style(_toggleText, Mathf.Max(_header.fontSize, 13f));
            _toggleText.enableWordWrapping = false;
            _toggleText.alignment = TextAlignmentOptions.Center;
            _toggleText.text = "<b>AUTOPSY</b>";

            // While the report is open, a clear hit area over EFT's LOOT tab takes you back to the loot.
            _lootHit = new GameObject("RealTargets.LootTab", typeof(RectTransform));
            _lootHit.transform.SetParent(_panel.transform, false);
            _lootHit.AddComponent<LayoutElement>().ignoreLayout = true;
            _lootHit.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            var lootButton = _lootHit.AddComponent<Button>();
            lootButton.transition = Selectable.Transition.None;
            lootButton.onClick.AddListener(() => SetMode(false));
            _lootHit.SetActive(false);

            PlaceToggle();
        }

        /// <summary>Beside EFT's LOOT tab, same height, in the panel's own space (follows resizes).</summary>
        private void PlaceToggle()
        {
            var parent = (RectTransform)_panel.transform;
            _headerRoot.GetWorldCorners(_corners);
            Vector2 min = parent.InverseTransformPoint(_corners[0]), max = parent.InverseTransformPoint(_corners[2]);
            float height = Mathf.Max(max.y - min.y + 8f, 24f);
            float width = Mathf.Max(_toggleText.preferredWidth + 24f, 96f);
            var origin = parent.rect.min;

            var rect = (RectTransform)_toggle.transform;
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(max.x + 14f, (min.y + max.y - height) * 0.5f) - origin;

            if (_lootHit.activeSelf)
            {
                var hit = (RectTransform)_lootHit.transform;
                hit.anchorMin = hit.anchorMax = Vector2.zero;
                hit.pivot = Vector2.zero;
                hit.sizeDelta = max - min;
                hit.anchoredPosition = min - origin;
            }
        }

        private void SetMode(bool autopsy)
        {
            _showing = autopsy;
            if (_toggleBack != null) _toggleBack.color = autopsy ? TabBackSelected : TabBack;
            if (_toggleText != null) _toggleText.color = autopsy ? TabTextSelected : TabText;
            if (_lootHit != null) _lootHit.SetActive(autopsy);

            if (!autopsy)
            {
                Restore();
                if (_root != null) _root.gameObject.SetActive(false);
                return;
            }

            BuildRoot();
            HideLoot();
            _record = AutopsyRecords.Find(_equipment);
            _highlight = 0;
            _focused = 0;
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            _toggle.transform.SetAsLastSibling();
            _lootHit.transform.SetAsLastSibling();
            PlaceToggle();
            Align();
            Build();
            _scroll.verticalNormalizedPosition = 1f;
        }

        // ---------- covering the loot ----------

        /// <summary>The loot column's scroll view (equipment slots + containers), else the loot transform itself.</summary>
        private void FindCover()
        {
            _cover.Clear();
            var loot = _panel._lootPanel as RectTransform;
            var scroll = loot != null ? loot.GetComponentInParent<ScrollRect>() : null;
            if (scroll != null && scroll.transform.IsChildOf(_panel.transform) && scroll.transform != _panel.transform)
            {
                _cover.Add((RectTransform)scroll.transform);
            }
            else if (loot != null)
            {
                _cover.Add(loot);
            }
            var containers = _panel._containersPanel != null ? _panel._containersPanel.transform as RectTransform : null;
            if (containers != null && !_cover.Exists(c => containers.IsChildOf(c)))
            {
                _cover.Add(containers);
            }
            // The loot's scrollbar is a sibling of the scroll view (1.1.0 layout dump: "Vertical Scrollbar").
            var bar = scroll != null ? scroll.verticalScrollbar : null;
            if (bar != null && !_cover.Exists(c => bar.transform.IsChildOf(c)))
            {
                _cover.Add((RectTransform)bar.transform);
            }
        }

        private void HideLoot()
        {
            Restore();
            FindCover();
            foreach (var rect in _cover)
            {
                var group = rect.GetComponent<CanvasGroup>();
                bool added = group == null;
                if (added) group = rect.gameObject.AddComponent<CanvasGroup>();
                _hidden.Add((group, group.alpha, group.blocksRaycasts, group.interactable, added));
                group.alpha = 0f;
                group.blocksRaycasts = false;
                group.interactable = false;
            }
        }

        private void Restore()
        {
            foreach (var (group, alpha, raycasts, interactable, added) in _hidden)
            {
                if (group == null) continue;
                if (added)
                {
                    Destroy(group);
                    continue;
                }
                group.alpha = alpha;
                group.blocksRaycasts = raycasts;
                group.interactable = interactable;
            }
            _hidden.Clear();
        }

        private readonly Vector3[] _corners = new Vector3[4];

        /// <summary>Lays the report over the covered area, in the panel's own space (follows resizes).</summary>
        private void Align()
        {
            if (IsHosted) return; // anchored to the host area's edges
            var parent = (RectTransform)_panel.transform;
            if (_cover.Count == 0) FindCover();
            if (_cover.Count == 0) return;
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            foreach (var rect in _cover)
            {
                if (rect == null) continue;
                rect.GetWorldCorners(_corners);
                foreach (var c in _corners)
                {
                    Vector2 local = parent.InverseTransformPoint(c);
                    min = Vector2.Min(min, local);
                    max = Vector2.Max(max, local);
                }
            }
            if (min.x > max.x) return;
            // The loot scroll view runs under EFT's LOOT tab: start the report below that header row.
            if (_headerRoot != null)
            {
                _headerRoot.GetWorldCorners(_corners);
                float headerBottom = Mathf.Min(parent.InverseTransformPoint(_corners[0]).y, parent.InverseTransformPoint(_corners[3]).y);
                if (headerBottom - 6f < max.y && headerBottom - 6f > min.y + 100f) max.y = headerBottom - 6f;
            }
            var origin = parent.rect.min;
            _root.anchorMin = _root.anchorMax = Vector2.zero;
            _root.pivot = Vector2.zero;
            _root.anchoredPosition = min - origin;
            _root.sizeDelta = max - min;
        }

        // ---------- report UI ----------

        private void BuildRoot()
        {
            if (_root != null) return;
            var go = new GameObject("RealTargets.Autopsy", typeof(RectTransform));
            _root = (RectTransform)go.transform;
            _root.SetParent(IsHosted ? _hostArea : _panel.transform, false);
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            if (IsHosted)
            {
                _root.anchorMin = Vector2.zero;
                _root.anchorMax = Vector2.one;
                _root.offsetMin = _root.offsetMax = Vector2.zero;
            }
            // Opaque over the results screen (release check 3: its big level and name texts showed through 0.97).
            go.AddComponent<Image>().color = new Color(0.055f, 0.06f, 0.058f, IsHosted ? 1f : 0.97f);

            var viewport = new GameObject("Viewport", typeof(RectTransform));
            var viewportRect = (RectTransform)viewport.transform;
            viewportRect.SetParent(_root, false);
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(0f, 0f);
            viewportRect.offsetMax = new Vector2(-4f, 0f);
            viewport.AddComponent<RectMask2D>();

            var content = new GameObject("Content", typeof(RectTransform));
            _content = (RectTransform)content.transform;
            _content.SetParent(viewportRect, false);
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.offsetMin = _content.offsetMax = Vector2.zero;
            var layout = content.AddComponent<VerticalLayoutGroup>();
            // Host mode: the AUTOPSY / BACK TO PMC button sits at the top left (release check 3: it covered the name).
            layout.padding = new RectOffset(14, 14, IsHosted ? 46 : 12, 16);
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _scroll = go.AddComponent<ScrollRect>();
            _scroll.viewport = viewportRect;
            _scroll.content = _content;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 30f;
            _scroll.inertia = false;

            _diagram = new AutopsyDiagram();
        }

        private void Style(TextMeshProUGUI text, float size)
        {
            if (_font != null) text.font = _font;
            if (_fontMaterial != null) text.fontSharedMaterial = _fontMaterial;
            text.fontSize = size;
            text.color = new Color(0.86f, 0.87f, 0.85f, 1f);
            text.richText = true;
            text.raycastTarget = false;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
        }

        private TextMeshProUGUI Text(Transform parent, string content, float size)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            Style(text, size);
            text.text = content;
            return text;
        }

        private void Build()
        {
            _builtVersion = _record != null ? _record.Version : 0;
            _rows.Clear();
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                var child = _content.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }

            if (_record == null)
            {
                Text(_content, $"<size=17><b>No recorded wounds</b></size>\n<color={Grey}>RealTargets saw no projectile hit this body this raid " +
                               "(it died before the mod was watching, or from something that isn't a projectile).</color>", 13f);
                return;
            }

            _visible.Clear();
            // Your own death: every shot was someone else's (Only my shots would hide them all).
            bool onlyMine = !IsHosted && Plugin.AutopsyOnlyMine.Value;
            int hidden = 0;
            foreach (var shot in _record.Shots)
            {
                if (onlyMine && !shot.ByYou) hidden++;
                else _visible.Add(shot);
            }

            Text(_content, Summary(_record), 13f);
            BuildDiagramRow();

            string armour = ArmourSummary(_visible);
            if (armour != null) Text(_content, armour, 12.5f);
            string gore = GoreSummary(_record, onlyMine);
            if (gore != null) Text(_content, gore, 12.5f);

            var heading = new StringBuilder("<b>SHOTS</b>");
            heading.Append($"  <color={Grey}>{_visible.Count} (oldest first)</color>");
            if (hidden > 0) heading.Append($"  <color={Dim}>{hidden} by others hidden (Only my shots)</color>");
            if (_record.Dropped > 0) heading.Append($"  <color={Dim}>{_record.Dropped} more not kept</color>");
            Text(_content, heading.ToString(), 13f);

            foreach (var shot in _visible) BuildRow(shot);
            if (_visible.Count == 0) Text(_content, $"<color={Grey}>None.</color>", 12.5f);

            // A rebuild (new hit on this body, Only my shots) keeps the clicked shot if it is still listed.
            if (_focused != 0 && !_visible.Exists(s => s.Number == _focused)) _focused = 0;
            if (_focused != 0)
            {
                _highlight = _focused;
                if (_using3D) _model.Focus(_focused, false);
            }
            ShowRowStates();
            RedrawDiagram();
        }

        private void BuildDiagramRow()
        {
            var row = new GameObject("Diagram", typeof(RectTransform));
            row.transform.SetParent(_content, false);
            // A narrow host (the results screen's model spot): the vitals go under the body instead of beside it.
            bool stacked = IsHosted && _hostArea.rect.width < AutopsyModel.Width + 260f;
            HorizontalOrVerticalLayoutGroup layout = stacked ? row.AddComponent<VerticalLayoutGroup>() : row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 14f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperLeft;

            var imageGo = new GameObject("Body", typeof(RectTransform));
            imageGo.transform.SetParent(row.transform, false);
            _diagramImage = imageGo.AddComponent<RawImage>();
            var element = imageGo.AddComponent<LayoutElement>();
            _modelLabels.Clear();

            // 3D in the death pose when the body and its pose are there; else the 2D front/back chart.
            _using3D = Try3D();
            if (_using3D)
            {
                _diagramImage.texture = _model.Texture;
                _diagramImage.raycastTarget = true;
                element.minWidth = element.preferredWidth = AutopsyModel.Width;
                element.minHeight = element.preferredHeight = AutopsyModel.Height;
                imageGo.AddComponent<ModelInput>().View = this;
                foreach (var shot in _visible)
                {
                    var label = Label(imageGo.transform, shot.Number.ToString(), Vector2.zero, 11f, shot.ByYou ? "#ffd27a" : "#b8d4ff");
                    _modelLabels.Add((shot.Number, label));
                }
                _model.Invalidate();
            }
            else
            {
                _diagramImage.texture = _diagram.Texture;
                _diagramImage.raycastTarget = false;
                element.minWidth = element.preferredWidth = AutopsyDiagram.Width;
                element.minHeight = element.preferredHeight = AutopsyDiagram.Height;
                Label(imageGo.transform, "FRONT", new Vector2(AutopsyDiagram.ViewCentreX(false), 9f), 11f, Grey);
                Label(imageGo.transform, "BACK", new Vector2(AutopsyDiagram.ViewCentreX(true), 9f), 11f, Grey);
                foreach (var shot in _visible)
                {
                    var first = shot.Projectiles.Find(p => p.Entry.Valid);
                    if (first == null) continue;
                    var at = AutopsyDiagram.Pixel(first.Entry.Point, first.Entry.Back);
                    Label(imageGo.transform, shot.Number.ToString(), at + new Vector2(9f, 7f), 11f, shot.ByYou ? "#ffd27a" : "#b8d4ff");
                }
            }

            var vitals = Text(row.transform, Vitals(_record, _using3D, _using3D ? _model.OrganSummary : null), 12.5f);
            var vitalsElement = vitals.gameObject.AddComponent<LayoutElement>();
            vitalsElement.flexibleWidth = 1f;
            vitalsElement.minWidth = 120f;
        }

        private bool Try3D()
        {
            if (_renderFailed) return false;
            // Host mode: the caller's snapshot, taken in the raid while the body was there.
            if (_modelShared) return _model != null && _model.Ready;
            if (_3dFailed || _record?.Pose == null || _record.Player == null) return false;
            try
            {
                _model ??= new AutopsyModel();
                return _model.Snapshot(_record, _visible);
            }
            catch (Exception ex)
            {
                // One failure turns the 3D view off for the session; the 2D chart stays.
                _3dFailed = true;
                Plugin.Log.LogWarning($"[Autopsy] 3D view off, using the 2D chart: {ex}");
                return false;
            }
        }

        /// <summary>Renders the 3D view when something changed and keeps the shot numbers on their entries.</summary>
        private void Update3D()
        {
            if (!_using3D || _model == null) return;
            bool rendered;
            try
            {
                if (!_model.Ready) throw new InvalidOperationException("the 3D body is gone");
                rendered = _model.RenderIfDirty(_highlight);
            }
            catch (Exception ex)
            {
                // Logged here: in EFT, Unity's own exception log reaches only Player.log, not BepInEx's (release check 3).
                Plugin.Log.LogWarning($"[Autopsy] 3D view off, using the 2D chart: {ex}");
                _renderFailed = true;
                _builtVersion = -1;
                return;
            }
            if (!rendered) return;
            if (_diagramImage != null && _diagramImage.texture != _model.Texture) _diagramImage.texture = _model.Texture;
            foreach (var (shot, label) in _modelLabels)
            {
                if (label == null) continue;
                bool visible = _model.LabelPoint(shot, out var viewport) && viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
                label.gameObject.SetActive(visible);
                if (visible) label.anchoredPosition = new Vector2(viewport.x * AutopsyModel.Width + 10f, viewport.y * AutopsyModel.Height + 8f);
            }
        }

        // Default: the body follows the mouse on both axes (1.1.0 test: turning was mirrored, tilting was not).
        // "3D drag turns the camera" flips both.
        internal void RotateModel(Vector2 delta) => _model?.Rotate(Plugin.AutopsyDragCamera.Value ? -delta : delta);
        // Share of the view's height the pointer moved: the body slides with it (the orbit point the other way).
        internal void PanModel(float share) => _model?.Pan(Plugin.AutopsyDragCamera.Value ? share : -share);
        internal void ZoomModel(float wheel) => _model?.Zoom(wheel);

        internal void ResetModel()
        {
            _focused = 0;
            _highlight = 0;
            _model?.ResetView(true);
            ShowRowStates();
            RedrawDiagram();
        }

        private sealed class ModelInput : MonoBehaviour, IBeginDragHandler, IDragHandler, IScrollHandler, IPointerClickHandler
        {
            public AutopsyView View;
            public void OnBeginDrag(PointerEventData eventData) { }
            public void OnDrag(PointerEventData eventData)
            {
                if (View == null) return;
                if (eventData.button == PointerEventData.InputButton.Left) View.RotateModel(eventData.delta);
                else if (eventData.button == PointerEventData.InputButton.Right)
                {
                    // Pixels -> the image's own units, so the slide matches the pointer at any UI scale.
                    var rect = (RectTransform)transform;
                    var cam = eventData.pressEventCamera;
                    if (rect.rect.height > 1f &&
                        RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, cam, out var now) &&
                        RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position - eventData.delta, cam, out var before))
                    {
                        View.PanModel((now.y - before.y) / rect.rect.height);
                    }
                }
            }
            public void OnScroll(PointerEventData eventData) => View?.ZoomModel(eventData.scrollDelta.y);
            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Left && eventData.clickCount >= 2) View?.ResetModel();
            }
        }

        private RectTransform Label(Transform parent, string content, Vector2 at, float size, string colour)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(60f, 16f);
            rect.anchoredPosition = at;
            var text = go.AddComponent<TextMeshProUGUI>();
            Style(text, size);
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.Center;
            text.text = $"<color={colour}><b>{content}</b></color>";
            return rect;
        }

        private void BuildRow(AutopsyShot shot)
        {
            var go = new GameObject($"Shot {shot.Number}", typeof(RectTransform));
            go.transform.SetParent(_content, false);
            var image = go.AddComponent<Image>();
            var normal = shot.Killing ? new Color(0.45f, 0.06f, 0.04f, 0.28f) : new Color(1f, 1f, 1f, 0.025f);
            image.color = normal;
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 5, 6);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            Text(go.transform, Row(shot), 12.5f);
            // Details: built with the row, shown while the row is the clicked one.
            var detail = Text(go.transform, Detail(shot), 12f).gameObject;
            detail.SetActive(false);
            var hover = go.AddComponent<RowHover>();
            hover.View = this;
            hover.Shot = shot.Number;
            _rows.Add((image, normal, shot.Number, detail));
        }

        internal void Hover(int shot, bool enter)
        {
            // Leaving a row falls back to the clicked shot, if any.
            int next = enter ? shot : (_highlight == shot ? _focused : _highlight);
            if (next == _highlight) return;
            _highlight = next;
            ShowRowStates();
            RedrawDiagram();
        }

        /// <summary>Click a row: the camera turns to that shot and its details open; click it again: back to the whole body.</summary>
        internal void Click(int shot)
        {
            if (_focused == shot)
            {
                _focused = 0;
                if (_using3D) _model?.ResetView(true);
            }
            else
            {
                _focused = shot;
                if (_using3D) _model?.Focus(shot, true);
            }
            _highlight = shot;
            ShowRowStates();
            RedrawDiagram();
        }

        private void ShowRowStates()
        {
            foreach (var (image, normal, number, detail) in _rows)
            {
                if (image == null) continue;
                float lift = number == _focused ? 0.14f : number == _highlight ? 0.09f : 0f;
                image.color = new Color(normal.r, normal.g, normal.b, normal.a + lift);
                if (detail != null && detail.activeSelf != (number == _focused)) detail.SetActive(number == _focused);
            }
        }

        private void RedrawDiagram()
        {
            if (_using3D)
            {
                _model?.Invalidate();
                return;
            }
            try
            {
                _diagram.Draw(_record, _visible, _highlight);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Autopsy] diagram: {ex.Message}");
            }
        }

        private sealed class RowHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
        {
            public AutopsyView View;
            public int Shot;
            public void OnPointerEnter(PointerEventData eventData) => View?.Hover(Shot, true);
            public void OnPointerExit(PointerEventData eventData) => View?.Hover(Shot, false);
            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Left) View?.Click(Shot);
            }
        }

        // ---------- report text ----------

        private static string Shooter(HitEvent hit) =>
            hit.ShooterIsYou ? "<color=#ffc440><b>YOU</b></color>"
            : string.IsNullOrEmpty(hit.ShooterName) ? $"<color={Grey}>unknown</color>"
            : $"<color=#8cbeff>{hit.ShooterName}</color> <color={Dim}>({AutopsyText.Role(hit.ShooterSide, hit.ShooterRole)})</color>";

        private static string Summary(BodyRecord r)
        {
            var sb = new StringBuilder(400);
            sb.Append($"<size=19><b>{r.Name}</b></size>   <color={Grey}>{r.Role}{(r.Level > 0 ? $" · level {r.Level}" : "")}</color>\n");
            if (!r.Dead)
            {
                sb.Append($"<color={Grey}>No death recorded.</color>");
                return sb.ToString();
            }

            var kill = r.KillingShot;
            if (r.KillerIsYou) sb.Append($"<b><color={Red}>KILLED BY YOU</color></b>");
            else if (!string.IsNullOrEmpty(r.KillerName)) sb.Append($"<b><color={Red}>KILLED BY</color></b> <color=#8cbeff>{r.KillerName}</color> <color={Dim}>({r.KillerRole})</color>");
            else sb.Append($"<b><color={Red}>DIED</color></b>");
            sb.Append($"   <color={Grey}>{r.DiedAt:HH:mm:ss} ({AutopsyText.Ago(DateTime.Now - r.DiedAt)})</color>\n");

            sb.Append($"<color={Grey}>Cause of death:</color> ");
            // The projectile that killed (or its later-frame hit, still inside), and the collider whose damage did it:
            // EFT's lethal segment, not the entry (1.1.0-legs: a forearm -> chest kill read "to the right arm").
            var killer = kill?.Projectiles.Find(p => p.Hit.Status == HitStatus.Killed || p.Continued.Exists(c => c.Status == HitStatus.Killed));
            var lethal = killer == null ? null : killer.Hit.Status == HitStatus.Killed ? killer.Hit : killer.Continued.Find(c => c.Status == HitStatus.Killed);
            bool bled = r.DeathType == EDamageType.LightBleeding || r.DeathType == EDamageType.HeavyBleeding;
            if (lethal == null && kill != null && kill.Credited && !kill.IsBlast)
            {
                // Died within a second of this shot from what EFT counts apart (a bleed tick on a part the shot left at
                // 1 HP): the shot is the cause, EFT's damage type the last straw.
                var hit = kill.First;
                var seg = CreditedSegment(hit, r.LethalPart);
                sb.Append($"{Ammo(hit)} to the {AutopsyText.Part(seg.BodyPart).ToLowerInvariant()} ({AutopsyText.Collider(seg.Collider)})");
                if (seg.BodyPart != hit.Entry.BodyPart && !hit.Entry.HasPlate)
                {
                    sb.Append($" <color={Grey}>through the {AutopsyText.Collider(hit.Entry.Collider)}</color>");
                }
                sb.Append($", {hit.Distance:F0} m (shot #{kill.Number}) <color={Grey}>· died {kill.CreditDelay:F1} s later, " +
                          $"{AutopsyText.DeathType(r.DeathType)}</color>");
                return sb.ToString();
            }
            if (lethal != null && lethal.IsBlast && !bled)
            {
                sb.Append($"blast of a {lethal.WeaponName} at {lethal.Distance:F1} m");
                if (lethal.BlastKpa > 1f) sb.Append($" (~{lethal.BlastKpa:F0} kPa)");
                if (r.LethalPart != EBodyPart.Common) sb.Append($", {AutopsyText.Part(r.LethalPart).ToLowerInvariant()}");
                sb.Append($" (shot #{kill.Number})");
            }
            else if (lethal != null && !bled)
            {
                var seg = lethal.Lethal;
                sb.Append($"{Ammo(lethal)} to the {AutopsyText.Part(seg.BodyPart).ToLowerInvariant()} ({AutopsyText.Collider(seg.Collider)})");
                if (seg.BodyPart != lethal.Entry.BodyPart && !lethal.Entry.HasPlate)
                {
                    sb.Append($" <color={Grey}>through the {AutopsyText.Collider(lethal.Entry.Collider)}</color>");
                }
                sb.Append($", {killer.Hit.Distance:F0} m (shot #{kill.Number})");
                string spread = SpreadTo(r, lethal, seg);
                if (spread != null)
                {
                    sb.Append($" <color={Grey}>· the destroyed {AutopsyText.Part(seg.BodyPart).ToLowerInvariant()}'s damage spread to the {spread}</color>");
                }
            }
            else
            {
                sb.Append(AutopsyText.DeathType(r.DeathType));
                if (!bled && r.LethalPart != EBodyPart.Common) sb.Append($", {AutopsyText.Part(r.LethalPart).ToLowerInvariant()}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// A limb segment killed although a vital part the round never damaged is what is destroyed at death: EFT spreads
        /// damage on a destroyed limb over the body (rc-1 test: an M80 took a forearm 1 -> 0 and the bot died at once
        /// with the thorax 18 -> 0; the line read "to the right arm"). Returns that part's name, else null.
        /// </summary>
        private static string SpreadTo(BodyRecord r, HitEvent hit, HitSegment seg)
        {
            if (seg.BodyPart == EBodyPart.Head || seg.BodyPart == EBodyPart.Chest) return null;
            foreach (var vital in new[] { EBodyPart.Head, EBodyPart.Chest })
            {
                if (!r.HealthAtDeath.TryGetValue(vital, out var hp) || hp.current > 0.5f) continue;
                bool crossed = hit.Segments.Exists(s => s.BodyPart == vital && s.DamageOut > 0.5f && s.AliveAfter);
                if (!crossed) return AutopsyText.Part(vital).ToLowerInvariant();
            }
            return null;
        }

        /// <summary>The credited shot's segment on EFT's lethal part, else its deepest flesh segment.</summary>
        private static HitSegment CreditedSegment(HitEvent hit, EBodyPart lethalPart)
        {
            HitSegment last = hit.Entry;
            foreach (var s in hit.Segments)
            {
                if (s.HasPlate) continue;
                if (s.BodyPart == lethalPart) return s;
                last = s;
            }
            return last;
        }

        private static string Ammo(HitEvent hit) =>
            !string.IsNullOrEmpty(hit.AmmoName) && hit.AmmoName != "none" ? hit.AmmoName : hit.WeaponName;

        private static string Vitals(BodyRecord r, bool model, string organs)
        {
            var sb = new StringBuilder(300);
            sb.Append("<b>AT DEATH</b>\n");
            if (r.HealthAtDeath.Count == 0)
            {
                sb.Append($"<color={Grey}>not recorded</color>");
                return sb.ToString();
            }
            foreach (var part in AutopsyRecords.Parts)
            {
                if (!r.HealthAtDeath.TryGetValue(part, out var hp)) continue;
                string colour = hp.destroyed ? Red : hp.current < hp.maximum - 0.5f ? Orange : Grey;
                sb.Append($"{AutopsyText.Part(part)}  <color={colour}>{Mathf.Ceil(hp.current):F0}/{hp.maximum:F0}</color>");
                if (hp.destroyed) sb.Append($" <color={Red}>destroyed</color>");
                if (r.EffectsAtDeath.TryGetValue(part, out var effects) && effects.Count > 0)
                {
                    sb.Append($" <color={Dim}>· {string.Join(", ", effects)}</color>");
                }
                sb.Append('\n');
            }
            if (model && !string.IsNullOrEmpty(organs))
            {
                sb.Append($"\n<b>ON THE PATH</b>\n<color={Orange}>{organs}</color>\n");
            }
            sb.Append(model
                ? $"\n<size=11><color={Dim}>Pose at the moment of death. Ball = entry, clear ball = exit, small ball = stopped by armour, " +
                  "red = fatal; yellow = your shots, blue = others. Bones (orange = fractured); organ zones (approximate) " +
                  "light up where a shot went through. Glowing spindle = temporary cavity: the tissue the round's " +
                  "energy stretched, widest where it dumped the most. Drag to turn, right-drag to move up/down, wheel to " +
                  "zoom, double-click to reset; hover a shot to trace it, click it to look at it and see the details.</color></size>"
                : $"\n<size=11><color={Dim}>dot = entry, ring = exit, small ring = stopped by armour, red ring = fatal; " +
                  "yellow = your shots, blue = others. Hover a shot to trace it, click it for the details.</color></size>");
            return sb.ToString();
        }

        private static string ArmourSummary(List<AutopsyShot> shots)
        {
            var pieces = new Dictionary<string, (string name, int cls, int hits, int through, int stopped, int deflected, float first, float last, float max)>();
            var order = new List<string>();
            foreach (var shot in shots)
            {
                foreach (var p in shot.Projectiles)
                {
                    foreach (var s in p.Hit.Segments)
                    {
                        var a = s.Armour;
                        if (!a.Any || string.IsNullOrEmpty(a.ItemId)) continue;
                        if (!pieces.TryGetValue(a.ItemId, out var e))
                        {
                            e = (a.Name, a.Class, 0, 0, 0, 0, a.DurabilityBefore, a.DurabilityAfter, a.DurabilityMax);
                            order.Add(a.ItemId);
                        }
                        e.hits++;
                        if (a.Outcome == ArmourOutcome.Penetrated) e.through++;
                        else if (a.Outcome == ArmourOutcome.Stopped) e.stopped++;
                        else if (a.Outcome == ArmourOutcome.Deflected) e.deflected++;
                        e.last = a.DurabilityAfter;
                        pieces[a.ItemId] = e;
                    }
                }
            }
            if (order.Count == 0) return null;

            var sb = new StringBuilder("<b>ARMOUR</b>\n");
            foreach (var id in order)
            {
                var e = pieces[id];
                sb.Append($"<color={ArmourBlue}>{e.name}</color> <color={Grey}>(class {e.cls})</color>: {e.hits} hit{(e.hits == 1 ? "" : "s")}");
                var parts = new List<string>(3);
                if (e.through > 0) parts.Add($"<color={Red}>{e.through} through</color>");
                if (e.stopped > 0) parts.Add($"{e.stopped} stopped");
                if (e.deflected > 0) parts.Add($"{e.deflected} ricochet{(e.deflected == 1 ? "" : "s")}");
                if (parts.Count > 0) sb.Append(": ").Append(string.Join(", ", parts));
                if (e.max > 0f) sb.Append($" <color={Grey}>· durability {e.first:F0} -> {e.last:F0}/{e.max:F0}</color>");
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static string GoreSummary(BodyRecord r, bool onlyMine)
        {
            if (r.Gore.Count == 0) return null;
            var sb = new StringBuilder("<b>GORE</b>\n");
            foreach (var g in r.Gore)
            {
                var shot = g.Shot > 0 ? r.Shots.Find(s => s.Number == g.Shot) : null;
                if (onlyMine && shot != null && !shot.ByYou) continue;
                sb.Append($"<color={Red}>{g.What}</color> <color={Grey}>({g.Why}{(g.Shot > 0 ? $", shot #{g.Shot}" : "")})</color>\n");
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>A blast wave's row: grenade, distance, overpressure, damage by part (EFT's numbers).</summary>
        private static string BlastRow(AutopsyShot shot)
        {
            var blast = shot.First;
            var sb = new StringBuilder(300);
            sb.Append($"<b>#{shot.Number}</b>  ");
            if (shot.Killing) sb.Append($"<b><color={Red}>FATAL</color></b>  ");
            sb.Append($"<b><color={Orange}>BLAST</color></b>  ").Append(Shooter(blast));
            sb.Append($" <color={Grey}>·</color> {blast.WeaponName} <color={Grey}>· {blast.Distance:F1} m</color>");
            if (blast.BlastKpa > 1f) sb.Append($" <color={Grey}>· ~{blast.BlastKpa:F0} kPa</color>");
            sb.Append('\n');
            float total = 0f;
            var parts = new List<string>(7);
            foreach (var s in blast.Segments)
            {
                total += s.DamageOut;
                parts.Add($"{AutopsyText.Part(s.BodyPart).ToLowerInvariant()} {s.DamageOut:F0}");
            }
            sb.Append($"<color={Grey}>Blast wave</color>");
            var from = shot.Projectiles[0].From;
            if (!string.IsNullOrEmpty(from)) sb.Append($" {from}");
            sb.Append($": {total:F0} HP <color={Grey}>({string.Join(", ", parts)})</color>");
            return sb.ToString();
        }

        /// <summary>The clicked BLAST row's extra lines: charge, overpressure, health before -> after per part.</summary>
        private string BlastDetail(AutopsyShot shot)
        {
            var blast = shot.First;
            var sb = new StringBuilder(400);
            sb.Append($"<color={Dim}>────────────</color>\n");
            sb.Append($"<color={Grey}>Time</color> {blast.WallTime:HH:mm:ss.f}");
            if (_record.Dead)
            {
                var toDeath = _record.DiedAt - blast.WallTime;
                if (toDeath.TotalSeconds >= 0.05) sb.Append($" <color={Grey}>· {Seconds(toDeath)} before death</color>");
            }
            sb.Append('\n');
            if (blast.BlastCharge > 0f)
            {
                sb.Append($"<color={Grey}>Charge</color> ~{blast.BlastCharge * 1000f:F0} g TNT <color={Grey}>· peak overpressure at the chest</color> " +
                          $"~{blast.BlastKpa:F0} kPa <color={Grey}>(lung injury from ~100, eardrums from ~35)</color>\n");
            }
            var parts = new List<string>(7);
            foreach (var s in blast.Segments)
            {
                if (!blast.HealthAfter.TryGetValue(s.BodyPart, out var hp)) continue;
                parts.Add($"{AutopsyText.Part(s.BodyPart)} {Mathf.Ceil(hp.current + s.DamageOut):F0} -> {Mathf.Ceil(hp.current):F0}/{hp.maximum:F0}");
            }
            if (parts.Count > 0) sb.Append($"<color={Grey}>Health</color> {string.Join(", ", parts)}\n");
            sb.Append($"<color={Grey}>EFT's own blast damage (no wound channel); its fragments are listed as their own row.</color>");
            sb.Append(shot.Killing ? $" <color={Red}>The blast killed.</color>" : "");
            return sb.ToString();
        }

        private static string Row(AutopsyShot shot)
        {
            if (shot.IsBlast) return BlastRow(shot);
            var first = shot.First;
            int count = shot.Projectiles.Count;
            bool multi = count > 1 || first.ProjectileCount > 1;
            bool fragments = first.IsFragment || first.DamageType == EDamageType.GrenadeFragment;

            var sb = new StringBuilder(500);
            sb.Append($"<b>#{shot.Number}</b>  ");
            if (shot.Killing) sb.Append($"<b><color={Red}>FATAL</color></b>  ");
            sb.Append(Shooter(first));
            if (!string.IsNullOrEmpty(first.WeaponName) && first.WeaponName != "none") sb.Append($" <color={Grey}>·</color> {first.WeaponName}");
            string ammo = Ammo(first);
            if (ammo != first.WeaponName) sb.Append($" <color={Grey}>·</color> {ammo}");
            sb.Append($" <color={Grey}>· {first.Distance:F0} m</color>");
            float energy = 0f;
            foreach (var p in shot.Projectiles) energy += p.Hit.ImpactEnergy;
            if (energy > 0.5f && !fragments) sb.Append($" <color={Grey}>· {energy:F0} J</color>");
            if (shot.PostMortem) sb.Append($" <color={Dim}>· after death</color>");
            sb.Append('\n');

            if (multi) AppendVolley(sb, shot, fragments);
            else AppendSingle(sb, shot.Projectiles[0]);
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>The clicked row's extra lines: timing, ballistics, armour penetration, damage per collider, wound
        /// channel and cavity, organs, the part's health before and after. Only what the hit record holds.</summary>
        private string Detail(AutopsyShot shot)
        {
            if (shot.IsBlast) return BlastDetail(shot);
            var sb = new StringBuilder(600);
            var first = shot.First;
            sb.Append($"<color={Dim}>────────────</color>\n");

            // When.
            sb.Append($"<color={Grey}>Time</color> {first.WallTime:HH:mm:ss.f}");
            var opening = _record.Shots.Count > 0 ? _record.Shots[0].First.WallTime : first.WallTime;
            if (shot.Number > 1) sb.Append($" <color={Grey}>· {Seconds(first.WallTime - opening)} after the first hit</color>");
            if (_record.Dead)
            {
                var toDeath = _record.DiedAt - first.WallTime;
                if (toDeath.TotalSeconds >= 0.05) sb.Append($" <color={Grey}>· {Seconds(toDeath)} before death</color>");
                else if (toDeath.TotalSeconds <= -0.05) sb.Append($" <color={Grey}>· {Seconds(-toDeath)} after death</color>");
            }
            sb.Append('\n');

            bool single = shot.Projectiles.Count == 1 && first.ProjectileCount <= 1 && !first.IsFragment;
            if (single) AppendBallistics(sb, first);
            else AppendProjectiles(sb, shot);

            // Wound channel (3D only: it is measured in the death pose).
            if (_using3D && _model != null)
            {
                if (_model.ChannelOf(shot.Number, out var ch) && ch.Length > 0.005f)
                {
                    sb.Append($"<color={Grey}>Wound channel</color> {ch.Length * 100f:F0} cm");
                    if (ch.Stretches > 1) sb.Append($" <color={Grey}>in {ch.Stretches} stretches (out of one part, into the next)</color>");
                    sb.Append(ch.StayedIn ? $", <color={Orange}>round stayed in</color>" : ", through");
                    if (ch.CavityRadius > 0f)
                    {
                        sb.Append($" <color={Grey}>· temporary cavity up to</color> {ch.CavityRadius * 200f:F0} cm <color={Grey}>across, widest</color> " +
                                  (ch.Expanding ? "near the entry (expanding)" : "deeper in (FMJ yaws)"));
                    }
                    sb.Append('\n');
                }
                string organs = _model.OrgansOf(shot.Number);
                if (organs.Length > 0) sb.Append($"<color={Grey}>Organs</color> <color={Orange}>{organs}</color>\n");
            }

            // Health of the part it hit, before (last recorded hit on that part, else full) and after.
            var part = MainPart(first);
            if (first.HealthAfter.TryGetValue(part, out var after))
            {
                float before = after.maximum;
                foreach (var earlier in _record.Shots)
                {
                    if (earlier.Number >= shot.Number) break;
                    foreach (var p in earlier.Projectiles)
                    {
                        if (p.Hit.HealthAfter.TryGetValue(part, out var hp)) before = hp.current;
                    }
                }
                foreach (var p in shot.Projectiles)
                {
                    if (p.Hit.HealthAfter.TryGetValue(part, out var hp)) after = hp;
                }
                sb.Append($"<color={Grey}>{AutopsyText.Part(part)}</color> {Mathf.Ceil(before):F0} -> {Mathf.Ceil(after.current):F0}/{after.maximum:F0}");
            }
            else
            {
                sb.Append($"<color={Grey}>{AutopsyText.Part(part)}</color>");
            }
            sb.Append(shot.Killing ? $" <color={Grey}>·</color> <color={Red}>this shot killed</color>"
                : shot.PostMortem ? $" <color={Grey}>· hit the corpse</color>"
                : $" <color={Grey}>· alive after it</color>");
            return sb.ToString();
        }

        private static string Seconds(TimeSpan span) => span.TotalSeconds < 60 ? $"{span.TotalSeconds:F1} s" : $"{(int)span.TotalMinutes} min {span.Seconds} s";

        private static EBodyPart MainPart(HitEvent hit)
        {
            var flesh = hit.Segments.Exists(s => !s.HasPlate) ? hit.Segments.Find(s => !s.HasPlate) : hit.Entry;
            return flesh.BodyPart;
        }

        private static void AppendBallistics(StringBuilder sb, HitEvent hit)
        {
            var entry = hit.Entry;
            sb.Append($"<color={Grey}>Round</color> {Ammo(hit)}");
            var round = new List<string>(2);
            if (!string.IsNullOrEmpty(hit.Caliber)) round.Add(hit.Caliber);
            if (hit.BulletMassGram > 0f) round.Add($"{hit.BulletMassGram:0.##} g");
            if (round.Count > 0) sb.Append($" <color={Grey}>({string.Join(", ", round)})</color>");
            if (hit.ImpactSpeed > 0f)
            {
                sb.Append($" <color={Grey}>·</color> {hit.ImpactSpeed:F0} m/s <color={Grey}>at impact</color>");
                if (hit.MuzzleSpeed > hit.ImpactSpeed + 0.5f) sb.Append($" <color={Grey}>({hit.MuzzleSpeed:F0} at the muzzle)</color>");
            }
            if (hit.ImpactEnergy > 0.5f) sb.Append($" <color={Grey}>·</color> {hit.ImpactEnergy:F0} J");
            sb.Append('\n');

            if (entry.PenetrationIn > 0f) sb.Append($"<color={Grey}>Penetration power</color> {entry.PenetrationIn:F0}");
            bool armour = false;
            foreach (var s in hit.Segments)
            {
                if (!s.Armour.Any) continue;
                armour = true;
                sb.Append($" <color={Grey}>·</color> <color={ArmourBlue}>{s.Armour.Name}</color> <color={Grey}>class {s.Armour.Class} met pen</color> {s.PenetrationIn:F0}");
            }
            if (entry.PenetrationIn > 0f || armour) sb.Append('\n');
            if (armour && hit.ImpactEnergy > 0.5f && hit.FleshFraction < 0.995f)
            {
                sb.Append($"<color={Grey}>Past the armour</color> {hit.EffectiveEnergy:F0} J <color={Grey}>({hit.FleshFraction * 100f:F0} % of the damage)</color>\n");
            }

            // Damage collider by collider (plates are their own segment and do no damage).
            var parts = new List<string>(4);
            foreach (var s in hit.Segments)
            {
                if (s.HasPlate) continue;
                parts.Add($"{AutopsyText.Collider(s.Collider)} {s.DamageIn:F0} -> {s.DamageOut:F0}");
            }
            if (parts.Count > 1) sb.Append($"<color={Grey}>Damage by place</color> {string.Join(", ", parts)}\n");
        }

        private static void AppendProjectiles(StringBuilder sb, AutopsyShot shot)
        {
            const int MaxLines = 12;
            var first = shot.First;
            bool fragments = first.IsFragment || first.DamageType == EDamageType.GrenadeFragment;
            sb.Append($"<color={Grey}>Round</color> {Ammo(first)}");
            if (!fragments && first.ImpactSpeed > 0f) sb.Append($" <color={Grey}>·</color> {first.ImpactSpeed:F0} m/s <color={Grey}>at impact</color>");
            sb.Append('\n');
            int n = 0;
            foreach (var p in shot.Projectiles)
            {
                if (n == MaxLines)
                {
                    sb.Append($"<color={Dim}>+{shot.Projectiles.Count - MaxLines} more</color>\n");
                    break;
                }
                n++;
                var hit = p.Hit;
                var flesh = hit.Segments.Exists(s => !s.HasPlate) ? hit.Segments.Find(s => !s.HasPlate) : hit.Entry;
                sb.Append($"<color={Grey}>{(fragments ? "fragment" : "pellet")} {n}</color> {AutopsyText.Collider(flesh.Collider)}");
                if (hit.ImpactEnergy > 0.5f) sb.Append($" <color={Grey}>· {hit.ImpactEnergy:F0} J</color>");
                float body = 0f;
                foreach (var s in hit.Segments)
                {
                    if (!s.HasPlate) body += s.DamageOut;
                }
                sb.Append($" <color={Grey}>· damage</color> {body:F0}");
                sb.Append(AutopsyDiagram.StoppedByArmour(hit) ? $" <color={ArmourBlue}>· stopped by armour</color>"
                    : hit.Exits.Exists(e => !e.plate) ? " <color=" + Grey + ">· through</color>"
                    : $" <color={Orange}>· stayed in</color>");
                sb.Append('\n');
            }
        }

        private static void AppendSingle(StringBuilder sb, AutopsyProjectile p)
        {
            var hit = p.Hit;
            var entry = hit.Entry;
            var flesh = hit.Segments.Find(s => !s.HasPlate);
            var part = flesh.Collider != EBodyPartColliderType.None && hit.Segments.Exists(s => !s.HasPlate) ? flesh : entry;
            sb.Append($"<color={Grey}>{AutopsyText.Part(part.BodyPart)}:</color> {AutopsyText.Collider(part.Collider)}");
            if (!string.IsNullOrEmpty(p.From)) sb.Append($", {p.From}");
            sb.Append('\n');

            foreach (var s in hit.Segments)
            {
                if (!s.Armour.Any) continue;
                string result = s.Armour.Outcome switch
                {
                    ArmourOutcome.Penetrated => $"<color={Red}>penetrated</color>",
                    ArmourOutcome.Stopped => "<b>stopped</b>",
                    ArmourOutcome.Deflected => "<b>ricochet</b>",
                    _ => "",
                };
                sb.Append($"<color={ArmourBlue}>{s.Armour.Name}</color> <color={Grey}>(class {s.Armour.Class})</color>: {result}");
                if (s.Armour.DurabilityMax > 0f) sb.Append($" <color={Grey}>· durability {s.Armour.DurabilityBefore:F0} -> {s.Armour.DurabilityAfter:F0}</color>");
                sb.Append('\n');
            }

            float body = 0f;
            foreach (var s in hit.Segments)
            {
                if (!s.HasPlate) body += s.DamageOut;
            }
            // The same round's later-frame hits on this body (still inside) add their damage.
            foreach (var more in p.Continued)
            {
                foreach (var s in more.Segments)
                {
                    if (!s.HasPlate) body += s.DamageOut;
                }
            }
            sb.Append($"<color={Grey}>Damage</color> {entry.DamageIn:F0} -> {body:F0} <color={Grey}>to the body</color>");
            if (hit.HealthAfter.TryGetValue(part.BodyPart, out var hp) && hit.Status != HitStatus.Corpse)
            {
                sb.Append($" <color={Grey}>· {AutopsyText.Part(part.BodyPart).ToLowerInvariant()} left at</color> {Mathf.Ceil(hp.current):F0}/{hp.maximum:F0}");
            }
            sb.Append('\n');

            sb.Append($"<color={Grey}>Path:</color> ");
            if (AutopsyDiagram.StoppedByArmour(hit))
            {
                sb.Append("stopped by armour, no wound");
                return;
            }
            // Out of the flesh and into armour further on (a forearm, then the chest plate).
            var later = AutopsyDiagram.StoppedLater(hit);
            string stoppedLater = later.HasValue
                ? $" -> <color={ArmourBlue}>{(later.Value.Armour.Outcome == ArmourOutcome.Deflected ? "ricochet off" : "stopped by")} {later.Value.Armour.Name}</color>"
                : "";
            if (p.Legs3D.Count > 0)
            {
                sb.Append(LegPath(p)).Append(stoppedLater);
                return;
            }
            var path = new List<string>(4);
            foreach (var s in hit.Segments)
            {
                if (s.HasPlate) continue;
                string name = AutopsyText.Collider(s.Collider);
                if (path.Count == 0 || path[path.Count - 1] != name) path.Add(name);
            }
            sb.Append(path.Count > 0 ? string.Join(" -> ", path) : AutopsyText.Collider(entry.Collider));
            var exit = hit.Exits.Find(e => !e.plate);
            if (hit.Exits.Exists(e => !e.plate)) sb.Append($" -> <b>exit</b> {AutopsyText.Collider(exit.collider)}");
            else if (!later.HasValue) sb.Append($" -> <color={Orange}>stayed in the body</color>");
            sb.Append(stoppedLater);
        }

        /// <summary>
        /// "left forearm -> exit left forearm -> upper chest -> exit upper back": every stretch through flesh, the same
        /// round's later frames included (an exit they prove wrong, the round still inside, is dropped).
        /// </summary>
        private static string LegPath(AutopsyProjectile p)
        {
            var tokens = new List<string>(6);
            int lastExit = -1;
            foreach (var mark in p.Legs3D)
            {
                var h = mark.Hit;
                if (mark.Leg >= h.Legs.Count) continue;
                var leg = h.Legs[mark.Leg];
                if (mark.FromInside && lastExit >= 0 && lastExit == tokens.Count - 1) tokens.RemoveAt(lastExit);
                lastExit = -1;
                int end = mark.Leg + 1 < h.Legs.Count ? h.Legs[mark.Leg + 1].EntrySegment : h.Segments.Count;
                for (int i = leg.EntrySegment; i < end; i++)
                {
                    var s = h.Segments[i];
                    if (s.HasPlate) continue;
                    string name = AutopsyText.Collider(s.Collider);
                    if (tokens.Count == 0 || tokens[tokens.Count - 1] != name) tokens.Add(name);
                }
                if (leg.Exited)
                {
                    tokens.Add($"<b>exit</b> {AutopsyText.Collider(leg.ExitCollider)}");
                    lastExit = tokens.Count - 1;
                }
                else
                {
                    tokens.Add($"<color={Orange}>stayed in the body</color>");
                }
            }
            return string.Join(" -> ", tokens);
        }

        private static void AppendVolley(StringBuilder sb, AutopsyShot shot, bool fragments)
        {
            var first = shot.First;
            var byPart = new Dictionary<EBodyPart, int>();
            int stopped = 0, through = 0;
            float damageIn = 0f, body = 0f;
            foreach (var p in shot.Projectiles)
            {
                var hit = p.Hit;
                if (AutopsyDiagram.StoppedByArmour(hit)) stopped++;
                else
                {
                    var flesh = hit.Segments.Exists(s => !s.HasPlate) ? hit.Segments.Find(s => !s.HasPlate) : hit.Entry;
                    byPart.TryGetValue(flesh.BodyPart, out int n);
                    byPart[flesh.BodyPart] = n + 1;
                    if (hit.Exits.Exists(e => !e.plate)) through++;
                }
                damageIn += hit.Entry.DamageIn;
                foreach (var s in hit.Segments)
                {
                    if (!s.HasPlate) body += s.DamageOut;
                }
            }

            string unit = fragments ? "fragment" : "pellet";
            int total = shot.Projectiles.Count;
            sb.Append(fragments ? $"{total} {unit}{(total == 1 ? "" : "s")} hit"
                : $"{total} of {Mathf.Max(first.ProjectileCount, total)} {unit}s hit");
            if (byPart.Count > 0)
            {
                sb.Append(": ").Append(string.Join(", ", byPart.OrderBy(kv => (int)kv.Key)
                    .Select(kv => $"{kv.Value} {AutopsyText.Part(kv.Key).ToLowerInvariant()}")));
            }
            var from = shot.Projectiles.Find(p => !string.IsNullOrEmpty(p.From))?.From;
            if (from != null) sb.Append($", {from}");
            sb.Append('\n');
            if (stopped > 0) sb.Append($"<color={ArmourBlue}>{stopped} stopped by armour</color>  ");
            if (through > 0) sb.Append($"{through} went through  ");
            sb.Append($"<color={Grey}>Damage</color> {damageIn:F0} -> {body:F0} <color={Grey}>to the body</color>");
        }

#if !PUBLIC
        /// <summary>One-time log of the loot panel's layout (first test of the tab: what the report covers).</summary>
        private static void Dump(ComplexStashPanel panel)
        {
            var sb = new StringBuilder("[Autopsy] loot panel layout:\n");
            void Walk(Transform t, int depth)
            {
                if (depth > 4) return;
                var rect = t as RectTransform;
                var names = string.Join(",", t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name));
                sb.Append(new string(' ', depth * 2)).Append(t.name);
                if (rect != null) sb.Append($" [{rect.rect.width:F0}x{rect.rect.height:F0}]");
                sb.Append(" {").Append(names).Append("}\n");
                for (int i = 0; i < t.childCount && i < 12; i++) Walk(t.GetChild(i), depth + 1);
            }
            Walk(panel.transform, 0);
            sb.Append($"header='{panel._complexGridName?.text}' parent={panel._complexGridName?.transform.parent?.name}");
            Plugin.Log.LogInfo(sb.ToString());
        }
#endif
    }
}
