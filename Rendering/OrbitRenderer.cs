using System;
using Microsoft.Xna.Framework;
using Solar.Core;
using Solar.Physics;

namespace Solar.Rendering
{
    /// <summary>Samples conics into screen-space line strips (with far-offscreen culling).</summary>
    public static class OrbitRenderer
    {
        private const double CullMargin = 4000;

        /// <summary>Draw the full conic (ellipse, or hyperbola clipped to maxR) centered on primaryAbs.</summary>
        public static void DrawConic(PrimitiveBatch pb, Camera2D cam, in OrbitalElements el,
                                     Vec2d primaryAbs, Color col, float width = 1.5f,
                                     double maxR = double.PositiveInfinity, int n = 256)
        {
            double p = el.SemiLatus;
            double nuMax = Math.PI;
            bool closed = !el.Hyperbolic && el.Apoapsis <= maxR;
            if (!closed)
            {
                double rLim = el.Hyperbolic ? maxR : Math.Min(maxR, el.Apoapsis);
                if (double.IsInfinity(rLim)) rLim = el.Periapsis * 1e4;
                double cnu = (p / rLim - 1) / el.E;
                nuMax = cnu >= 1 ? 0.1 : cnu <= -1 ? Math.PI - 1e-3 : Math.Acos(cnu);
            }

            Vec2d prev = default; bool prevValid = false; Vector2 prevV = default;
            for (int s = 0; s <= n; s++)
            {
                double nu = closed
                    ? -Math.PI + 2 * Math.PI * s / n
                    : -nuMax + 2 * nuMax * s / n;
                double r = p / (1 + el.E * Math.Cos(nu));
                if (r <= 0) { prevValid = false; continue; }
                Vec2d world = primaryAbs + Vec2d.FromAngle(el.ArgPe + el.Dir * nu) * r;
                Vec2d sD = cam.WorldToScreenD(world);
                bool valid = cam.OnScreen(sD, CullMargin);
                var v = new Vector2((float)sD.X, (float)sD.Y);
                if (prevValid || valid)
                {
                    if (s > 0) pb.Line(prevV, v, width, col);
                }
                prev = sD; prevValid = valid; prevV = v;
            }
        }

        /// <summary>Draw a glowing conic (3 passes: wide faint to thin bright).</summary>
        public static void DrawConicGlow(PrimitiveBatch pb, Camera2D cam, in OrbitalElements el,
                                         Vec2d primaryAbs, Color col, double maxR = double.PositiveInfinity)
        {
            DrawConic(pb, cam, el, primaryAbs, col * 0.16f, 6f, maxR, 192);
            DrawConic(pb, cam, el, primaryAbs, col * 0.38f, 3f, maxR, 192);
            DrawConic(pb, cam, el, primaryAbs, col, 1.5f, maxR, 256);
        }

        /// <summary>Draw a trajectory sampled uniformly in time from ut0 to ut1 (used for prediction segments).</summary>
        public static void DrawTrajectory(PrimitiveBatch pb, Camera2D cam, in OrbitalElements el,
                                          Vec2d primaryAbs, double ut0, double ut1, Color col,
                                          float width = 1.5f, int n = 200)
        {
            Vector2 prevV = default; bool prevValid = false; bool hasPrev = false;
            for (int s = 0; s <= n; s++)
            {
                double t = ut0 + (ut1 - ut0) * s / n;
                Vec2d world = primaryAbs + Kepler.StateAtTime(el, t).pos;
                Vec2d sD = cam.WorldToScreenD(world);
                bool valid = cam.OnScreen(sD, CullMargin);
                var v = new Vector2((float)sD.X, (float)sD.Y);
                if (hasPrev && (prevValid || valid)) pb.Line(prevV, v, width, col);
                prevV = v; prevValid = valid; hasPrev = true;
            }
        }

        /// <summary>World position of periapsis/apoapsis markers for a conic around primaryAbs.</summary>
        public static Vec2d PeriapsisPoint(in OrbitalElements el, Vec2d primaryAbs) =>
            primaryAbs + Vec2d.FromAngle(el.ArgPe) * el.Periapsis;

        public static Vec2d ApoapsisPoint(in OrbitalElements el, Vec2d primaryAbs) =>
            primaryAbs + Vec2d.FromAngle(el.ArgPe + Math.PI) * el.Apoapsis;
    }
}
