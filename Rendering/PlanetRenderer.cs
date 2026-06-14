using System;
using Microsoft.Xna.Framework;
using Solar.Core;
using Solar.Physics;

namespace Solar.Rendering
{
    /// <summary>Draws celestial bodies: an organic terrain silhouette (or a horizon arc when extremely
    /// zoomed in), colored by elevation or slope and lit from the sun.</summary>
    public static class PlanetRenderer
    {
        private const double ArcModeRadiusPx = 3500;
        private const double Ambient = 0.30;   // night-side brightness (0 = black, 1 = no shading)

        public static void Draw(PrimitiveBatch pb, Camera2D cam, CelestialBody b, double ut, bool mapMode,
                                Microsoft.Xna.Framework.Graphics.SpriteBatch sb = null,
                                Microsoft.Xna.Framework.Graphics.Texture2D tex = null,
                                Vec2d sunPos = default, bool slopeOverlay = false)
        {
            Vec2d pos = b.AbsolutePositionAt(ut);
            Vec2d sD = cam.WorldToScreenD(pos);
            double rPx = b.Radius / cam.MetersPerPixel;

            if (!cam.OnScreen(sD, rPx + (b.Atmo != null ? b.Atmo.Top / cam.MetersPerPixel : 0) + 60))
                return;

            // Sun direction at this body (the root star sits at the origin, so the default is correct).
            Vec2d sunDir = (sunPos - pos).Normalized();

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
                // A body texture (round PNG with transparent corners) replaces the procedural disc when one
                // is loaded; otherwise draw an organic terrain disc, or a flat-shaded circle if too small.
                double ampPx = (b.Terrain?.MaxAmplitude ?? 0) / cam.MetersPerPixel;
                if (tex != null && sb != null)
                {
                    float d = (float)rPx * 2f;
                    sb.Draw(tex, new Rectangle((int)(s.X - rPx), (int)(s.Y - rPx), (int)d, (int)d), Color.White);
                }
                else if (b.Terrain != null && rPx > 40 && ampPx > 0.8)
                {
                    DrawTerrainDisc(pb, cam, b, pos, s, rPx, sunDir, slopeOverlay);
                }
                else
                {
                    pb.FillCircle(s, (float)rPx, Lighten(b.BodyColor, 0.30f), Darken(b.BodyColor, 0.35f));
                }
            }
            else
            {
                DrawHorizonArc(pb, cam, b, pos, sunDir, slopeOverlay);
            }
        }

        /// <summary>Terrain disc: a triangle fan whose rim follows the height field, each rim segment colored
        /// by elevation band (or slope) and shaded by the sun. Used at mid zoom in both flight and map view.</summary>
        private static void DrawTerrainDisc(PrimitiveBatch pb, Camera2D cam, CelestialBody b, Vec2d pos,
                                            Vector2 center, double rPx, Vec2d sunDir, bool slopeOverlay)
        {
            int segs = Math.Clamp((int)(rPx * 0.5), 96, 256);
            Color core = Darken(b.BodyColor, 0.45f);   // interior filler under the colored rim
            Vector2 prev = default; Color prevCol = default;
            for (int s = 0; s <= segs; s++)
            {
                double a = Math.PI * 2 * s / segs;
                Vector2 rim = cam.WorldToScreen(pos + Vec2d.FromAngle(a, b.SurfaceRadiusAt(a)));
                Color col = SurfaceColor(b, a, sunDir, slopeOverlay);
                if (s > 0) pb.Tri(center, prev, rim, core, prevCol, col);
                prev = rim; prevCol = col;
            }
        }

        /// <summary>When the planet disc is huge, draw only the visible part of the surface (following the
        /// terrain) plus the atmosphere band.</summary>
        private static void DrawHorizonArc(PrimitiveBatch pb, Camera2D cam, CelestialBody b, Vec2d pos,
                                           Vec2d sunDir, bool slopeOverlay)
        {
            double diagWorld = Math.Sqrt((double)cam.ScreenW * cam.ScreenW + (double)cam.ScreenH * cam.ScreenH) * cam.MetersPerPixel;
            Vec2d toCam = cam.Center - pos;
            double thetaC = toCam.Angle();
            double half = Math.Min(Math.PI, 2.5 * diagWorld / b.Radius + 0.01);
            int segs = 200;

            // atmosphere band (surface -> top, fading out)
            if (b.Atmo != null)
                ArcBand(pb, cam, pos, b.Radius, b.Radius + b.Atmo.Top, thetaC, half, segs,
                        b.AtmoColor * 0.5f, Color.Transparent);

            // ground band: outer edge follows the terrain height field, colored per-segment.
            double depth = Math.Min(b.Radius, diagWorld * 2.5);
            Vector2 pi0 = default, po0 = default; Color c0 = default;
            for (int s = 0; s <= segs; s++)
            {
                double a = thetaC - half + 2 * half * s / segs;
                double rOut = b.Terrain != null ? b.SurfaceRadiusAt(a) : b.Radius;
                Vec2d dir = Vec2d.FromAngle(a);
                Vector2 pi1 = cam.WorldToScreen(pos + dir * (rOut - depth));
                Vector2 po1 = cam.WorldToScreen(pos + dir * rOut);
                Color cOut = b.Terrain != null ? SurfaceColor(b, a, sunDir, slopeOverlay)
                                               : Shade(b.BodyColor, Brightness(dir, sunDir));
                Color cIn = Darken(cOut, 0.5f);
                if (s > 0) pb.Quad(pi0, po0, po1, pi1, cIn, c0, cOut, cIn);
                pi0 = pi1; po0 = po1; c0 = cOut;
            }
        }

        /// <summary>Color for a surface point: elevation band (or slope tint when the overlay is on), then
        /// sun shading (day/night terminator).</summary>
        private static Color SurfaceColor(CelestialBody b, double angle, Vec2d sunDir, bool slopeOverlay)
        {
            Color baseCol;
            if (slopeOverlay && b.Terrain != null)
            {
                double s = b.Terrain.SlopeAt(angle) / Terrain.LandableSlope;   // 1 at the landable limit
                baseCol = s <= 1 ? Color.Lerp(new Color(70, 200, 95), new Color(225, 195, 70), (float)s)
                                 : Color.Lerp(new Color(225, 195, 70), new Color(215, 75, 60), (float)Math.Clamp(s - 1, 0, 1));
            }
            else
            {
                double norm = b.Terrain?.NormalizedHeight(angle) ?? 0;   // [-1,1]
                baseCol = norm < 0
                    ? Color.Lerp(b.BodyColor, Darken(b.BodyColor, 0.5f), (float)-norm)    // depressions: darker
                    : Color.Lerp(b.BodyColor, Lighten(b.BodyColor, 0.35f), (float)norm);  // highlands: lighter
            }
            return Shade(baseCol, Brightness(Vec2d.FromAngle(angle), sunDir));
        }

        // Day/night brightness for an outward surface normal lit from sunDir.
        private static double Brightness(Vec2d normal, Vec2d sunDir)
        {
            if (sunDir.LengthSquared < 1e-9) return 1;   // no sun reference: full bright
            double dot = normal.Normalized().Dot(sunDir);
            return Ambient + (1 - Ambient) * Math.Clamp(dot, 0, 1);
        }

        private static Color Shade(Color c, double b) =>
            new Color((int)(c.R * b), (int)(c.G * b), (int)(c.B * b), (int)c.A);

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
