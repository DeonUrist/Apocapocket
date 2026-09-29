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
    ///  - UI: slot 3's widget (RawImage icon + coloured frame + number label) is cloned three times and placed at the same
    ///    spacing to the right. Icon / frame colour / selection outline are driven by the mod (no FSMs on the clones).
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
        internal static int Active { get { return Mathf.Clamp(Plugin.ExtraSlotCount.Value, 0, Max); } }

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

        internal static void ResetUi() { _uiFailed = false; for (int i = 0; i < Max; i++) { _icons[i] = null; _frames[i] = null; _outlines[i] = null; _uiRoots[i].Clear(); } }

        private static void BuildUi(Transform slot2, Transform slot3)
        {
            GameObject img2, frame2, img3, frame3;
            Texture emptyTex; Color emptyCol, fullCol;
            if (!ReadSlotUi(slot2, out img2, out frame2, out emptyTex, out emptyCol, out fullCol)) return;
            if (!ReadSlotUi(slot3, out img3, out frame3, out emptyTex, out emptyCol, out fullCol)) return;
            if (img3 == null || frame3 == null) return;
            _emptyTex = emptyTex; _emptyColor = emptyCol; _fullColor = fullCol;

            // The widget root(s) of slot 3: highest ancestors that do not also contain slot 2's parts.
            var roots3 = new List<Transform>();
            var r1 = SlotRoot(img3.transform, frame2.transform, img2.transform);
            var r2 = SlotRoot(frame3.transform, frame2.transform, img2.transform);
            roots3.Add(r1); if (r2 != r1) roots3.Add(r2);
            var roots2 = new List<Transform>();
            var q1 = SlotRoot(img2.transform, frame3.transform, img3.transform);
            var q2 = SlotRoot(frame2.transform, frame3.transform, img3.transform);
            roots2.Add(q1); if (q2 != q1) roots2.Add(q2);
            var parent = r1.parent;
            // Number labels ("3") that sit outside the widget root(s).
            foreach (Transform c in parent)
                if (!roots3.Contains(c) && !roots2.Contains(c) && HasText(c, "3")) roots3.Add(c);

            var rt3 = r1 as RectTransform; var rt2 = q1 as RectTransform;
            Vector2 step = rt3 != null && rt2 != null ? rt3.anchoredPosition - rt2.anchoredPosition : new Vector2(60f, 0f);
            bool layout = parent.GetComponent<LayoutGroup>() != null;
            Plugin.V("Extra slot UI: cloning " + roots3.Count + " object(s) of slot 3 under " + Path(parent) + ", step " + step + (layout ? " (layout group)" : ""));
            DumpOnce(parent);

            for (int i = 0; i < Max; i++)
            {
                string num = (i + 4).ToString();
                foreach (var src in roots3)
                {
                    var clone = UnityEngine.Object.Instantiate(src.gameObject, parent, false);
                    clone.name = src.name.Replace("3", num) + "_apocapocket";
                    foreach (var f in clone.GetComponentsInChildren<PlayMakerFSM>(true)) UnityEngine.Object.DestroyImmediate(f);
                    var crt = clone.transform as RectTransform;
                    var srt = src as RectTransform;
                    if (crt != null && srt != null) crt.anchoredPosition = srt.anchoredPosition + step * (i + 1);
                    if (layout) clone.transform.SetSiblingIndex(src.GetSiblingIndex() + 1 + i);
                    SetTexts(clone.transform, "3", num);
                    _uiRoots[i].Add(clone);
                    // Counterparts of slot 3's icon / frame inside this clone.
                    var ic = Counterpart(src, img3.transform, clone.transform);
                    if (ic != null && _icons[i] == null) _icons[i] = ic.GetComponent<RawImage>();
                    var fr = Counterpart(src, frame3.transform, clone.transform);
                    if (fr != null && _frames[i] == null) { _frames[i] = fr.GetComponent<Graphic>(); _outlines[i] = fr.GetComponent<Outline>(); }
                }
            }
            Plugin.V("Extra slot UI ready: icons " + (_icons[0] != null) + ", frames " + (_frames[0] != null) + ", outline " + (_outlines[0] != null));
        }

        /// Per frame: visibility (only active slots), icon or empty texture, frame colour, selection outline.
        internal static void UpdateUi(Func<int, GameObject> itemInSlot, int heldFrom)
        {
            int active = Active;
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

        /// Highest ancestor of t that contains neither of the other slot's parts.
        private static Transform SlotRoot(Transform t, Transform otherA, Transform otherB)
        {
            var r = t;
            while (r.parent != null && !otherA.IsChildOf(r.parent) && !otherB.IsChildOf(r.parent)) r = r.parent;
            return r;
        }

        /// The object in `clone` at the same relative path as `target` has under `src` (null if target is not under src).
        private static Transform Counterpart(Transform src, Transform target, Transform clone)
        {
            if (target != src && !target.IsChildOf(src)) return null;
            var path = new List<int>();
            for (var t = target; t != src; t = t.parent) path.Insert(0, t.GetSiblingIndex());
            var c = clone;
            foreach (var idx in path) { if (idx >= c.childCount) return null; c = c.GetChild(idx); }
            return c;
        }

        private static bool HasText(Transform t, string value)
        {
            foreach (var comp in t.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var p = comp.GetType().GetProperty("text", typeof(string));
                if (p == null || !(comp is Graphic)) continue;
                var s = p.GetValue(comp, null) as string;
                if (s != null && s.Trim() == value) return true;
            }
            return false;
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
