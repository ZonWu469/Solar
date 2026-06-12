using System;
using Microsoft.Xna.Framework;
using Solar.Core;

namespace Solar.Rendering
{
    /// <summary>
    /// A near-field layer of world-space dust motes drawn only in flight view. Because the
    /// camera tracks the vessel, the motes (fixed in world space) stream past, and each is
    /// drawn as a motion-blur streak along the velocity axis whose length scales with speed —
    /// the primary cue of motion in an otherwise empty, ship-centred frame.
    /// </summary>
    public sealed class DustField
    {
        private readonly Vec2d[] _pos;
        private readonly Random _rnd;
        private bool _init;

        public DustField(int count = 70, int seed = 7)
        {
            _pos = new Vec2d[count];
            _rnd = new Random(seed);
        }

        public void Draw(PrimitiveBatch pb, Camera2D cam, Vec2d velocity)
        {
            double hx = cam.ScreenW * cam.MetersPerPixel * 0.75;
            double hy = cam.ScreenH * cam.MetersPerPixel * 0.75;
            if (hx <= 0 || hy <= 0) return;

            if (!_init)
            {
                for (int i = 0; i < _pos.Length; i++)
                    _pos[i] = cam.Center + new Vec2d((_rnd.NextDouble() * 2 - 1) * hx, (_rnd.NextDouble() * 2 - 1) * hy);
                _init = true;
            }

            double speed = velocity.Length;
            Vec2d vdir = speed > 1e-6 ? velocity.Normalized() : new Vec2d(0, 1);
            var streakScreen = new Vector2((float)vdir.X, -(float)vdir.Y); // world->screen flips Y
            double pxPerSec = speed / cam.MetersPerPixel;
            float streakLen = (float)Math.Clamp(pxPerSec * 0.03, 0, 44);
            int alpha = (int)Math.Clamp(40 + streakLen * 3, 40, 170);
            var col = new Color(150, 170, 200, alpha);

            for (int i = 0; i < _pos.Length; i++)
            {
                // toroidal wrap into the window centred on the camera (keeps density constant)
                Vec2d rel = _pos[i] - cam.Center;
                if (rel.X > hx) _pos[i].X -= 2 * hx; else if (rel.X < -hx) _pos[i].X += 2 * hx;
                if (rel.Y > hy) _pos[i].Y -= 2 * hy; else if (rel.Y < -hy) _pos[i].Y += 2 * hy;

                var s = cam.WorldToScreen(_pos[i]);
                if (s.X < -8 || s.X > cam.ScreenW + 8 || s.Y < -8 || s.Y > cam.ScreenH + 8) continue;

                if (streakLen < 2f) pb.FillRect(s.X, s.Y, 1.4f, 1.4f, col);
                else pb.Line(s, s - streakScreen * streakLen, 1.4f, col);
            }
        }
    }
}
