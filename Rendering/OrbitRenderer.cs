using System;
using Microsoft.Xna.Framework;
using Solar.Core;
using Solar.Physics;

namespace Solar.Rendering
{
    /// <summary>Samples conics into screen-space line strips. Each base segment is recursively
    /// subdivided so the arc passing through the viewport is drawn accurately at any zoom (a fixed
    /// step would leave huge gaps when zoomed onto a small part of a large orbit), while segments
    /// whose screen bounding-box can't touch the viewport are culled whole.</summary>
    public static class OrbitRenderer
    {
        private const double CullMargin = 4000;
        private const int MaxDepth = 14;     // subdivision cap per base segment
        private const double SplitPx = 24;   // stop subdividing once a segment is this short on screen

        /// <summary>Draw the full conic (ellipse, or hyperbola clipped to maxR) centered on primaryAbs.</summary>
        public static void DrawConic(PrimitiveBatch pb, Camera2D cam, in OrbitalElements el,
                                     Vec2d primaryAbs, Color col, float width = 1.5f,
                                     double maxR = double.PositiveInfinity, int n = 256)
        {
            var ce = el;   // 'in' params can't be captured by the local position function
            double p = ce.SemiLatus;
            double nuMax = Math.PI;
            bool closed = !ce.Hyperbolic && ce.Apoapsis <= maxR;
            if (!closed)
            {
                double rLim = ce.Hyperbolic ? maxR : Math.Min(maxR, ce.Apoapsis);
                if (double.IsInfinity(rLim)) rLim = ce.Periapsis * 1e4;
                double cnu = (p / rLim - 1) / ce.E;
                nuMax = cnu >= 1 ? 0.1 : cnu <= -1 ? Math.PI - 1e-3 : Math.Acos(cnu);
            }

            Vec2d At(double nu)
            {
                double r = p / (1 + ce.E * Math.Cos(nu));
                return primaryAbs + Vec2d.FromAngle(ce.ArgPe + ce.Dir * nu) * r;
            }
            EmitCurve(pb, cam, At, closed ? -Math.PI : -nuMax, closed ? Math.PI : nuMax, col, width, n);
        }

        /// <summary>Walk n base intervals over [pLo, pHi], recursively subdividing each toward the
        /// viewport. <paramref name="at"/> maps a curve parameter to a world position.</summary>
        private static void EmitCurve(PrimitiveBatch pb, Camera2D cam, Func<double, Vec2d> at,
                                      double pLo, double pHi, Color col, float width, int n)
        {
            double a = pLo;
            Vec2d sa = cam.WorldToScreenD(at(a));
            for (int s = 1; s <= n; s++)
            {
                double b = pLo + (pHi - pLo) * s / n;
                Vec2d sb = cam.WorldToScreenD(at(b));
                Subdivide(pb, cam, at, a, sa, b, sb, col, width, MaxDepth);
                a = b; sa = sb;
            }
        }

        private static void Subdivide(PrimitiveBatch pb, Camera2D cam, Func<double, Vec2d> at,
                                      double pa, Vec2d sa, double pb_, Vec2d sb, Color col, float width, int depth)
        {
            if (!SegNearViewport(cam, sa, sb)) return;   // screen bbox can't reach the viewport
            double dx = sb.X - sa.X, dy = sb.Y - sa.Y;
            if (depth <= 0 || dx * dx + dy * dy <= SplitPx * SplitPx)
            {
                pb.Line(new Vector2((float)sa.X, (float)sa.Y), new Vector2((float)sb.X, (float)sb.Y), width, col);
                return;
            }
            double pm = 0.5 * (pa + pb_);
            Vec2d sm = cam.WorldToScreenD(at(pm));
            Subdivide(pb, cam, at, pa, sa, pm, sm, col, width, depth - 1);
            Subdivide(pb, cam, at, pm, sm, pb_, sb, col, width, depth - 1);
        }

        /// <summary>Whether the screen-space bounding box of a segment overlaps the viewport (plus margin).
        /// A segment that straddles the screen with both endpoints far off-screen still passes.</summary>
        private static bool SegNearViewport(Camera2D cam, Vec2d a, Vec2d b)
        {
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
            double minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            return maxX > -CullMargin && minX < cam.ScreenW + CullMargin
                && maxY > -CullMargin && minY < cam.ScreenH + CullMargin;
        }

        /// <summary>Draw a glowing conic (3 passes: wide faint to thin bright).</summary>
        public static void DrawConicGlow(PrimitiveBatch pb, Camera2D cam, in OrbitalElements el,
                                         Vec2d primaryAbs, Color col, double maxR = double.PositiveInfinity)
        {
            DrawConic(pb, cam, el, primaryAbs, col * 0.16f, 6f, maxR, 192);
            DrawConic(pb, cam, el, primaryAbs, col * 0.38f, 3f, maxR, 192);
            DrawConic(pb, cam, el, primaryAbs, col, 1.5f, maxR, 256);
        }

        /// <summary>Draw a trajectory sampled in time from ut0 to ut1 (used for prediction segments),
        /// with the same adaptive subdivision as <see cref="DrawConic"/> so it stays visible at any zoom.</summary>
        public static void DrawTrajectory(PrimitiveBatch pb, Camera2D cam, in OrbitalElements el,
                                          Vec2d primaryAbs, double ut0, double ut1, Color col,
                                          float width = 1.5f, int n = 200)
        {
            var ce = el;
            Vec2d At(double t) => primaryAbs + Kepler.StateAtTime(ce, t).pos;
            EmitCurve(pb, cam, At, ut0, ut1, col, width, n);
        }

        /// <summary>Place a few chevrons along the conic pointing in the direction of travel (prograde),
        /// so the player can read which way a body or vessel is moving along its orbit.</summary>
        public static void DrawDirectionArrows(PrimitiveBatch pb, Camera2D cam, in OrbitalElements el,
                                               Vec2d primaryAbs, Color col, int count = 6)
        {
            double p = el.SemiLatus;
            double start, span;
            if (!el.Hyperbolic) { start = -Math.PI; span = 2 * Math.PI; }
            else { double nuInf = Math.Acos(-1 / el.E); start = -0.6 * nuInf; span = 1.2 * nuInf; }

            for (int i = 0; i < count; i++)
            {
                double nu = start + span * (i + 0.5) / count;
                double r = p / (1 + el.E * Math.Cos(nu));
                if (r <= 0) continue;
                Vec2d world = primaryAbs + Vec2d.FromAngle(el.ArgPe + el.Dir * nu) * r;
                Vec2d sD = cam.WorldToScreenD(world);
                if (!cam.OnScreen(sD, 0)) continue;

                var (_, vel) = Kepler.StateAtTrueAnomaly(el, nu, r);
                var d = new Vector2((float)vel.X, -(float)vel.Y);   // world dir -> screen (Y flipped)
                float dl = d.Length();
                if (dl < 1e-9f) continue;
                d /= dl;
                var perp = new Vector2(-d.Y, d.X);
                var tip = new Vector2((float)sD.X, (float)sD.Y) + d * 5f;
                var back = tip - d * 9f;
                pb.Line(back + perp * 5f, tip, 1.6f, col);
                pb.Line(back - perp * 5f, tip, 1.6f, col);
            }
        }

        /// <summary>World position of periapsis/apoapsis markers for a conic around primaryAbs.</summary>
        public static Vec2d PeriapsisPoint(in OrbitalElements el, Vec2d primaryAbs) =>
            primaryAbs + Vec2d.FromAngle(el.ArgPe) * el.Periapsis;

        public static Vec2d ApoapsisPoint(in OrbitalElements el, Vec2d primaryAbs) =>
            primaryAbs + Vec2d.FromAngle(el.ArgPe + Math.PI) * el.Apoapsis;
    }
}
