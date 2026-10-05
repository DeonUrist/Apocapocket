using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.UI;

namespace Apocapocket
{
    /// Slots 4..6 next to the game's three weapon slots.
    ///  - Holders are runtime custody locations. Fixed 1.x reference IDs are retained for one migration release only;
    ///    version 2 normalises pocketed objects to world items before serialization and never saves these parents.
    ///  - UI: slots stay in numerical order while the card's right edge stays fixed beside the ammo/value panel.
    ///    Each extra slot extends the card to the left and moves the slot row and left-side hints by one native pitch.
    ///    End caps remain intact and the middle texture repeats at its original scale.
    internal static class ExtraSlots
    {
        internal const int Max = 3;
        // Arbitrary fixed Easy Save reference IDs (GameObject, Transform) per holder. Never change once released.
        private static readonly long[] GoIds = { 7431505000000000401L, 7431505000000000501L, 7431505000000000601L };
        private static readonly long[] TrIds = { 7431505000000000402L, 7431505000000000502L, 7431505000000000602L };

        internal static readonly Transform[] Holders = new Transform[Max];
        internal static Transform Staging;
        private static readonly RawImage[] _icons = new RawImage[Max];
        private static readonly Graphic[] _frames = new Graphic[Max];
        private static readonly Outline[] _outlines = new Outline[Max];
        private static readonly List<GameObject>[] _uiRoots = { new List<GameObject>(), new List<GameObject>(), new List<GameObject>() };
        private static Texture _emptyTex;
        private static Color _emptyColor = Color.white, _fullColor = new Color(1f, 0.985f, 0f, 1f);
        private static bool _uiFailed;
        private static float _nextUiTry;

        /// Number of extra slots currently usable (config), 0..3.
        internal static int Active { get { return Mathf.Min(Mathf.Clamp(Plugin.ExtraSlotCount.Value, 0, Max), Unlocked); } }
        /// Slots unlocked by the worn backpack (set by the Runner every frame; 3 when no backpack is required).
        internal static int Unlocked = 3;

        // ------------------------------------------------------------------ holders
        internal static void EnsureHolders(Transform playerCamera)
        {
            if (playerCamera == null) return;
            var container = playerCamera.Find("Apocapocket_ExtraSlots");
            if (container == null)
            {
                container = new GameObject("Apocapocket_ExtraSlots").transform;
                container.SetParent(playerCamera, false);
            }
            for (int i = 0; i < Max; i++)
            {
                var h = container.Find("Slot " + (i + 4));
                if (h == null)
                {
                    h = new GameObject("Slot " + (i + 4)).transform;
                    h.SetParent(container, false);
                }
                Holders[i] = h;
                // Resolve 1.x parents during migration only. New saves always normalise these items to the world.
                Register(h, i);
            }
            Staging = container.Find("Staging");
            if (Staging == null)
            {
                Staging = new GameObject("Staging").transform;
                Staging.SetParent(container, false);
            }
        }

        private static void Register(Transform h, int i)
        {
            try
            {
                var mgr = ES3Internal.ES3ReferenceMgrBase.Current;
                if (mgr == null) return;
                long gid = mgr.Get(h.gameObject), tid = mgr.Get(h);
                if (gid != GoIds[i]) mgr.Add(h.gameObject, GoIds[i]);
                if (tid != TrIds[i]) mgr.Add(h, TrIds[i]);
                Plugin.V("Slot " + (i + 4) + " holder registered with Easy Save (go " + mgr.Get(h.gameObject) + ", transform " + mgr.Get(h) + ")");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Easy Save registration of slot " + (i + 4) + " failed: " + e.GetType().Name + ": " + e.Message); }
        }

        // ------------------------------------------------------------------ UI
        /// Clone slot 3's widget three times (once per scene; retried every 2 s until the game's UI exists).
        internal static void EnsureUi(Transform slot2, Transform slot3)
        {
            if (_icons[0] != null || _uiFailed) return;
            if (Time.unscaledTime < _nextUiTry) return;
            _nextUiTry = Time.unscaledTime + 2f;
            try { BuildUi(slot2, slot3); }
            catch (Exception e) { _uiFailed = true; Plugin.Log.LogWarning("Extra slot UI could not be built: " + e); }
        }

        internal static void ResetUi()
        {
            HideAll();
            if (_bgExtension != null) UnityEngine.Object.Destroy(_bgExtension.gameObject);
            _bgExtension = null; _bgGraphic = null; _bgRect = null;
            foreach (var part in _decorations) if (part.Clone != null) UnityEngine.Object.Destroy(part.Clone);
            _decorations.Clear(); _shifted.Clear(); _uiFailed = false; _nextUiTry = 0f;
            for (int i = 0; i < 3; i++)
            {
                _gameImg[i] = null; _gameFrame[i] = null; _gameOutline[i] = null;
                foreach (var go in _uiRoots[i]) if (go != null) UnityEngine.Object.Destroy(go);
                _icons[i] = null; _frames[i] = null; _outlines[i] = null; _uiRoots[i].Clear();
            }
        }

        private static void BuildUi(Transform slot2, Transform slot3)
        {
            // Read what each game slot's SlotEmptyFull FSM writes: icon RawImage, number label (frame), colours, empty texture.
            var slotObjs = new[] { slot3.parent != null ? slot3.parent.Find("Slot 1") : null, slot2, slot3 };
            var img = new GameObject[3]; var frame = new GameObject[3];
            for (int k = 0; k < 3; k++)
            {
                if (slotObjs[k] == null) return;
                Texture et; Color ec, fc;
                if (!ReadSlotUi(slotObjs[k], out img[k], out frame[k], out et, out ec, out fc)) return;
                if (img[k] == null || frame[k] == null) return;
                if (k == 2) { _emptyTex = et; _emptyColor = ec; _fullColor = fc; }
            }
            var parent = img[0].transform.parent;
            for (int k = 0; k < 3; k++) { _gameImg[k] = img[k].GetComponent<RawImage>(); _gameFrame[k] = frame[k].GetComponent<Graphic>(); _gameOutline[k] = frame[k].GetComponent<Outline>(); }
            DumpOnce(parent);

            // Continue the native slot pitch: 4 follows 3, independent of the card's edge padding.
            Vector3 c1 = Center(img[0].transform), c3 = Center(img[2].transform);
            var bg = FindBackground(parent, c1, c3);
            float pitch = Mathf.Abs(c3.x - c1.x) * 0.5f;
            if (pitch <= 0f) return;
            float worldShift = pitch * 3f;

            if (bg != null)
            {
                DumpChildren(bg.parent);
                var cardRect = WorldRect(bg);
                BuildBackground(bg, cardRect, img[0].transform, img[2].transform, pitch);
                // Copy per-slot decorations only. The card itself remains the original UI element.
                var sources = new List<Transform>();
                foreach (Transform sib in bg.parent)
                {
                    if (sib == bg || sib.name.EndsWith("_apocapocket") || sib == parent || parent.IsChildOf(sib)) continue;
                    if (sib.GetComponentInChildren<Graphic>(true) == null) continue;
                    var r = WorldRect(sib);
                    if (r.width <= 0f || r.width * r.height >= cardRect.width * cardRect.height) continue;
                    if (!cardRect.Contains(r.center)) continue;
                    sources.Add(sib);
                }
                foreach (var sib in sources)
                {
                    var r = WorldRect(sib);
                    int slot = Mathf.Clamp(Mathf.CeilToInt((r.center.x - c1.x) / pitch - 0.1f), 0, 2);
                    var clone = CloneWidget(sib.gameObject, worldShift);
                    _decorations.Add(new CardDecoration { Source = sib.gameObject, Clone = clone, Slot = slot });
                    TrackShift(sib, pitch);
                    TrackShift(clone.transform, pitch);
                }
            }

            for (int i = 0; i < Max; i++)
            {
                int k = i;   // slot 4 <- widgets of slot 1, 5 <- 2, 6 <- 3
                string from = (k + 1).ToString(), to = (k + 4).ToString();
                foreach (var srcGo in new[] { img[k], frame[k] })
                {
                    var clone = CloneWidget(srcGo, worldShift);
                    clone.name = srcGo.name.Replace(from, to) + "_apocapocket";
                    SetTexts(clone.transform, from, to);
                    _uiRoots[i].Add(clone);
                    TrackShift(srcGo.transform, pitch);
                    TrackShift(clone.transform, pitch);
                    if (srcGo == img[k]) _icons[i] = clone.GetComponent<RawImage>();
                    else { _frames[i] = clone.GetComponent<Graphic>(); _outlines[i] = clone.GetComponent<Outline>(); }
                }
            }
            if (bg != null) CaptureNeighbours(parent, bg, WorldRect(bg), pitch);
            Plugin.V("Slots stay in numerical order; card and left-side hints grow left by " + pitch.ToString("0.0") + " per unlocked slot; ammo/value panel stays fixed");
        }

        private static GameObject CloneWidget(GameObject source, float shift)
        {
            var clone = UnityEngine.Object.Instantiate(source, source.transform.parent, false);
            clone.name = source.name + "_apocapocket";
            foreach (var f in clone.GetComponentsInChildren<PlayMakerFSM>(true)) UnityEngine.Object.DestroyImmediate(f);
            foreach (var g in clone.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            clone.transform.position = source.transform.position + new Vector3(shift, 0f, 0f);
            return clone;
        }

        private static RectTransform _bgRect;
        private static Graphic _bgGraphic;
        private static bool _bgEnabled;
        private static float _bgWidth, _slotWidth;
        private static Vector2 _bgPosition;
        private static ExtendedCardGraphic _bgExtension;
        private class CardDecoration { internal GameObject Source, Clone; internal int Slot; }
        private static readonly List<CardDecoration> _decorations = new List<CardDecoration>();

        private static void BuildBackground(Transform card, Rect bounds, Transform first, Transform last, float pitch)
        {
            _bgRect = card as RectTransform; _bgGraphic = card.GetComponent<Graphic>();
            if (_bgRect == null || _bgGraphic == null) return;
            _bgWidth = _bgRect.rect.width; _bgPosition = _bgRect.anchoredPosition; _bgEnabled = _bgGraphic.enabled;
            float scale = Mathf.Max(0.0001f, Mathf.Abs(card.lossyScale.x));
            _slotWidth = pitch / scale;
            var child = new GameObject("Apocapocket.CardExtension", typeof(RectTransform), typeof(CanvasRenderer));
            child.transform.SetParent(card, false);
            var rt = (RectTransform)child.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            _bgExtension = child.AddComponent<ExtendedCardGraphic>();
            float left = Mathf.Max(0f, WorldRect(first).xMin - bounds.xMin) / scale;
            float right = Mathf.Max(0f, bounds.xMax - WorldRect(last).xMax) / scale;
            _bgExtension.Configure(_bgGraphic, _bgWidth, left, right);
            child.SetActive(false);
        }

        private static void ExtendBackground(int slots)
        {
            if (_bgRect == null || _bgGraphic == null || _bgExtension == null) return;
            float width = _slotWidth * slots;
            _bgRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _bgWidth + width);
            // Keep the original right edge fixed for any pivot; all added width goes to the left.
            _bgRect.anchoredPosition = _bgPosition - new Vector2(width * (1f - _bgRect.pivot.x), 0f);
            _bgGraphic.enabled = slots == 0 && _bgEnabled;
            _bgExtension.color = _bgGraphic.color;
            _bgExtension.gameObject.SetActive(slots > 0 && _bgEnabled);
        }
        private static readonly RawImage[] _gameImg = new RawImage[3];
        private static readonly Graphic[] _gameFrame = new Graphic[3];
        private static readonly Outline[] _gameOutline = new Outline[3];

        /// While a weapon from an extra slot borrows game slot n, that slot's own widget keeps showing what it held before
        /// (SlotEmptyFull writes the icon on state entry only, so a per-frame write wins) and its selection outline stays off.
        internal static void ShowInGameSlot(int n, GameObject item)
        {
            if (n < 0 || n > 2 || _gameImg[n] == null) return;
            Texture t = item != null ? Icons.IconFor(item) : _emptyTex;
            if (t == null) t = _emptyTex;
            if (t != null && _gameImg[n].texture != t) { _gameImg[n].texture = t; _gameImg[n].color = Color.white; }
            if (_gameFrame[n] != null) { var c = item != null ? _fullColor : _emptyColor; if (_gameFrame[n].color != c) _gameFrame[n].color = c; }
            if (_gameOutline[n] != null && _gameOutline[n].enabled) _gameOutline[n].enabled = false;
        }

        internal static void ShowLogicalGameSlot(int n, GameObject item, bool selected)
        {
            ShowInGameSlot(n, item);
            if (n >= 0 && n < 3 && _gameOutline[n] != null) _gameOutline[n].enabled = selected;
        }

        // ---- The slot widgets, decorations and hints to their left move by one pitch per unlocked slot.
        private class Shifted { public Transform T; public Vector3 Orig; public Vector3 Delta; }
        private static readonly List<Shifted> _shifted = new List<Shifted>();

        private static void TrackShift(Transform t, float pitch)
        {
            // Move each hierarchy once, even when a number label is inside an icon or decoration.
            foreach (var sh in _shifted)
                if (t == sh.T || t.IsChildOf(sh.T)) return;
            _shifted.RemoveAll(sh => sh.T.IsChildOf(t));
            float sx = t.parent != null ? Mathf.Max(0.0001f, Mathf.Abs(t.parent.lossyScale.x)) : 1f;
            _shifted.Add(new Shifted { T = t, Orig = t.localPosition, Delta = new Vector3(-pitch / sx, 0f, 0f) });
        }

        private static void CaptureNeighbours(Transform slotGroup, Transform card, Rect cardRect, float pitch)
        {
            var canvas = slotGroup.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var root = canvas.rootCanvas != null ? canvas.rootCanvas.transform : canvas.transform;
            // Grenade / unequip / drop hints occupy the strip immediately left of the native card.
            var zone = new Rect(cardRect.xMin - cardRect.width, cardRect.yMin - 20f, cardRect.width + 10f, cardRect.height + 40f);
            var cands = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (t == root || t.name.EndsWith("_apocapocket") || NativeWidget(t) || t.IsChildOf(card) || card.IsChildOf(t)) continue;
                bool slotPart = false;
                foreach (var sh in _shifted)
                    if (t == sh.T || t.IsChildOf(sh.T) || sh.T.IsChildOf(t)) { slotPart = true; break; }
                if (slotPart) continue;
                var r = VisualRect(t);
                if (r.width <= 0f || r.height <= 0f) continue;
                if (r.width > cardRect.width * 1.2f || r.height > cardRect.height * 1.5f) continue;
                if (!zone.Contains(r.center)) continue;
                cands.Add(t);
            }
            var set = new HashSet<Transform>(cands);
            var sb = new System.Text.StringBuilder("Left-side hints move left as the slot card grows:");
            foreach (var t in cands)
            {
                bool ancestorMoves = false;
                for (var p = t.parent; p != null; p = p.parent) if (set.Contains(p)) { ancestorMoves = true; break; }
                if (ancestorMoves) continue;
                TrackShift(t, pitch);
                var r = VisualRect(t);
                sb.Append("\n  ").Append(Path(t)).Append(t.gameObject.activeInHierarchy ? "" : " (inactive)").Append(" x=").Append(r.xMin.ToString("0")).Append(" w=").Append(r.width.ToString("0"));
            }
            Plugin.V(sb.ToString());
        }

        private static bool NativeWidget(Transform t)
        {
            for (int i = 0; i < 3; i++)
                foreach (var graphic in new Graphic[] { _gameImg[i], _gameFrame[i] })
                    if (graphic != null && (t == graphic.transform || t.IsChildOf(graphic.transform) || graphic.transform.IsChildOf(t))) return true;
            return false;
        }

        /// Where a UI element is actually drawn. Text boxes in this game are often far wider than their text (1000 px boxes with
        /// centred text), so for UI.Text the horizontal extent comes from the alignment and the text's preferred width.
        private static Rect VisualRect(Transform t)
        {
            var r = WorldRect(t);
            var text = t.GetComponent<Text>();
            if (text == null || string.IsNullOrEmpty(text.text)) return r;
            float scale = Mathf.Abs(t.lossyScale.x);
            float w = Mathf.Min(r.width, text.preferredWidth * scale);
            if (w <= 0f) return r;
            float x;
            switch (text.alignment)
            {
                case TextAnchor.UpperLeft: case TextAnchor.MiddleLeft: case TextAnchor.LowerLeft: x = r.xMin; break;
                case TextAnchor.UpperRight: case TextAnchor.MiddleRight: case TextAnchor.LowerRight: x = r.xMax - w; break;
                default: x = r.center.x - w * 0.5f; break;
            }
            float h = Mathf.Min(r.height, text.preferredHeight * Mathf.Abs(t.lossyScale.y));
            if (h <= 0f) h = r.height;
            float y;
            switch (text.alignment)
            {
                case TextAnchor.UpperLeft: case TextAnchor.UpperCenter: case TextAnchor.UpperRight: y = r.yMax - h; break;
                case TextAnchor.LowerLeft: case TextAnchor.LowerCenter: case TextAnchor.LowerRight: y = r.yMin; break;
                default: y = r.center.y - h * 0.5f; break;
            }
            return new Rect(x, y, w, h);
        }

        private static void ApplyShift(int slots)
        {
            foreach (var sh in _shifted)
            {
                if (sh.T == null) continue;
                var want = sh.Orig + sh.Delta * slots;
                if (sh.T.localPosition != want) sh.T.localPosition = want;
            }
        }

        /// Mod disabled: hide every extra-slot object and put the game's UI back where it was.
        internal static void HideAll()
        {
            ApplyShift(0);
            ExtendBackground(0);
            for (int i = 0; i < Max; i++) foreach (var go in _uiRoots[i]) if (go != null && go.activeSelf) go.SetActive(false);
            foreach (var part in _decorations) if (part.Clone != null && part.Clone.activeSelf) part.Clone.SetActive(false);
        }

        private static Rect WorldRect(Transform t)
        {
            var rt = t as RectTransform;
            if (rt == null) return new Rect(t.position.x, t.position.y, 0f, 0f);
            var c = new Vector3[4]; rt.GetWorldCorners(c);
            float minX = Mathf.Min(c[0].x, c[2].x), minY = Mathf.Min(c[0].y, c[2].y);
            return new Rect(minX, minY, Mathf.Abs(c[2].x - c[0].x), Mathf.Abs(c[2].y - c[0].y));
        }

        private static void DumpChildren(Transform container)
        {
            if (!Plugin.Verbose.Value) return;
            var sb = new System.Text.StringBuilder("Card container " + Path(container) + ":");
            foreach (Transform ch in container)
            {
                var r = WorldRect(ch);
                sb.Append("\n  [").Append(ch.GetSiblingIndex()).Append("] ").Append(ch.name).Append(ch.gameObject.activeSelf ? "" : " (inactive)")
                  .Append(" rect=(").Append(r.x.ToString("0")).Append(",").Append(r.y.ToString("0")).Append(" ").Append(r.width.ToString("0")).Append("x").Append(r.height.ToString("0")).Append(")");
                foreach (var c in ch.GetComponents<Component>()) if (c != null && !(c is Transform) && !(c is CanvasRenderer)) sb.Append(" [").Append(c.GetType().Name).Append("]");
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static Vector3 Center(Transform t)
        {
            var rt = t as RectTransform;
            if (rt == null) return t.position;
            var c = new Vector3[4]; rt.GetWorldCorners(c);
            return (c[0] + c[2]) * 0.5f;
        }

        private static float WorldWidth(Transform t)
        {
            var rt = t as RectTransform;
            if (rt == null) return 0f;
            var c = new Vector3[4]; rt.GetWorldCorners(c);
            return Mathf.Abs(c[2].x - c[0].x);
        }

        /// The slot card: smallest Image/RawImage on the canvas (outside the slot group, not one of its ancestors, not
        /// full-screen) whose rectangle holds the centres of slot 1 and slot 3.
        private static Transform FindBackground(Transform slotGroup, Vector3 c1, Vector3 c3)
        {
            var canvas = slotGroup.GetComponentInParent<Canvas>();
            if (canvas == null) return null;
            var root = canvas.rootCanvas != null ? canvas.rootCanvas.transform : canvas.transform;
            var rootRt = root as RectTransform;
            float screenArea = rootRt != null ? Mathf.Abs(rootRt.rect.width * rootRt.rect.height * root.lossyScale.x * root.lossyScale.y) : float.MaxValue;
            Transform best = null; float bestArea = float.MaxValue;
            var sb = new System.Text.StringBuilder("Slot card candidates:");
            foreach (var g in root.GetComponentsInChildren<Graphic>(true))
            {
                if (!(g is Image) && !(g is RawImage)) continue;
                var t = g.transform as RectTransform;
                if (t == null || t == slotGroup || t.IsChildOf(slotGroup) || slotGroup.IsChildOf(t)) continue;
                if (!g.gameObject.activeInHierarchy || t.name.EndsWith("_apocapocket")) continue;
                var c = new Vector3[4]; t.GetWorldCorners(c);
                float minX = Mathf.Min(c[0].x, c[2].x), maxX = Mathf.Max(c[0].x, c[2].x), minY = Mathf.Min(c[0].y, c[2].y), maxY = Mathf.Max(c[0].y, c[2].y);
                bool holds = c1.x >= minX && c1.x <= maxX && c3.x >= minX && c3.x <= maxX && c1.y >= minY && c1.y <= maxY;
                if (!holds) continue;
                float area = (maxX - minX) * (maxY - minY);
                sb.Append("\n  ").Append(Path(t)).Append(" ").Append((maxX - minX).ToString("0")).Append("x").Append((maxY - minY).ToString("0"));
                if (area > screenArea * 0.25f) continue;
                if (area < bestArea) { bestArea = area; best = t; }
            }
            Plugin.V(sb.ToString());
            return best;
        }

        /// Per frame: visibility (only active slots), icon or empty texture, frame colour, selection outline (`selected` =
        /// slot index 3..5 whose content is in the hand / drawn, else -1).
        internal static void UpdateUi(Func<int, GameObject> itemInSlot, int selected)
        {
            int active = Active;
            ApplyShift(active);
            ExtendBackground(active);
            foreach (var part in _decorations)
            {
                if (part.Clone == null) continue;
                bool show = part.Slot < active && part.Source != null && part.Source.activeSelf;
                if (part.Clone.activeSelf != show) part.Clone.SetActive(show);
            }
            for (int i = 0; i < Max; i++)
            {
                bool show = i < active;
                foreach (var go in _uiRoots[i]) if (go != null && go.activeSelf != show) go.SetActive(show);
                if (!show) continue;
                var item = itemInSlot(3 + i);
                if (_icons[i] != null)
                {
                    Texture t = item != null ? Icons.IconFor(item) : _emptyTex;
                    if (t == null) t = _emptyTex;
                    if (_icons[i].texture != t) { _icons[i].texture = t; _icons[i].color = Color.white; }
                }
                if (_frames[i] != null) { var c = item != null ? _fullColor : _emptyColor; if (_frames[i].color != c) _frames[i].color = c; }
                if (_outlines[i] != null) { bool sel = selected == 3 + i; if (_outlines[i].enabled != sel) _outlines[i].enabled = sel; }
            }
        }

        // ------------------------------------------------------------------ helpers
        /// Reads what the game's SlotEmptyFull FSM of a slot writes: icon RawImage, frame graphic, empty texture, colours.
        private static bool ReadSlotUi(Transform slot, out GameObject img, out GameObject frame, out Texture emptyTex, out Color emptyCol, out Color fullCol)
        {
            img = null; frame = null; emptyTex = null; emptyCol = Color.white; fullCol = new Color(1f, 0.985f, 0f, 1f);
            var fsm = Fsms.Find(slot.gameObject, "SlotEmptyFull");
            if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Initialized) return false;
            foreach (var st in fsm.FsmStates)
                foreach (var a in st.Actions)
                {
                    if (a == null) continue;
                    string tn = a.GetType().Name;
                    if (tn == "UiRawImageSetTexture")
                    {
                        var go = Owner(a, fsm);
                        var tex = Field(a, "texture") as FsmTexture;
                        if (st.Name == "full") img = go;
                        else if (st.Name == "empty" && tex != null) emptyTex = tex.Value;
                    }
                    else if (tn == "UiGraphicSetColor")
                    {
                        var go = Owner(a, fsm);
                        var col = Field(a, "color") as FsmColor;
                        if (st.Name == "full") { frame = go; if (col != null) fullCol = col.Value; }
                        else if (st.Name == "empty" && col != null) emptyCol = col.Value;
                    }
                }
            return img != null;
        }

        private static object Field(object o, string name)
        {
            var f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f != null ? f.GetValue(o) : null;
        }

        private static GameObject Owner(object action, PlayMakerFSM fsm)
        {
            var od = Field(action, "gameObject") as FsmOwnerDefault;
            if (od == null) return null;
            return od.OwnerOption == OwnerDefaultOption.UseOwner ? fsm.gameObject : (od.GameObject != null ? od.GameObject.Value : null);
        }




        private static void SetTexts(Transform t, string from, string to)
        {
            foreach (var comp in t.GetComponentsInChildren<Component>(true))
            {
                if (comp == null || !(comp is Graphic)) continue;
                var p = comp.GetType().GetProperty("text", typeof(string));
                if (p == null || !p.CanWrite) continue;
                var s = p.GetValue(comp, null) as string;
                if (s != null && s.Trim() == from) p.SetValue(comp, s.Replace(from, to), null);
            }
        }

        private static bool _dumped;
        private static void DumpOnce(Transform parent)
        {
            if (_dumped || !Plugin.Verbose.Value) return;
            _dumped = true;
            var sb = new System.Text.StringBuilder("Slot UI hierarchy (" + Path(parent) + "):");
            Dump(parent, 0, sb);
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static void Dump(Transform t, int depth, System.Text.StringBuilder sb)
        {
            if (depth > 4) return;
            var rt = t as RectTransform;
            sb.Append("\n  ").Append(new string(' ', depth * 2)).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (inactive)");
            if (rt != null) sb.Append(" pos=").Append(rt.anchoredPosition).Append(" size=").Append(rt.sizeDelta);
            foreach (var c in t.GetComponents<Component>()) if (c != null && !(c is Transform)) sb.Append(" [").Append(c.GetType().Name).Append("]");
            foreach (Transform ch in t) Dump(ch, depth + 1, sb);
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
