using System;
using Microsoft.Xna.Framework;
using Solar.Core;

namespace Solar.Rendering
{
    /// <summary>Fixed background starfield with a hint of parallax drift at interplanetary scales.</summary>
    public sealed class StarfieldRenderer
    {
        private struct Star { public float X, Y, Size; public Color C; }
        private readonly Star[] _stars;

        public StarfieldRenderer(int count = 420, int seed = 1234)
        {
            var rnd = new Random(seed);
            _stars = new Star[count];
            for (int i = 0; i < count; i++)
            {
                float b = 0.25f + (float)rnd.NextDouble() * 0.75f;
                int tint = rnd.Next(3);
                var c = new Color(
                    (int)(b * (tint == 1 ? 200 : 255)),
                    (int)(b * (tint == 2 ? 220 : 245)),
                    (int)(b * 255));
                _stars[i] = new Star
                {
                    X = (float)rnd.NextDouble(),
                    Y = (float)rnd.NextDouble(),
                    Size = 1f + (float)rnd.NextDouble() * 1.4f,
                    C = c,
                };
            }
        }

        public void Draw(PrimitiveBatch pb, int w, int h, Vec2d camCenter)
        {
            // tiny parallax: stars shift only over many Gm of camera travel
            double ox = camCenter.X * 4e-10, oy = -camCenter.Y * 4e-10;
            foreach (var s in _stars)
            {
                double x = s.X + ox, y = s.Y + oy;
                x -= Math.Floor(x); y -= Math.Floor(y);
                pb.FillRect((float)(x * w), (float)(y * h), s.Size, s.Size, s.C);
            }
        }
    }
}
