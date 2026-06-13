using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Solar.Rendering
{
    /// <summary>
    /// Immediate-mode filled-shape renderer in screen pixel coordinates.
    /// Everything is buffered as a triangle list and flushed in End() (or when full),
    /// so draw order within a batch is preserved.
    /// </summary>
    public sealed class PrimitiveBatch : IDisposable
    {
        private readonly GraphicsDevice _gd;
        private readonly BasicEffect _fx;
        private readonly BasicEffect _texFx;
        private VertexPositionColor[] _v = new VertexPositionColor[18000];
        private readonly VertexPositionColorTexture[] _tv = new VertexPositionColorTexture[6];
        private int _n;

        public PrimitiveBatch(GraphicsDevice gd)
        {
            _gd = gd;
            _fx = new BasicEffect(gd) { VertexColorEnabled = true, TextureEnabled = false, LightingEnabled = false };
            _texFx = new BasicEffect(gd) { VertexColorEnabled = true, TextureEnabled = true, LightingEnabled = false };
        }

        public void Begin(BlendState blend = null)
        {
            var vp = _gd.Viewport;
            var proj = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, 1);
            _fx.Projection = proj; _fx.View = Matrix.Identity; _fx.World = Matrix.Identity;
            _texFx.Projection = proj; _texFx.View = Matrix.Identity; _texFx.World = Matrix.Identity;
            _gd.BlendState = blend ?? BlendState.NonPremultiplied;
            _gd.RasterizerState = RasterizerState.CullNone;
            _gd.DepthStencilState = DepthStencilState.None;
            _n = 0;
        }

        public void End() => Flush();

        private void Flush()
        {
            if (_n == 0) return;
            _fx.CurrentTechnique.Passes[0].Apply();
            _gd.DrawUserPrimitives(PrimitiveType.TriangleList, _v, 0, _n / 3);
            _n = 0;
        }

        private void Ensure(int add)
        {
            if (_n + add > _v.Length) Flush();
            if (add > _v.Length) Array.Resize(ref _v, add + 3000);
        }

        /// <summary>Draw a textured quad (corners CW from top-left a,b,c,d; UVs 0..1), tinted.
        /// Flushes the pending color triangles first so the sprite layers correctly within the
        /// same pass — no separate SpriteBatch needed and world z-order is preserved.</summary>
        public void TexturedQuad(Texture2D tex, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color tint, bool flipX = false)
        {
            if (tex == null) return;
            Flush();
            float u0 = flipX ? 1 : 0, u1 = flipX ? 0 : 1;   // mirror horizontally by swapping the U coords
            _tv[0] = new VertexPositionColorTexture(new Vector3(a, 0), tint, new Vector2(u0, 0));
            _tv[1] = new VertexPositionColorTexture(new Vector3(b, 0), tint, new Vector2(u1, 0));
            _tv[2] = new VertexPositionColorTexture(new Vector3(c, 0), tint, new Vector2(u1, 1));
            _tv[3] = new VertexPositionColorTexture(new Vector3(a, 0), tint, new Vector2(u0, 0));
            _tv[4] = new VertexPositionColorTexture(new Vector3(c, 0), tint, new Vector2(u1, 1));
            _tv[5] = new VertexPositionColorTexture(new Vector3(d, 0), tint, new Vector2(u0, 1));
            _texFx.Texture = tex;
            _texFx.CurrentTechnique.Passes[0].Apply();
            _gd.DrawUserPrimitives(PrimitiveType.TriangleList, _tv, 0, 2);
        }

        public void Tri(Vector2 a, Vector2 b, Vector2 c, Color col) => Tri(a, b, c, col, col, col);

        public void Tri(Vector2 a, Vector2 b, Vector2 c, Color ca, Color cb, Color cc)
        {
            Ensure(3);
            _v[_n++] = new VertexPositionColor(new Vector3(a, 0), ca);
            _v[_n++] = new VertexPositionColor(new Vector3(b, 0), cb);
            _v[_n++] = new VertexPositionColor(new Vector3(c, 0), cc);
        }

        public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col)
        {
            Tri(a, b, c, col);
            Tri(a, c, d, col);
        }

        public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color ca, Color cb, Color cc, Color cd)
        {
            Tri(a, b, c, ca, cb, cc);
            Tri(a, c, d, ca, cc, cd);
        }

        public void FillRect(float x, float y, float w, float h, Color col) =>
            Quad(new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h), col);

        public void FillRect(Rectangle r, Color col) => FillRect(r.X, r.Y, r.Width, r.Height, col);

        public void RectOutline(Rectangle r, float t, Color col)
        {
            FillRect(r.X, r.Y, r.Width, t, col);
            FillRect(r.X, r.Bottom - t, r.Width, t, col);
            FillRect(r.X, r.Y + t, t, r.Height - 2 * t, col);
            FillRect(r.Right - t, r.Y + t, t, r.Height - 2 * t, col);
        }

        public void Line(Vector2 a, Vector2 b, float width, Color col)
        {
            var d = b - a;
            float l = d.Length();
            if (l < 1e-5f) return;
            var n = new Vector2(-d.Y, d.X) * (width * 0.5f / l);
            Quad(a + n, b + n, b - n, a - n, col);
        }

        public void LineStrip(List<Vector2> pts, float width, Color col)
        {
            for (int i = 1; i < pts.Count; i++) Line(pts[i - 1], pts[i], width, col);
        }

        public static int AutoSegments(float r) => Math.Clamp((int)(r * 0.7f) + 10, 14, 180);

        public void FillCircle(Vector2 c, float r, Color col, int segs = 0) => FillCircle(c, r, col, col, segs);

        public void FillCircle(Vector2 c, float r, Color center, Color edge, int segs = 0)
        {
            if (r <= 0) return;
            if (segs <= 0) segs = AutoSegments(r);
            Vector2 prev = c + new Vector2(r, 0);
            for (int s = 1; s <= segs; s++)
            {
                double a = 2 * Math.PI * s / segs;
                Vector2 p = c + new Vector2((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a)));
                Tri(c, prev, p, center, edge, edge);
                prev = p;
            }
        }

        public void Ring(Vector2 c, float rIn, float rOut, Color colIn, Color colOut, int segs = 0)
        {
            if (segs <= 0) segs = AutoSegments(rOut);
            RingArc(c, rIn, rOut, 0, (float)(2 * Math.PI), colIn, colOut, segs);
        }

        public void RingArc(Vector2 c, float rIn, float rOut, float a0, float a1, Color colIn, Color colOut, int segs)
        {
            Vector2 pi0 = c + rIn * new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0));
            Vector2 po0 = c + rOut * new Vector2((float)Math.Cos(a0), (float)Math.Sin(a0));
            for (int s = 1; s <= segs; s++)
            {
                float a = a0 + (a1 - a0) * s / segs;
                var dir = new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
                Vector2 pi1 = c + rIn * dir;
                Vector2 po1 = c + rOut * dir;
                Quad(pi0, po0, po1, pi1, colIn, colOut, colOut, colIn);
                pi0 = pi1; po0 = po1;
            }
        }

        public void CircleOutline(Vector2 c, float r, float width, Color col, int segs = 0)
        {
            float hw = width * 0.5f;
            if (segs <= 0) segs = AutoSegments(r);
            RingArc(c, r - hw, r + hw, 0, (float)(2 * Math.PI), col, col, segs);
        }

        public void DashedCircleOutline(Vector2 c, float r, float width, Color col, int dashes = 48)
        {
            float hw = width * 0.5f;
            for (int d = 0; d < dashes; d++)
            {
                float a0 = (float)(2 * Math.PI * d / dashes);
                float a1 = (float)(2 * Math.PI * (d + 0.55f) / dashes);
                RingArc(c, r - hw, r + hw, a0, a1, col, col, 3);
            }
        }

        public void Dispose() { _fx.Dispose(); _texFx.Dispose(); }
    }
}
