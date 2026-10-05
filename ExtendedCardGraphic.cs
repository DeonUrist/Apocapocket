using UnityEngine;
using UnityEngine.UI;

namespace Apocapocket
{
    /// Repeats the existing card's middle at its original scale. End caps and height retain their original UV scale.
    internal sealed class ExtendedCardGraphic : MaskableGraphic
    {
        private Texture _texture;
        private Rect _uv = new Rect(0f, 0f, 1f, 1f);
        private float _nativeWidth, _left, _right;

        public override Texture mainTexture { get { return _texture != null ? _texture : base.mainTexture; } }

        internal void Configure(Graphic source, float nativeWidth, float left, float right)
        {
            _texture = source.mainTexture;
            _nativeWidth = nativeWidth;
            _left = Mathf.Clamp(left, nativeWidth * 0.04f, nativeWidth * 0.25f);
            _right = Mathf.Clamp(right, nativeWidth * 0.04f, nativeWidth * 0.25f);
            var raw = source as RawImage;
            var image = source as Image;
            if (raw != null) _uv = raw.uvRect;
            else if (image != null)
            {
                var sprite = image.overrideSprite != null ? image.overrideSprite : image.sprite;
                if (sprite != null)
                {
                    Vector2 min = Vector2.one, max = Vector2.zero;
                    foreach (var uv in sprite.uv) { min = Vector2.Min(min, uv); max = Vector2.Max(max, uv); }
                    _uv = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
                }
            }
            material = source.material;
            color = source.color;
            raycastTarget = false;
            SetMaterialDirty(); SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = GetPixelAdjustedRect();
            if (_nativeWidth <= 0f || rect.width <= 0f || rect.height <= 0f) return;
            float left = Mathf.Min(_left, rect.width * 0.5f);
            float right = Mathf.Min(_right, rect.width - left);
            float period = Mathf.Max(0.01f, _nativeWidth - _left - _right);
            if (left > 0f) Quad(mesh, rect, 0f, left, 0f, _left / _nativeWidth);
            float end = rect.width - right;
            for (float x = left; x < end; )
            {
                float next = Mathf.Min(x + period, end);
                Quad(mesh, rect, x, next, _left / _nativeWidth, (_left + next - x) / _nativeWidth);
                x = next;
            }
            if (right > 0f) Quad(mesh, rect, end, rect.width, 1f - _right / _nativeWidth, 1f);
        }

        private void Quad(VertexHelper mesh, Rect rect, float x0, float x1, float u0, float u1)
        {
            int first = mesh.currentVertCount;
            u0 = _uv.x + _uv.width * u0; u1 = _uv.x + _uv.width * u1;
            mesh.AddVert(new Vector3(rect.xMin + x0, rect.yMin), color, new Vector2(u0, _uv.y));
            mesh.AddVert(new Vector3(rect.xMin + x0, rect.yMax), color, new Vector2(u0, _uv.y + _uv.height));
            mesh.AddVert(new Vector3(rect.xMin + x1, rect.yMax), color, new Vector2(u1, _uv.y + _uv.height));
            mesh.AddVert(new Vector3(rect.xMin + x1, rect.yMin), color, new Vector2(u1, _uv.y));
            mesh.AddTriangle(first, first + 1, first + 2); mesh.AddTriangle(first + 2, first + 3, first);
        }
    }
}
