using System;
using Microsoft.Xna.Framework;
using Solar.Core;

namespace Solar.Rendering
{
    /// <summary>
    /// A short world-space ribbon of exhaust puffs trailing a firing engine, drawn only in flight view.
    /// Puffs are emitted at the nozzle, drift in world space (so the camera-tracked ship leaves them
    /// behind as a trail), grow and fade over ~1s. Denser/longer-lived in atmosphere, thin and faint in
    /// vacuum. A fixed-size ring buffer keeps it allocation-free per frame.
    /// </summary>
    public sealed class ExhaustTrail
    {
        private struct Puff { public Vec2d Pos, Vel; public double Born; public float Seed; public bool Live; }

        private readonly Puff[] _p;
        private int _next;
        private double _lastUt = double.NaN;
        private readonly Random _rnd = new(11);

        public ExhaustTrail(int capacity = 200) { _p = new Puff[capacity]; }

        /// <summary>Advance and draw the trail. <paramref name="emitDir"/> is the world-space unit vector the
        /// gas travels (opposite thrust). <paramref name="density"/> in [0,1] is the local atmospheric
        /// fraction; 0 = vacuum. Pass throttle 0 (or no thrust) to let the trail fade out without emitting.</summary>
        public void Draw(PrimitiveBatch pb, Camera2D cam, double ut, Vec2d emitPos, Vec2d emitDir,
                         Vec2d vesselVel, double throttle, Color color, double density)
        {
            double dt = double.IsNaN(_lastUt) ? 0 : ut - _lastUt;
            _lastUt = ut;
            // Under time warp dt balloons; the trail is only meaningful off-rails at ~real time, so clamp.
            if (dt < 0 || dt > 0.5) dt = 0;

            double maxLife = 0.5 + 0.7 * density;          // longer-lived plumes in thick air
            float ejectSpeed = 12f;                        // m/s spread of gas along the exhaust axis

            // Emit: more puffs at higher throttle and in denser atmosphere.
            if (throttle > 0.01 && dt > 0)
            {
                int emit = 1 + (int)(throttle * (1 + 2 * density));
                for (int e = 0; e < emit; e++)
                {
                    ref var pf = ref _p[_next];
                    _next = (_next + 1) % _p.Length;
                    double jx = (_rnd.NextDouble() * 2 - 1), jy = (_rnd.NextDouble() * 2 - 1);
                    var lateral = new Vec2d(-emitDir.Y, emitDir.X) * (jx * 1.5);
                    pf.Pos = emitPos + lateral;
                    pf.Vel = vesselVel + emitDir * (ejectSpeed * (0.6 + 0.6 * _rnd.NextDouble())) + lateral * 2.0 + new Vec2d(0, jy * 2);
                    pf.Born = ut;
                    pf.Seed = (float)_rnd.NextDouble();
                    pf.Live = true;
                }
            }

            // Smoke gets sootier (toward gray) in atmosphere; in vacuum it stays the engine's exhaust color.
            Color smoke = Color.Lerp(color, new Color(90, 90, 95), (float)(0.55 * density));

            for (int i = 0; i < _p.Length; i++)
            {
                ref var pf = ref _p[i];
                if (!pf.Live) continue;
                double age = ut - pf.Born;
                if (age < 0 || age >= maxLife) { pf.Live = false; continue; }
                if (dt > 0) pf.Pos += pf.Vel * dt;

                float life = (float)(age / maxLife);      // 0 fresh -> 1 dead
                var s = cam.WorldToScreen(pf.Pos);
                if (s.X < -16 || s.X > cam.ScreenW + 16 || s.Y < -16 || s.Y > cam.ScreenH + 16) continue;

                float grow = 0.5f + 2.2f * life;          // meters; expands as it ages
                float rPx = (float)(grow / cam.MetersPerPixel);
                if (rPx < 0.6f) rPx = 0.6f;
                // bright and tight when fresh, fading translucent as it dissipates; vacuum trails are fainter
                float a0 = (float)(0.85 - 0.5 * density);
                int alpha = (int)(MathHelper.Clamp((1f - life) * (0.4f + 0.6f * (float)density + a0 * (1 - (float)density)), 0f, 1f) * 200);
                if (alpha <= 3) continue;
                var c = smoke * (alpha / 255f);
                pb.FillCircle(s, rPx, c, c * 0.2f);
            }
        }
    }
}
