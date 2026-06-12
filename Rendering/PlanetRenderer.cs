using System;
using Microsoft.Xna.Framework;
using Solar.Core;
using Solar.Physics;

namespace Solar.Rendering
{
    /// <summary>Draws celestial bodies as shaded discs (or a horizon arc when extremely zoomed in).</summary>
    public static class PlanetRenderer
    {
        private const double ArcModeRadiusPx = 3500;

        public static void Draw(PrimitiveBatch pb, Camera2D cam, CelestialBody b, double ut, bool mapMode,
                                Microsoft.Xna.Framework.Graphics.SpriteBatch sb = null,
                                Microsoft.Xna.Framework.Graphics.Texture2D tex = null)
        {
            Vec2d pos = b.AbsolutePositionAt(ut);
            Vec2d sD = cam.WorldToScreenD(pos);
            double rPx = b.Radius / cam.MetersPerPixel;

            if (!cam.OnScreen(sD, rPx + (b.Atmo != null ? b.Atmo.Top / cam.MetersPerPixel : 0) + 60))
                return;

            if (rPx <= ArcModeRadiusPx)
            {
                var s = new Vector2((float)sD.X, (float)sD.Y);
                if (rPx < 1.3)
                {
                    // sub-pixel body: draw an icon dot so it stays visible in map view
                    pb.FillCircle(s, mapMode ? 2.5f : 1.5f, b.BodyColor);
                    return;
                }
                if (b.Atmo != null)
                {
                    float atmoPx = (float)((b.Radius + b.Atmo.Top) / cam.MetersPerPixel);
                    pb.FillCircle(s, atmoPx, b.AtmoColor * 0.45f, Color.Transparent);
                }
                // A body texture (round PNG with transparent corners) replaces the procedural disc when
                // one is loaded for this body's id; otherwise fall back to the flat-shaded circle.
                if (tex != null && sb != null)
                {
                    float d = (float)rPx * 2f;
                    sb.Draw(tex, new Rectangle((int)(s.X - rPx), (int)(s.Y - rPx), (int)d, (int)d), Color.White);
                }
                else
                {
                    pb.FillCircle(s, (float)rPx, Lighten(b.BodyColor, 0.30f), Darken(b.BodyColor, 0.35f));
                    if (!mapMode && rPx > 50) DrawDiscFeatures(pb, s, (float)rPx, b);
                }
            }
            else
            {
                DrawHorizonArc(pb, cam, b, pos);
            }
        }

        /// <summary>Deterministic craters/highlights on a body disc (fixed in world space → they parallax).</summary>
        private static void DrawDiscFeatures(PrimitiveBatch pb, Vector2 s, float rPx, CelestialBody b)
        {
            var rnd = new Random(b.Name.GetHashCode());
            Color dark = Darken(b.BodyColor, 0.5f);
            Color light = Lighten(b.BodyColor, 0.18f);
            for (int i = 0; i < 9; i++)
            {
                double a = rnd.NextDouble() * Math.PI * 2;
                double dist = (0.12 + 0.82 * rnd.NextDouble()) * rPx;
                float cr = (float)((0.04 + 0.09 * rnd.NextDouble()) * rPx);
                if (dist + cr > rPx) dist = rPx - cr;
                var c = s + new Vector2((float)Math.Cos(a), -(float)Math.Sin(a)) * (float)dist;
                pb.FillCircle(c, cr, (i % 3 == 0) ? light : dark);
            }
        }

        /// <summary>When the planet disc is huge, draw only the visible part of the surface and atmosphere band.</summary>
        private static void DrawHorizonArc(PrimitiveBatch pb, Camera2D cam, CelestialBody b, Vec2d pos)
        {
            double diagWorld = Math.Sqrt((double)cam.ScreenW * cam.ScreenW + (double)cam.ScreenH * cam.ScreenH) * cam.MetersPerPixel;
            Vec2d toCam = cam.Center - pos;
            double thetaC = toCam.Angle();
            double half = Math.Min(Math.PI, 2.5 * diagWorld / b.Radius + 0.01);
            int segs = 110;

            // atmosphere band (surface -> top, fading out)
            if (b.Atmo != null)
                ArcBand(pb, cam, pos, b.Radius, b.Radius + b.Atmo.Top, thetaC, half, segs,
                        b.AtmoColor * 0.5f, Color.Transparent);

            // ground band (deep enough to cover the lower screen)
            double depth = Math.Min(b.Radius, diagWorld * 2.5);
            ArcBand(pb, cam, pos, b.Radius - depth, b.Radius, thetaC, half, segs,
                    Darken(b.BodyColor, 0.55f), b.BodyColor);

            DrawHorizonDetail(pb, cam, b, pos, thetaC, half);
        }

        /// <summary>Darker terrain streaks at fixed world longitudes so the surface streams past at low altitude.</summary>
        private static void DrawHorizonDetail(PrimitiveBatch pb, Camera2D cam, CelestialBody b, Vec2d pos, double thetaC, double half)
        {
            const double step = 0.0025; // rad between candidate features (world-fixed)
            long k0 = (long)Math.Floor((thetaC - half) / step);
            long k1 = (long)Math.Ceiling((thetaC + half) / step);
            if (k1 - k0 > 600) return; // too zoomed out to be a useful cue
            Color dark = Darken(b.BodyColor, 0.5f);
            for (long k = k0; k <= k1; k++)
            {
                var rnd = new Random(unchecked((int)(k * 2654435761) ^ b.Name.GetHashCode()));
                if (rnd.NextDouble() > 0.5) continue; // sparse
                double a = k * step;
                double h = (0.0006 + 0.0016 * rnd.NextDouble()) * b.Radius;
                Vec2d dir = Vec2d.FromAngle(a);
                Vector2 baseS = cam.WorldToScreen(pos + dir * b.Radius);
                Vector2 inS = cam.WorldToScreen(pos + dir * (b.Radius - h));
                pb.Line(baseS, inS, 1.5f, dark);
            }
        }

        private static void ArcBand(PrimitiveBatch pb, Camera2D cam, Vec2d center, double rIn, double rOut,
                                    double thetaC, double half, int segs, Color colIn, Color colOut)
        {
            Vector2 pi0 = default, po0 = default;
            for (int s = 0; s <= segs; s++)
            {
                double a = thetaC - half + 2 * half * s / segs;
                Vec2d dir = Vec2d.FromAngle(a);
                Vector2 pi1 = cam.WorldToScreen(center + dir * rIn);
                Vector2 po1 = cam.WorldToScreen(center + dir * rOut);
                if (s > 0) pb.Quad(pi0, po0, po1, pi1, colIn, colOut, colOut, colIn);
                pi0 = pi1; po0 = po1;
            }
        }

        public static Color Lighten(Color c, float f) => Color.Lerp(c, Color.White, f);
        public static Color Darken(Color c, float f) => Color.Lerp(c, Color.Black, f);
    }
}
