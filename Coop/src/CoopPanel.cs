using System;
using System.Collections.Generic;
using Blukulele.CHE;
using Blukulele.Core;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gambonanza.Coop
{
    /// <summary>
    /// The co-op panel, built out of the game's own menu parts rather than a repurposed
    /// Settings window: the frame is a clone of the menu Container, and every button is a
    /// clone of a real menu button, so hover, press, sound and styling are the genuine
    /// article instead of an approximation.
    /// </summary>
    internal sealed class CoopPanel
    {
        private GameObject _root;
        private Transform _column;
        private TMP_Text _title, _seat, _peer, _hint;
        private readonly List<CoopNativeButton> _buttons = new List<CoopNativeButton>();

        public bool IsOpen => _root != null && _root.activeSelf;
        public GameObject Root => _root;

        public CoopNativeButton Host { get; private set; }
        public CoopNativeButton Invite { get; private set; }
        public CoopNativeButton Start { get; private set; }
        public CoopNativeButton Leave { get; private set; }
        public CoopNativeButton Close { get; private set; }

        /// <summary>Builds the panel if it does not exist yet. Returns false if the menu parts aren't available.</summary>
        public bool Ensure(Action onHost, Action onInvite, Action onStart, Action onLeave, Action onClose)
        {
            if (_root != null) return true;

            var parts = CoopMenuParts.Find();
            if (parts == null) return false;

            var canvas = parts.ButtonCell.GetComponentInParent<Canvas>();
            if (canvas == null) return false;

            // ---- full-screen backdrop that swallows clicks behind the panel ----
            _root = new GameObject("__CoopPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _root.transform.SetParent(canvas.transform, false);
            var rootRt = (RectTransform)_root.transform;
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;
            _root.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
            _root.transform.SetAsLastSibling();

            // ---- the frame: a clone of the menu's own container, emptied ----
            var frameGo = UnityEngine.Object.Instantiate(parts.Container.gameObject, _root.transform);
            frameGo.name = "Frame";
            // DestroyImmediate throughout: Destroy is deferred to end of frame, and a surviving
            // HorizontalLayoutGroup makes AddComponent<VerticalLayoutGroup> return null
            // (LayoutGroup is DisallowMultipleComponent) - which NRE'd the whole panel.
            for (int i = frameGo.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(frameGo.transform.GetChild(i).gameObject);
            StripLayout(frameGo);

            var frameRt = (RectTransform)frameGo.transform;
            frameRt.anchorMin = new Vector2(0.5f, 0.5f);
            frameRt.anchorMax = new Vector2(0.5f, 0.5f);
            frameRt.pivot = new Vector2(0.5f, 0.5f);
            frameRt.anchoredPosition = Vector2.zero;
            frameRt.sizeDelta = new Vector2(680f, 700f);

            // No layout group: the cloned cells lose their intrinsic size once the content
            // fitters are stripped, so a layout group has nothing to work with and flings them
            // outside the frame. Everything below is positioned explicitly instead.
            _column = frameGo.transform;

            // ---- content ----
            _title = CloneLabel(parts, "CO-OP", 50f, 290f);
            _seat  = CloneLabel(parts, "", 34f, 215f);
            _peer  = CloneLabel(parts, "", 24f, 163f);
            _hint  = CloneLabel(parts, "", 21f, 116f);

            Host   = AddButton(parts, "Host a game", onHost);
            Invite = AddButton(parts, "Invite a friend", onInvite);
            Start  = AddButton(parts, "Start the run", onStart);
            Leave  = AddButton(parts, "Leave", onLeave);
            Close  = AddButton(parts, "Close", onClose);

            _root.SetActive(false);
            return true;
        }

        private static void StripLayout(GameObject go)
        {
            foreach (var l in go.GetComponents<LayoutGroup>()) UnityEngine.Object.DestroyImmediate(l);
            foreach (var f in go.GetComponents<ContentSizeFitter>()) UnityEngine.Object.DestroyImmediate(f);
            foreach (var r in go.GetComponents<MonoBehaviour>())
                if (r != null && r.GetType().Name == "AutoLayoutRebuilder") UnityEngine.Object.DestroyImmediate(r);
        }

        private TMP_Text CloneLabel(CoopMenuParts parts, string text, float size, float y)
        {
            var go = UnityEngine.Object.Instantiate(parts.Label.gameObject, _column);
            go.name = "Label";
            foreach (var f in go.GetComponents<ContentSizeFitter>()) UnityEngine.Object.DestroyImmediate(f);
            foreach (var l in go.GetComponents<LayoutElement>()) UnityEngine.Object.DestroyImmediate(l);

            var t = go.GetComponent<TMP_Text>();
            if (t == null) return null;
            t.text = text;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.enableAutoSizing = false;
            t.enableWordWrapping = true;

            Place((RectTransform)go.transform, new Vector2(540f, size * 1.7f), y);
            return t;
        }

        private CoopNativeButton AddButton(CoopMenuParts parts, string label, Action onClick)
        {
            var cell = UnityEngine.Object.Instantiate(parts.ButtonCell.gameObject, _column);
            cell.name = "BTN_" + label;
            cell.SetActive(true);

            Place((RectTransform)cell.transform, new Vector2(420f, 74f), 0f);

            // The inner button keeps its own content fitter so it sizes to its label; just
            // make sure it sits centred in the cell rather than wherever the menu row put it.
            var inner = cell.transform.childCount > 0 ? (RectTransform)cell.transform.GetChild(0) : null;
            if (inner != null)
            {
                inner.anchorMin = new Vector2(0.5f, 0.5f);
                inner.anchorMax = new Vector2(0.5f, 0.5f);
                inner.pivot = new Vector2(0.5f, 0.5f);
                inner.anchoredPosition = Vector2.zero;
            }

            var btn = CoopNativeButton.Attach(cell, label, onClick, fontSize: 32f);
            _buttons.Add(btn);
            return btn;
        }

        private static void Place(RectTransform rt, Vector2 size, float y)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size;
            rt.anchoredPosition = new Vector2(0f, y);
        }

        /// <summary>
        /// Stacks whichever action buttons are visible, centred in the lower half of the
        /// panel with generous gaps - never a glued-together block.
        /// </summary>
        private void LayoutButtons()
        {
            const float areaCenter = -110f, step = 108f;

            var visible = new List<CoopNativeButton>();
            foreach (var b in _buttons)
                if (b != null && b.gameObject != null && b.gameObject.activeSelf)
                    visible.Add(b);

            float first = areaCenter + (visible.Count - 1) * step / 2f;
            for (int i = 0; i < visible.Count; i++)
                ((RectTransform)visible[i].transform).anchoredPosition = new Vector2(0f, first - i * step);
        }

        public void Show()
        {
            if (_root == null) return;
            _root.transform.SetAsLastSibling();
            _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        public void SetTexts(string seat, Color seatColor, string peer, string hint)
        {
            SetTextsInternal(seat, seatColor, peer, hint);
        }

        private void SetTextsInternal(string seat, Color seatColor, string peer, string hint)
        {
            if (_seat != null) { _seat.text = seat; _seat.color = seatColor; }
            if (_peer != null) _peer.text = peer;
            if (_hint != null) _hint.text = hint;
            LayoutButtons();
        }

        public void Teardown()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
            _buttons.Clear();
            Host = Invite = Start = Leave = Close = null;
        }
    }

    /// <summary>Locates the reusable pieces of the home menu: a button cell, a label, a frame.</summary>
    internal sealed class CoopMenuParts
    {
        public Transform ButtonCell;   // BTN_Container: the cell holding a real menu button
        public TMP_Text Label;         // a real menu label, for font/material
        public Transform Container;    // the dark rounded frame

        public static CoopMenuParts Find()
        {
            var menu = UnityEngine.Object.FindAnyObjectByType<CanvasMenu>();
            if (menu == null) return null;

            var settings = GameRefl.GetField(menu, "m_Text_Settings") as TMP_Text;
            if (settings == null) return null;

            var button = settings.transform.parent;            // BTN_Settings
            if (button == null) return null;
            var cell = button.parent;                          // BTN_Container
            if (cell == null) return null;
            var container = cell.parent;                       // Container (framed row)
            if (container == null) return null;

            return new CoopMenuParts { ButtonCell = cell, Label = settings, Container = container };
        }
    }

    /// <summary>
    /// A cloned menu button, rewired. This game wires every menu button through an
    /// EventTrigger whose entries drive RotationButton (hover: scale 1.1 over 0.2s plus the
    /// UI_MouseOver sound) and ShadowButton (the press). Destroying that wiring and
    /// reimplementing it produced a button that neither hovered nor clicked, so instead we
    /// keep all of it and only switch off the persistent listeners that point outside the
    /// clone - i.e. the original "open Settings" action - then add our own click listener.
    /// </summary>
    internal sealed class CoopNativeButton : MonoBehaviour
    {
        private Action _onClick;

        public static CoopNativeButton Attach(GameObject cell, string label, Action onClick, float fontSize = -1f)
        {
            var self = cell.GetComponent<CoopNativeButton>() ?? cell.AddComponent<CoopNativeButton>();
            self._onClick = onClick;

            var tmp = cell.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null)
            {
                tmp.text = label;
                tmp.enableAutoSizing = false;
                // The menu label carries a big serialized font size; left alone it (and the
                // box content-fitted around it) dwarfs a panel row.
                if (fontSize > 0f) tmp.fontSize = fontSize;
            }

            int muted = 0;
            EventTrigger.Entry clickEntry = null;

            foreach (var trigger in cell.GetComponentsInChildren<EventTrigger>(true))
            {
                if (trigger.triggers == null) continue;
                foreach (var entry in trigger.triggers)
                {
                    if (entry == null || entry.callback == null) continue;

                    // Silence anything targeting an object outside this clone (the menu's own
                    // action); keep listeners aimed at the clone's own hover/press components.
                    for (int i = 0; i < entry.callback.GetPersistentEventCount(); i++)
                    {
                        var target = entry.callback.GetPersistentTarget(i) as Component;
                        bool insideClone = target != null && target.transform.IsChildOf(cell.transform);
                        if (!insideClone)
                        {
                            entry.callback.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
                            muted++;
                        }
                    }

                    if (entry.eventID == EventTriggerType.PointerClick) clickEntry = entry;
                }

                if (clickEntry == null)
                {
                    clickEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                    trigger.triggers.Add(clickEntry);
                }
                clickEntry.callback.AddListener(_ => self.Fire());
                break;   // one EventTrigger is enough
            }

            if (clickEntry == null)
                CoopLog.Warn($"button '{label}': no EventTrigger on the cloned cell - clicks will not register.");
            else
                CoopLog.Debug($"button '{label}': rewired ({muted} original listener(s) muted)");

            return self;
        }

        private void Fire()
        {
            try { _onClick?.Invoke(); }
            catch (Exception ex) { CoopLog.Error($"button '{name}' threw: {ex.Message}"); }
        }

        /// <summary>Hides rather than greys out - a disabled-looking button here reads as broken.</summary>
        public void SetVisible(bool on)
        {
            if (gameObject.activeSelf != on) gameObject.SetActive(on);
        }

        public void SetLabel(string text)
        {
            var tmp = GetComponentInChildren<TMP_Text>(true);
            if (tmp != null) tmp.text = text;
        }
    }
}
