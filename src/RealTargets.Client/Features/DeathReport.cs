using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.UI;
using EFT.UI.SessionEnd;
using HarmonyLib;
using RealTargets.Autopsy;
using RealTargets.Compat;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RealTargets.Features
{
    /// <summary>
    /// The report side of the death screen (1.1.0, Stompa 2026-10-19). On EFT's raid results after a death:
    /// - an AUTOPSY button by the model (and F12 [Death screen] Autopsy key) switches the model spot to the autopsy of your
    ///   own PMC (AutopsyView in host mode: X-ray in the death pose, every shot and its shooter, cause of death, health at
    ///   death) and back to the wounded PMC (DeathScreen);
    /// - F12 [Death screen] Report picks whose report shows: ours, TraumaCore's (its `TraumaCoreHitMarkers` container under
    ///   the preview, Compat.TraumaCore) or both. TraumaCore's is only hidden while ours has something to show;
    /// - the model turns with a left-drag (TraumaCore's own drag lives in the container we hide).
    /// The X-ray needs the body, which is gone by the results screen: it is baked from your corpse in the raid, a moment
    /// after your death (once the hits on you have settled), with your head or limbs collapsed where our rules took them.
    /// </summary>
    internal sealed class DeathReport : MonoBehaviour
    {
        private const int MaxSnapshots = 4;

        internal static DeathReport Instance { get; private set; }

        // ---- The X-ray snapshot (raid) -----------------------------------------------------------
        private static AutopsyModel _model;
        private static int _snapshots, _snapVersion = -1, _seenVersion = -1;
        private static float _seenAt, _snapAt;

        // ---- The results screen -------------------------------------------------------------------
        private SessionResultExitStatus _screen;
        private Profile _profile;
        private ESideType _side;
        private bool _killed;
        private bool _tcRan;
        private bool _tcLook;
        private bool _bodyLabelShown;
        private float _lookUntil, _nextLook;
        private RawImage _preview;
        private GameObject _tcMarkers;
        private RectTransform _drag;
        private RectTransform _area;
        private AutopsyView _view;
        private GameObject _button;
        private Image _buttonBack;
        private TextMeshProUGUI _buttonText;
        private bool _showing;
        private bool _failed;
        private readonly Vector3[] _corners = new Vector3[4];

        private static readonly Color TabBack = new Color(0.10f, 0.105f, 0.10f, 0.92f);
        private static readonly Color TabBackSelected = new Color(0.82f, 0.82f, 0.77f, 1f);
        private static readonly Color TabText = new Color(0.78f, 0.78f, 0.74f, 1f);
        private static readonly Color TabTextSelected = new Color(0.08f, 0.08f, 0.08f, 1f);

        private void Awake()
        {
            Instance = this;
        }

        internal static void Enable()
        {
            new ShowPatch().Enable();
        }

        /// <summary>A new raid (DeathScreen.RaidStarted): the last raid's X-ray goes.</summary>
        internal static void RaidStarted()
        {
            _model?.Dispose();
            _model = null;
            _snapshots = 0;
            _snapVersion = -1;
            _seenVersion = -1;
        }

        // ---- Snapshot -----------------------------------------------------------------------------

        private void Update()
        {
            if (!Singleton<GameWorld>.Instantiated || _snapshots >= MaxSnapshots) return;
            var record = AutopsyRecords.Local;
            if (record == null || !record.Dead || record.Version == _snapVersion || record.Player == null || record.Pose == null) return;
            float now = Time.time;
            if (record.Version != _seenVersion)
            {
                _seenVersion = record.Version;
                _seenAt = now;
            }
            // Hits on you are published 50 ms+ after they land: wait until they settle (bots shooting your corpse: at
            // most every 1.5 s, at most MaxSnapshots times).
            bool settled = now - _seenAt >= 0.4f;
            bool overdue = now - (_snapshots == 0 ? record.DiedTime : _snapAt) >= 1.5f;
            if (settled || overdue) Snapshot(record);
        }

        private static void Snapshot(BodyRecord record)
        {
            _snapshots++;
            _snapAt = Time.time;
            _snapVersion = record.Version;
            var collapsed = new List<(Transform bone, Vector3 scale)>();
            try
            {
                // Our rules took these at your death (nothing changed on you in the raid): collapse them for the bake, as
                // Dismemberment does on bots, so the X-ray and its anatomy show them gone.
                var gone = DeathScreen.GoneBones();
                var bones = record.Player.PlayerBones;
                var root = bones == null ? null : bones.RootJoint != null ? bones.RootJoint : bones.Pelvis?.Original;
                if (gone.Count > 0 && root != null)
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (!gone.Contains(t.name)) continue;
                        collapsed.Add((t, t.localScale));
                        t.localScale = Vector3.one * 0.001f;
                    }
                }
                _model ??= new AutopsyModel();
                if (!_model.Snapshot(record, record.Shots))
                {
                    Plugin.Log.LogWarning("[Death screen] X-ray of your PMC not made (no pose or body skins): the autopsy uses the 2D chart");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Death screen] X-ray of your PMC failed, the autopsy uses the 2D chart: {ex}");
                _model?.Dispose();
                _model = null;
                _snapshots = MaxSnapshots;
            }
            finally
            {
                foreach (var (bone, scale) in collapsed)
                {
                    if (bone != null) bone.localScale = scale;
                }
            }
        }

        // ---- The results screen -------------------------------------------------------------------

        private sealed class ShowPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SessionResultExitStatus), nameof(SessionResultExitStatus.Show),
                new[] { typeof(Profile), typeof(PlayerVisualRepresentation), typeof(ESideType), typeof(ExitStatus), typeof(TimeSpan), typeof(IEftSession), typeof(bool) });

            [PatchPostfix]
            private static void Postfix(SessionResultExitStatus __instance, Profile activeProfile, ESideType side, ExitStatus exitStatus)
            {
                try
                {
                    Instance?.Begin(__instance, activeProfile, side, exitStatus);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Death screen] report: {ex.Message}");
                }
            }
        }

        private void Begin(SessionResultExitStatus screen, Profile profile, ESideType side, ExitStatus status)
        {
            Clear();
            _failed = false;
            _screen = screen;
            _profile = profile;
            // The argument as TraumaCore's postfix sees it (its name rule is PMC only).
            _side = side;
            _killed = status == ExitStatus.Killed;
            // TraumaCore's postfix ran on this Show (before or after ours): it moved the level into the name.
            _tcRan = TraumaCore.ReportOn && (status == ExitStatus.Killed || status == ExitStatus.MissingInAction);
            _tcLook = _tcRan;
            // EFT's own choice for the "(thorax)" label; TraumaCore hides it later, from its coroutine.
            _bodyLabelShown = screen._bodyPartLabel != null && screen._bodyPartLabel.gameObject.activeSelf;
            _lookUntil = Time.realtimeSinceStartup + 20f;
            _nextLook = 0f;
        }

        /// <summary>Our objects from an earlier show go (the screen object is reused).</summary>
        private void Clear()
        {
            _showing = false;
            if (_area != null) Destroy(_area.gameObject);
            if (_drag != null) Destroy(_drag.gameObject);
            if (_button != null) Destroy(_button);
            _area = null;
            _view = null;
            _drag = null;
            _button = null;
            _preview = null;
            _tcMarkers = null;
            _screen = null;
        }

        private void LateUpdate()
        {
            if (_screen == null || _failed) return;
            if (!_screen.gameObject.activeInHierarchy) return;
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                _failed = true;
                Plugin.Log.LogWarning($"[Death screen] report off for this screen: {ex}");
            }
        }

        private void Tick()
        {
            if (_preview == null)
            {
                float now = Time.realtimeSinceStartup;
                if (now > _lookUntil)
                {
                    Plugin.Log.LogWarning("[Death screen] no model preview found on the raid results: no AUTOPSY switch");
                    _failed = true;
                    return;
                }
                var modelView = _screen._playerModelView;
                if (now < _nextLook || modelView == null || !modelView.LoadingComplete || modelView.PlayerBody == null) return;
                _nextLook = now + 0.25f;
                _preview = FindPreview(modelView);
                if (_preview == null) return;
                BuildDrag(modelView);
#if !PUBLIC
                LogLayout();
#endif
            }

            var mode = Plugin.DeathScreenReport.Value;
            var record = AutopsyRecords.Local;
            bool ours = _killed && Plugin.AutopsyEnabled.Value && record != null && record.Dead;

            // TraumaCore's report: hidden while ours is picked and has something to show.
            bool tcVisible = !(ours && mode == DeathReportMode.RealTargets);
            if (_tcRan)
            {
                if (_tcMarkers == null)
                {
                    var t = _preview.transform.Find(TraumaCore.MarkersName);
                    if (t != null) _tcMarkers = t.gameObject;
                }
                if (_tcMarkers != null && _tcMarkers.activeSelf != tcVisible) _tcMarkers.SetActive(tcVisible);
                Look(tcVisible);
            }

            // Our AUTOPSY switch.
            bool offer = ours && mode != DeathReportMode.TraumaCore;
            if (offer && _button == null) BuildButton();
            if (_button != null && _button.activeSelf != offer) _button.SetActive(offer);
            if (!offer)
            {
                if (_showing) SetAutopsy(false);
                return;
            }
            if (Plugin.DeathScreenAutopsyKey.Value.IsDown()) SetAutopsy(!_showing);
            Place();
        }

        /// <summary>
        /// The name, level and "(thorax)" label as TraumaCore leaves them (level in the name, label hidden once its markers
        /// are up) or as EFT draws them, so switching Report live puts the screen back the way each one had it.
        /// </summary>
        private void Look(bool traumaCore)
        {
            if (traumaCore != _tcLook)
            {
                _tcLook = traumaCore;
                if (_screen._levelPanel != null) _screen._levelPanel.gameObject.SetActive(!traumaCore);
                var name = _screen._namePanel;
                if (name != null && _profile != null)
                {
                    if (!traumaCore) name.Set(_profile);
                    else if (_side == ESideType.Pmc && name._name != null && _profile.Info != null)
                        name._name.text = $"{_profile.GetCorrectedNickname()} (Lv. {_profile.Info.Level})";
                }
            }
            var label = _screen._bodyPartLabel;
            if (label == null) return;
            bool shown = _bodyLabelShown && !(traumaCore && _tcMarkers != null);
            if (label.gameObject.activeSelf != shown) label.gameObject.SetActive(shown);
        }

        private void SetAutopsy(bool on)
        {
            _showing = on;
            if (_buttonBack != null) _buttonBack.color = on ? TabBackSelected : TabBack;
            if (_buttonText != null) _buttonText.color = on ? TabTextSelected : TabText;
            SetButtonText();
            if (!on)
            {
                _view?.HideHosted();
                if (_area != null) _area.gameObject.SetActive(false);
                return;
            }
            if (_area == null)
            {
                var go = new GameObject("RealTargets.DeathAutopsy", typeof(RectTransform));
                _area = (RectTransform)go.transform;
                _area.SetParent(_preview.transform.parent, false);
                go.AddComponent<LayoutElement>().ignoreLayout = true;
                Solid(go);
                Place();
                _view = AutopsyView.CreateHosted(_area, (TMP_Text)_screen._bodyPartLabel ?? _screen._raidTime);
            }
            _area.gameObject.SetActive(true);
            _area.SetAsLastSibling();
            if (_button != null) _button.transform.SetAsLastSibling();
            Place();
            var record = AutopsyRecords.Local;
            _view.ShowHosted(record, _model != null && _model.Ready ? _model : null);
            Plugin.Log.LogInfo($"[Death screen] autopsy of your PMC: {record?.Shots.Count ?? 0} shot(s), " +
                               $"{(_model != null && _model.Ready ? $"X-ray ({_snapshots} snapshot(s))" : "2D chart (no X-ray)")}");
        }

        // ---- Layout -------------------------------------------------------------------------------

        /// <summary>
        /// The RawImage showing the model: the image whose render texture a camera that sees the model draws into (as
        /// TraumaCore finds it), else the largest image showing a render texture next to the model view.
        /// </summary>
        private static RawImage FindPreview(PlayerModelView view)
        {
            var parent = view.transform.parent;
            if (parent == null) return null;
            var images = parent.GetComponentsInChildren<RawImage>(true);
            var body = view.PlayerBody.transform;
            var probe = body.position + body.up * 1.1f;
            foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (camera == null || camera.targetTexture == null || !camera.gameObject.scene.IsValid()) continue;
                var at = camera.WorldToViewportPoint(probe);
                if (at.z <= 0f || at.x < 0f || at.x > 1f || at.y < 0f || at.y > 1f) continue;
                foreach (var image in images)
                {
                    if (image != null && image.texture == camera.targetTexture) return image;
                }
            }
            RawImage best = null;
            float area = 0f;
            foreach (var image in images)
            {
                if (image == null || !(image.texture is RenderTexture) || !image.gameObject.activeInHierarchy) continue;
                var r = image.rectTransform.rect;
                if (r.width * r.height <= area) continue;
                area = r.width * r.height;
                best = image;
            }
            return best;
        }

        private const float ReportWidth = 900f;

        /// <summary>
        /// The report's area (release check 4, Stompa: wider and longer than the model's 458x812): ~900 wide centred on the
        /// model (the vitals then sit beside the X-ray), at most 60 % of the screen; from the model's top down over the
        /// "lost all items" banner to just above EFT's NEXT button, which stays clickable; on screen.
        /// </summary>
        private Rect AreaRect(RectTransform parent)
        {
            var preview = Bounds(_preview.rectTransform, parent);
            var screen = Bounds((RectTransform)_screen.transform, parent);
            float width = Mathf.Clamp(Mathf.Max(preview.width, ReportWidth), Mathf.Min(440f, screen.width * 0.6f), screen.width * 0.6f);
            float top = Mathf.Min(preview.yMax, screen.yMax);
            float bottom = preview.yMin;
            var next = _screen._nextButton != null ? _screen._nextButton.transform as RectTransform : null;
            if (next != null && next.gameObject.activeInHierarchy)
            {
                var button = Bounds(next, parent);
                if (button.yMax + 12f < bottom) bottom = button.yMax + 12f;
            }
            bottom = Mathf.Max(bottom, top - screen.height * 0.92f);
            var rect = Rect.MinMaxRect(preview.center.x - width * 0.5f, bottom, preview.center.x + width * 0.5f, top);
            rect.x = Mathf.Clamp(rect.x, screen.xMin, Mathf.Max(screen.xMin, screen.xMax - rect.width));
            rect.y = Mathf.Clamp(rect.y, screen.yMin, Mathf.Max(screen.yMin, screen.yMax - rect.height));
            return rect;
        }

        /// <summary>The model itself (left-drag turns it): the preview, on screen. Never the report's wider area.</summary>
        private Rect ModelRect(RectTransform parent)
        {
            var preview = Bounds(_preview.rectTransform, parent);
            var screen = Bounds((RectTransform)_screen.transform, parent);
            return Rect.MinMaxRect(Mathf.Max(preview.xMin, screen.xMin), Mathf.Max(preview.yMin, screen.yMin),
                Mathf.Min(preview.xMax, screen.xMax), Mathf.Min(preview.yMax, screen.yMax));
        }

        private Rect Bounds(RectTransform rect, RectTransform space)
        {
            rect.GetWorldCorners(_corners);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            foreach (var c in _corners)
            {
                Vector2 local = space.InverseTransformPoint(c);
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static void Put(RectTransform rect, Rect at, RectTransform parent)
        {
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = at.min - parent.rect.min;
            rect.sizeDelta = at.size;
        }

        /// <summary>
        /// Follows the preview (resizes): the drag area over the model, the report over its wider area, the button under
        /// EFT's level panel (Stompa, release check 4), else at the report's top left.
        /// </summary>
        private void Place()
        {
            if (_preview == null) return;
            var parent = (RectTransform)_preview.transform.parent;
            var area = AreaRect(parent);
            if (_drag != null) Put(_drag, ModelRect(parent), parent);
            if (_area != null && _area.gameObject.activeSelf) Put(_area, area, parent);
            if (_button != null)
            {
                float width = Mathf.Max(_buttonText.preferredWidth + 24f, 110f);
                const float height = 28f;
                var at = new Rect(area.xMin + 10f, area.yMax - height - 10f, width, height);
                // Under the level only while the PMC shows: the level sits over the model, inside the report's area, so
                // BACK TO PMC stays at the report's top left (its text starts below it).
                var level = !_showing && _screen._levelPanel != null ? _screen._levelPanel.transform as RectTransform : null;
                if (level != null)
                {
                    var panel = Bounds(level, parent);
                    if (panel.width > 1f && panel.height > 1f) at = new Rect(panel.xMin, panel.yMin - height - 8f, width, height);
                }
                Put((RectTransform)_button.transform, at, parent);
            }
        }

        private void BuildDrag(PlayerModelView modelView)
        {
            var go = new GameObject("RealTargets.ModelDrag", typeof(RectTransform));
            _drag = (RectTransform)go.transform;
            _drag.SetParent(_preview.transform.parent, false);
            _drag.SetSiblingIndex(_preview.transform.GetSiblingIndex() + 1);
            go.AddComponent<LayoutElement>().ignoreLayout = true;
            go.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            go.AddComponent<TurnModel>().View = modelView;
            Place();
        }

        private void BuildButton()
        {
            _button = new GameObject("RealTargets.AutopsySwitch", typeof(RectTransform));
            var rect = (RectTransform)_button.transform;
            rect.SetParent(_preview.transform.parent, false);
            _button.AddComponent<LayoutElement>().ignoreLayout = true;
            Solid(_button);
            _buttonBack = _button.AddComponent<Image>();
            _buttonBack.color = TabBack;
            var button = _button.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => SetAutopsy(!_showing));

            var label = new GameObject("Text", typeof(RectTransform));
            var labelRect = (RectTransform)label.transform;
            labelRect.SetParent(rect, false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            _buttonText = label.AddComponent<TextMeshProUGUI>();
            TMP_Text font = (TMP_Text)_screen._bodyPartLabel ?? _screen._raidTime;
            if (font != null && font.font != null)
            {
                _buttonText.font = font.font;
                _buttonText.fontSharedMaterial = font.fontSharedMaterial;
            }
            _buttonText.fontSize = 14f;
            _buttonText.enableWordWrapping = false;
            _buttonText.alignment = TextAlignmentOptions.Center;
            _buttonText.raycastTarget = false;
            _buttonText.richText = true;
            _buttonText.color = TabText;
            SetButtonText();
            rect.SetAsLastSibling();
        }

        /// <summary>
        /// Full opacity whatever the screen's own fade does (release check 3: the level number and name behind the report
        /// showed through it while the level emblem beside it was at full brightness).
        /// </summary>
        private static void Solid(GameObject go)
        {
            var group = go.AddComponent<CanvasGroup>();
            group.ignoreParentGroups = true;
            group.alpha = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;
        }

        private void SetButtonText()
        {
            if (_buttonText == null) return;
            var key = Plugin.DeathScreenAutopsyKey.Value;
            string hint = key.MainKey == KeyCode.None ? "" : $"  <size=11>[{key}]</size>";
            _buttonText.text = (_showing ? "<b>BACK TO PMC</b>" : "<b>AUTOPSY</b>") + hint;
        }

        /// <summary>Left-drag turns the model about its vertical axis (EFT's own model screens do the same).</summary>
        private sealed class TurnModel : MonoBehaviour, IBeginDragHandler, IDragHandler
        {
            public PlayerModelView View;
            public void OnBeginDrag(PointerEventData eventData) { }
            public void OnDrag(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left) return;
                var poser = View != null ? View.ModelPlayerPoser : null;
                if (poser == null) return;
                var t = poser.transform;
                t.localRotation = Quaternion.AngleAxis(-eventData.delta.x * 0.3f, Vector3.up) * t.localRotation;
            }
        }

#if !PUBLIC
        private void LogLayout()
        {
            var parent = (RectTransform)_preview.transform.parent;
            var preview = Bounds(_preview.rectTransform, parent);
            var screen = Bounds((RectTransform)_screen.transform, parent);
            Plugin.Log.LogInfo($"[Death screen] preview '{_preview.name}' {preview.width:F0}x{preview.height:F0} under '{parent.name}', " +
                               $"screen {screen.width:F0}x{screen.height:F0}, report area {AreaRect(parent).size}, " +
                               $"screen fade {(parent.GetComponentInParent<CanvasGroup>() is CanvasGroup g ? g.alpha.ToString("F2") : "none")}; TraumaCore report " +
                               $"{(_tcRan ? "drawn" : TraumaCore.Installed ? "off" : "not installed")}");
        }
#endif
    }
}
