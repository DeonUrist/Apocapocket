using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.UI;

namespace Apocapocket
{
    /// Item-only slots 4..6 next to the game's three weapon slots.
    ///  - Holders: "Slot 4..6" under PlayerCamera/Apocapocket_ExtraSlots. A pocketed item is parented to its holder exactly
    ///    like an item in Slot 1..3. Easy Save stores an item's parent as a reference ID, so each holder (GameObject and
    ///    Transform) is registered in the scene's ES3 reference manager under a FIXED ID: a save made with an item in slot 5
    ///    resolves that parent again after a load, as long as the holders are registered before the game loads its items.
    ///  - UI: slot k's icon + number label are cloned for slot k+3 and placed one card width to the left (order 4 5 6 | 1 2 3),
    ///    together with a clone of the rusty slot card behind them. Icon / label colour / selection outline are driven by the
    ///    mod (no FSMs on the clones).
    internal static class ExtraSlots
    {
        internal const int Max = 3;
        // Arbitrary fixed Easy Save reference IDs (GameObject, Transform) per holder. Never change once released.
        private static readonly long[] GoIds = { 7431505000000000401L, 7431505000000000501L, 7431505000000000601L };
        private static readonly long[] TrIds = { 7431505000000000402L, 7431505000000000502L, 7431505000000000602L };

        internal static readonly Transform[] Holders = new Transform[Max];
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
                Register(h, i);
            }
        }

        private static void Register(Transform h, int i)
        {
            try
            {
                var mgr = ES3Internal.ES3ReferenceMgrBase.Current;
                if (mgr == null) { Plugin.Log.LogWarning("No Easy Save reference manager in the scene; items in slot " + (i + 4) + " will not survive a save/load"); return; }
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

        internal static void ResetUi() { _shifted.Clear(); _shiftOn = false; _uiFailed = false; _bgClone = null; _bgSource = null; _cardParts.Clear(); for (int i = 0; i < Max; i++) { _icons[i] = null; _frames[i] = null; _outlines[i] = null; _uiRoots[i].Clear(); } }

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
            DumpOnce(parent);

            // Distance between the two groups: one background card width (so 4 5 6 sit on their own card left of 1 2 3).
            Vector3 c1 = Center(img[0].transform), c3 = Center(img[2].transform);
            var bg = FindBackground(parent, c1, c3);
            float worldShift = bg != null ? WorldWidth(bg) : (c3.x - c1.x) * 1.5f;
            float localShift = worldShift / Mathf.Max(0.0001f, parent.lossyScale.x);
            Plugin.V("Extra slot UI: slot group shift " + localShift.ToString("0.0") + " (card " + (bg != null ? Path(bg) + " width " + WorldWidth(bg).ToString("0.0") : "not found") + ")");

            if (bg != null)
            {
                DumpChildren(bg.parent);
                // The card plus every decoration drawn on it (dividers, slot squares...): siblings in the card's container whose
                // centre lies on the card. Each copy goes right after its original, so the drawing order stays the same.
                var parts = new List<Transform> { bg };
                var cardRect = WorldRect(bg);
                foreach (Transform sib in bg.parent)
                {
                    if (sib == bg || sib.name.EndsWith("_apocapocket") || sib == parent || parent.IsChildOf(sib)) continue;
                    if (sib.GetComponentInChildren<Graphic>(true) == null) continue;
                    var r = WorldRect(sib);
                    if (r.width <= 0f || r.width * r.height >= cardRect.width * cardRect.height) continue;
                    if (!cardRect.Contains(r.center)) continue;
                    parts.Add(sib);
                }
                var ordered = new List<Transform>(parts);
                ordered.Sort((x, y) => y.GetSiblingIndex().CompareTo(x.GetSiblingIndex()));   // insert from the back so indices stay valid
                foreach (var src in ordered)
                {
                    var clone = UnityEngine.Object.Instantiate(src.gameObject, src.parent, false);
                    clone.name = src.name + "_apocapocket";
                    foreach (var f in clone.GetComponentsInChildren<PlayMakerFSM>(true)) UnityEngine.Object.DestroyImmediate(f);
                    clone.transform.position = src.position - new Vector3(worldShift, 0f, 0f);
                    clone.transform.SetSiblingIndex(src.GetSiblingIndex() + 1);
                    _cardParts.Add(new KeyValuePair<GameObject, GameObject>(src.gameObject, clone));
                    if (src == bg) { _bgSource = bg.gameObject; _bgClone = clone; }
                }
                Plugin.V("Extra slot UI: cloned the card and " + (parts.Count - 1) + " decoration(s) on it");
                CaptureNeighbours(parent, bg, cardRect, worldShift);
            }

            for (int i = 0; i < Max; i++)
            {
                int k = i;   // slot 4 <- widgets of slot 1, 5 <- 2, 6 <- 3
                string from = (k + 1).ToString(), to = (k + 4).ToString();
                foreach (var srcGo in new[] { img[k], frame[k] })
                {
                    var src = srcGo.transform;
                    var clone = UnityEngine.Object.Instantiate(srcGo, parent, false);
                    clone.name = src.name.Replace(from, to) + "_apocapocket";
                    foreach (var f in clone.GetComponentsInChildren<PlayMakerFSM>(true)) UnityEngine.Object.DestroyImmediate(f);
                    var crt = clone.transform as RectTransform; var srt = src as RectTransform;
                    if (crt != null && srt != null) crt.anchoredPosition = srt.anchoredPosition - new Vector2(localShift, 0f);
                    else clone.transform.position = src.position - new Vector3(worldShift, 0f, 0f);
                    SetTexts(clone.transform, from, to);
                    _uiRoots[i].Add(clone);
                    if (srcGo == img[k]) _icons[i] = clone.GetComponent<RawImage>();
                    else { _frames[i] = clone.GetComponent<Graphic>(); _outlines[i] = clone.GetComponent<Outline>(); }
                }
            }
            Plugin.V("Extra slot UI ready: icons " + (_icons[0] != null) + ", frames " + (_frames[0] != null) + ", outline " + (_outlines[0] != null) + ", card " + (_bgClone != null));
        }

        private static GameObject _bgSource, _bgClone;

        // ---- game UI left of the weapon card (Unequip / Drop / Grenade hints on weapon_extra_bg): moved out of the way while
        //      extra slots are shown, restored to the exact original position when they are not.
        private class Shifted { public Transform T; public Vector3 Orig; public Vector3 Delta; }
        private static readonly List<Shifted> _shifted = new List<Shifted>();
        private static bool _shiftOn;

        private static void CaptureNeighbours(Transform slotGroup, Transform card, Rect cardRect, float worldShift)
        {
            var canvas = slotGroup.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var root = canvas.rootCanvas != null ? canvas.rootCanvas.transform : canvas.transform;
            // Strip directly left of the card, one card wide, same height band.
            float minX = cardRect.xMin - cardRect.width, maxX = cardRect.xMin + 10f;
            float minY = cardRect.yMin - 20f, maxY = cardRect.yMax + 20f;
            var skip = new HashSet<Transform>();
            foreach (var kv in _cardParts) { if (kv.Key != null) skip.Add(kv.Key.transform); if (kv.Value != null) skip.Add(kv.Value.transform); }
            var cands = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (t == root || skip.Contains(t) || t.name.EndsWith("_apocapocket")) continue;
                if (t == slotGroup || t.IsChildOf(slotGroup) || slotGroup.IsChildOf(t) || card.IsChildOf(t)) continue;
                bool insideClone = false;
                foreach (var sk in skip) if (t.IsChildOf(sk)) { insideClone = true; break; }
                if (insideClone) continue;
                var r = VisualRect(t);
                if (r.width <= 0f || r.height <= 0f) continue;
                if (r.width > cardRect.width * 1.2f || r.height > cardRect.height * 1.5f) continue;
                var c = r.center;
                if (c.x < minX || c.x > maxX || c.y < minY || c.y > maxY) continue;
                cands.Add(t);
            }
            var set = new HashSet<Transform>(cands);
            var sb = new System.Text.StringBuilder("UI moved aside while slots 4-6 are shown:");
            foreach (var t in cands)
            {
                if (t.parent != null && set.Contains(t.parent)) continue;   // its parent moves it already
                float sx = t.parent != null ? Mathf.Max(0.0001f, t.parent.lossyScale.x) : 1f;
                _shifted.Add(new Shifted { T = t, Orig = t.localPosition, Delta = new Vector3(-worldShift / sx, 0f, 0f) });
                var r = VisualRect(t);
                sb.Append("\n  ").Append(Path(t)).Append(t.gameObject.activeInHierarchy ? "" : " (inactive)").Append(" x=").Append(r.xMin.ToString("0")).Append(" w=").Append(r.width.ToString("0"));
            }
            Plugin.V(sb.ToString());
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

        private static void ApplyShift(bool on)
        {
            foreach (var sh in _shifted)
            {
                if (sh.T == null) continue;
                var want = on ? sh.Orig + sh.Delta : sh.Orig;
                if (sh.T.localPosition != want) sh.T.localPosition = want;
            }
            _shiftOn = on;
        }

        /// Mod disabled: hide every extra-slot object and put the game's UI back where it was.
        internal static void HideAll()
        {
            ApplyShift(false);
            for (int i = 0; i < Max; i++) foreach (var go in _uiRoots[i]) if (go != null && go.activeSelf) go.SetActive(false);
            foreach (var kv in _cardParts) if (kv.Value != null && kv.Value.activeSelf) kv.Value.SetActive(false);
        }
        private static readonly List<KeyValuePair<GameObject, GameObject>> _cardParts = new List<KeyValuePair<GameObject, GameObject>>();

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

        /// Per frame: visibility (only active slots), icon or empty texture, frame colour, selection outline.
        internal static void UpdateUi(Func<int, GameObject> itemInSlot, int heldFrom)
        {
            int active = Active;
            ApplyShift(active > 0);
            foreach (var kv in _cardParts)
            {
                if (kv.Value == null) continue;
                bool showPart = active > 0 && kv.Key != null && kv.Key.activeSelf;
                if (kv.Value.activeSelf != showPart) kv.Value.SetActive(showPart);
            }
            for (int i = 0; i < Max; i++)
            {
                bool show = i < active;
                foreach (var go in _uiRoots[i]) if (go != null && go.activeSelf != show) go.SetActive(show);
                if (!show) continue;
                var item = itemInSlot(3 + i);
                if (_icons[i] != null)
                {
                    Texture t = item != null ? (Texture)Icons.Get(item) : _emptyTex;
                    if (t == null) t = _emptyTex;
                    if (_icons[i].texture != t) { _icons[i].texture = t; _icons[i].color = Color.white; }
                }
                if (_frames[i] != null) { var c = item != null ? _fullColor : _emptyColor; if (_frames[i].color != c) _frames[i].color = c; }
                if (_outlines[i] != null) { bool sel = heldFrom == 3 + i; if (_outlines[i].enabled != sel) _outlines[i].enabled = sel; }
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
