using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Apocapocket
{
    /// Renders a small snapshot of an item's model into a texture (once per prefab name, cached for the session)
    /// and shows it in the game's slot icon (RawImage "Image_1/2/3").
    internal static class Icons
    {
        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();
        private static int _layer = -1;
        private static bool _pipelineChecked, _pipelineOk;

        internal static string PrefabName(GameObject go)
        {
            string n = go.name;
            int i = n.IndexOf("(Clone)", StringComparison.Ordinal);
            return i > 0 ? n.Substring(0, i) : n;
        }

        internal static Texture2D Get(GameObject item)
        {
            string key = PrefabName(item);
            Texture2D t;
            if (_cache.TryGetValue(key, out t) && t != null) return t;
            t = Render(item);
            _cache[key] = t;
            return t;
        }

        /// Sets the slot's RawImage to the item's icon; returns false if the image object could not be found.
        internal static bool Apply(int slot, Texture2D tex)
        {
            var img = FindImage(slot);
            if (img == null) return false;
            if (tex != null && img.texture != tex) { img.texture = tex; img.color = Color.white; }
            return true;
        }

        private static readonly RawImage[] _images = new RawImage[3];
        internal static Transform[] Slots;   // set by Runner

        /// The RawImage the game's own SlotEmptyFull FSM writes to (several UI objects share the name "Image_N").
        private static RawImage FindImage(int slot)
        {
            if (_images[slot] != null) return _images[slot];
            if (Slots == null || Slots[slot] == null) return null;
            var fsm = Fsms.Find(Slots[slot].gameObject, "SlotEmptyFull");
            if (fsm == null || !fsm.Fsm.Initialized) return null;
            foreach (var st in fsm.FsmStates)
            {
                if (st.Name != "full") continue;
                foreach (var a in st.Actions)
                {
                    if (a == null || a.GetType().Name != "UiRawImageSetTexture") continue;
                    var fld = a.GetType().GetField("gameObject");
                    var od = fld != null ? fld.GetValue(a) as HutongGames.PlayMaker.FsmOwnerDefault : null;
                    GameObject go = null;
                    if (od != null) go = od.OwnerOption == HutongGames.PlayMaker.OwnerDefaultOption.UseOwner ? fsm.gameObject : (od.GameObject != null ? od.GameObject.Value : null);
                    if (go != null) { _images[slot] = go.GetComponent<RawImage>(); Plugin.V("Slot " + (slot + 1) + " icon image: " + GetPath(go)); }
                    break;
                }
            }
            return _images[slot];
        }

        private static string GetPath(GameObject go)
        {
            string p = go.name; var t = go.transform.parent;
            while (t != null) { p = t.name + "/" + p; t = t.parent; }
            return p;
        }

        private static int FreeLayer()
        {
            if (_layer >= 0) return _layer;
            for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { _layer = i; break; }
            if (_layer < 0) _layer = 31;
            Plugin.V("Icon render layer " + _layer);
            return _layer;
        }

        private static Texture2D Render(GameObject item)
        {
            if (!_pipelineChecked)
            {
                _pipelineChecked = true;
                _pipelineOk = GraphicsSettings.renderPipelineAsset == null;
                if (!_pipelineOk) Plugin.Log.LogWarning("Scriptable render pipeline in use; item icons disabled");
            }
            if (!_pipelineOk) return null;

            GameObject clone = null, camGo = null, lightGo = null, holder = null;
            RenderTexture rt = null;
            var oldActive = RenderTexture.active;
            bool oldFog = RenderSettings.fog;
            try
            {
                int size = Plugin.IconSize.Value;
                int layer = FreeLayer();
                Vector3 far = new Vector3(0f, -4000f, 0f);

                // Instantiate under an inactive holder so no Awake/Start runs on the copy before we strip its logic.
                holder = new GameObject("Apocapocket.IconHolder");
                holder.SetActive(false);
                holder.transform.position = far;
                clone = UnityEngine.Object.Instantiate(item, holder.transform);
                clone.name = "Apocapocket.IconClone";
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.SetActive(true);
                // Strip everything that could run logic; keep transforms, meshes and renderers.
                foreach (var f in clone.GetComponentsInChildren<PlayMakerFSM>(true)) UnityEngine.Object.DestroyImmediate(f);
                foreach (var b in clone.GetComponentsInChildren<MonoBehaviour>(true)) if (b != null) UnityEngine.Object.DestroyImmediate(b);
                foreach (var j in clone.GetComponentsInChildren<Joint>(true)) UnityEngine.Object.DestroyImmediate(j);
                foreach (var rb in clone.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(rb);
                foreach (var c in clone.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
                foreach (var l in clone.GetComponentsInChildren<Light>(true)) UnityEngine.Object.DestroyImmediate(l);
                var bounds = new Bounds(far, Vector3.zero);
                bool any = false;
                foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) { r.enabled = false; continue; }
                    r.enabled = true;
                    r.gameObject.SetActive(true);
                    r.gameObject.layer = layer;
                    any = true;
                }
                foreach (var t in clone.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                holder.SetActive(true);
                any = false;
                foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled) continue;
                    if (!any) { bounds = r.bounds; any = true; } else bounds.Encapsulate(r.bounds);
                }
                if (!any) { Plugin.V("No renderers on " + item.name + " for icon"); return null; }

                float radius = Mathf.Max(bounds.extents.magnitude, 0.05f);
                camGo = new GameObject("Apocapocket.IconCam");
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.cullingMask = 1 << layer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                cam.orthographic = true;
                cam.orthographicSize = radius * 1.05f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = radius * 6f;
                cam.allowHDR = false; cam.allowMSAA = false;
                cam.useOcclusionCulling = false;
                Vector3 dir = new Vector3(-1f, 0.8f, -1f).normalized;
                cam.transform.position = bounds.center + dir * radius * 3f;
                cam.transform.LookAt(bounds.center);

                lightGo = new GameObject("Apocapocket.IconLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.cullingMask = 1 << layer;
                light.intensity = 1.2f;
                light.color = Color.white;
                light.shadows = LightShadows.None;
                lightGo.transform.rotation = Quaternion.LookRotation(-dir + Vector3.down * 0.3f);

                rt = new RenderTexture(size, size, 16, RenderTextureFormat.ARGB32);
                rt.antiAliasing = 4;
                cam.targetTexture = rt;
                RenderSettings.fog = false;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
                tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                tex.Apply();
                tex.name = "Apocapocket_icon_" + PrefabName(item);
                Plugin.V("Rendered icon for " + PrefabName(item) + " (r=" + radius.ToString("0.00") + ")");
                return tex;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Icon render failed for " + item.name + ": " + e.Message);
                return null;
            }
            finally
            {
                RenderSettings.fog = oldFog;
                RenderTexture.active = oldActive;
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
                if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
                if (lightGo != null) UnityEngine.Object.DestroyImmediate(lightGo);
            }
        }
    }
}
