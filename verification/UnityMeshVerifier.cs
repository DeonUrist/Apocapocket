// Optional verification executed inside the installed Unity 2020.3 editor, using the production graphic source.
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class UnityMeshVerifier
{
    public static void Run()
    {
        try
        {
            var canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
            canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var source = new GameObject("NativeCard", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            source.transform.SetParent(canvas.transform, false);
            var texture = new Texture2D(192, 64);
            var raw = source.GetComponent<RawImage>(); raw.texture = texture;
            raw.uvRect = new Rect(0.125f, 0.25f, 0.75f, 0.5f);
            var type = Assembly.Load("Assembly-CSharp").GetType("Apocapocket.ExtendedCardGraphic", true);
            var target = new GameObject("ExtendedCard", typeof(RectTransform), typeof(CanvasRenderer));
            target.transform.SetParent(canvas.transform, false);
            var graphic = (Graphic)target.AddComponent(type);
            var configure = type.GetMethod("Configure", BindingFlags.NonPublic | BindingFlags.Instance);
            var populate = type.GetMethod("OnPopulateMesh", BindingFlags.NonPublic | BindingFlags.Instance);
            configure.Invoke(graphic, new object[] { raw, 180f, 12f, 12f });
            for (int extra = 0; extra <= 3; extra++)
            {
                graphic.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 180f + extra * 60f);
                graphic.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 60f);
                using (var mesh = new VertexHelper())
                {
                    populate.Invoke(graphic, new object[] { mesh });
                    Check(mesh.currentVertCount >= 12, "Card must contain both caps and a middle");
                    var first = new UIVertex(); mesh.PopulateUIVertex(ref first, 0);
                    var last = new UIVertex(); mesh.PopulateUIVertex(ref last, mesh.currentVertCount - 1);
                    Check(Mathf.Abs(first.uv0.x - raw.uvRect.xMin) < 0.0001f, "Left cap retains its original UV");
                    Check(Mathf.Abs(last.uv0.x - raw.uvRect.xMax) < 0.0001f, "Right cap retains its original UV");
                    float covered = 0f;
                    for (int i = 0; i < mesh.currentVertCount; i += 4)
                    {
                        var a = new UIVertex(); var b = new UIVertex();
                        mesh.PopulateUIVertex(ref a, i); mesh.PopulateUIVertex(ref b, i + 3);
                        float width = b.position.x - a.position.x;
                        Check(Mathf.Abs((b.uv0.x - a.uv0.x) / width - raw.uvRect.width / 180f) < 0.0001f, "Horizontal artwork density must not stretch");
                        covered += width;
                    }
                    Check(Mathf.Abs(covered - (180f + extra * 60f)) < 0.001f, "Background covers exactly the unlocked card width");
                    Debug.Log("Verified native UI mesh: " + (3 + extra) + " slots, " + covered + " units, unchanged artwork density");
                }
            }
            UnityEngine.Object.DestroyImmediate(canvas); UnityEngine.Object.DestroyImmediate(texture);
            Debug.Log("APOCAPOCKET_UI_PASS: production graphic validated inside Unity " + Application.unityVersion);
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
