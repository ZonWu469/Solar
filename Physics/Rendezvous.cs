using System;
using System.Collections.Generic;
using Solar.Core;

namespace Solar.Physics
{
    /// <summary>One geometric proximity between two orbit curves: the nearest point on your curve,
    /// the matching point on the target curve, their gap, and whether the curves actually cross
    /// there (gap ~ 0). Purely geometric (timing-independent), unlike <see cref="Rendezvous.ClosestApproach"/>.</summary>
    public readonly struct ProximityPoint
    {
        public readonly double NuYou;
        public readonly Vec2d YouPos;
        public readonly Vec2d TgtPos;
        public readonly double Sep;
        public readonly bool Intersect;
        public ProximityPoint(double nuYou, Vec2d youPos, Vec2d tgtPos, double sep, bool intersect)
        { NuYou = nuYou; YouPos = youPos; TgtPos = tgtPos; Sep = sep; Intersect = intersect; }
    }

    /// <summary>Closest-approach search between the player's (possibly planned) conic and a moving
    /// target, both evaluated in absolute coordinates so they may sit in different SOIs.</summary>
    public static class Rendezvous
    {
        /// <summary>Find the time of minimum separation over [utStart, utStart+window] between your
        /// orbit (around <paramref name="yourPrimary"/>) and the target position function. Returns
        /// the approach time, separation, and relative speed there.</summary>
        public static bool ClosestApproach(in OrbitalElements you, CelestialBody yourPrimary,
            Func<double, Vec2d> targetAbsPos, double utStart, double window,
            out double utCa, out double sep, out double relSpeed)
        {
            utCa = utStart; sep = double.MaxValue; relSpeed = 0;
            if (double.IsNaN(you.A) || window <= 0 || yourPrimary == null) return false;

            var el = you;
            Vec2d YouAbs(double t) => yourPrimary.AbsolutePositionAt(t) + Kepler.StateAtTime(el, t).pos;

            // coarse global sweep, then a local golden/ternary refine around the best sample
            const int n = 240;
            double bestT = utStart, bestD = double.MaxValue;
            for (int i = 0; i <= n; i++)
            {
                double t = utStart + window * i / n;
                double d = (YouAbs(t) - targetAbsPos(t)).Length;
                if (d < bestD) { bestD = d; bestT = t; }
            }

            double step = window / n;
            double lo = bestT - step, hi = bestT + step;
            for (int it = 0; it < 60; it++)
            {
                double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                double d1 = (YouAbs(m1) - targetAbsPos(m1)).Length;
                double d2 = (YouAbs(m2) - targetAbsPos(m2)).Length;
                if (d1 < d2) hi = m2; else lo = m1;
            }
            utCa = (lo + hi) / 2;
            sep = (YouAbs(utCa) - targetAbsPos(utCa)).Length;

            // relative speed = |d(rel)/dt| via central difference
            double h = Math.Max(1e-3, window * 1e-5);
            Vec2d rel0 = YouAbs(utCa - h) - targetAbsPos(utCa - h);
            Vec2d rel1 = YouAbs(utCa + h) - targetAbsPos(utCa + h);
            relSpeed = (rel1 - rel0).Length / (2 * h);
            return true;
        }

        // ---- geometric orbit-to-orbit proximity (intersections + closest points) ----

        /// <summary>A point on a conic at true anomaly nu, in absolute coordinates.</summary>
        private static Vec2d PointAt(in OrbitalElements el, Vec2d primaryAbs, double nu)
        {
            double r = el.SemiLatus / (1 + el.E * Math.Cos(nu));
            return primaryAbs + Vec2d.FromAngle(el.ArgPe + el.Dir * nu) * r;
        }

        /// <summary>True-anomaly half-range that keeps a conic's radius finite (pi for closed orbits,
        /// just shy of the asymptote for hyperbolas).</summary>
        private static double CurveLimit(in OrbitalElements el)
        {
            if (!el.Hyperbolic) return Math.PI;
            double nuInf = Math.Acos(Math.Max(-1, -1.0 / el.E));
            return Math.Min(nuInf * 0.99, Math.PI - 1e-3);
        }

        /// <summary>Nearest point on a conic curve to <paramref name="p"/> (coarse sweep + ternary refine).</summary>
        private static double NearestNu(Vec2d p, in OrbitalElements el, Vec2d primaryAbs, double lim, out double dist)
        {
            const int m = 360;
            double bestNu = 0, bestD = double.MaxValue;
            for (int j = 0; j <= m; j++)
            {
                double nu = -lim + 2 * lim * j / m;
                double r = el.SemiLatus / (1 + el.E * Math.Cos(nu));
                if (r <= 0) continue;
                double d = (PointAt(el, primaryAbs, nu) - p).Length;
                if (d < bestD) { bestD = d; bestNu = nu; }
            }
            double step = 2 * lim / m, lo = bestNu - step, hi = bestNu + step;
            for (int it = 0; it < 40; it++)
            {
                double a = lo + (hi - lo) / 3, b = hi - (hi - lo) / 3;
                if ((PointAt(el, primaryAbs, a) - p).Length < (PointAt(el, primaryAbs, b) - p).Length) hi = b; else lo = a;
            }
            double fnu = (lo + hi) / 2;
            dist = (PointAt(el, primaryAbs, fnu) - p).Length;
            return fnu;
        }

        /// <summary>Up to two geometric proximities between your conic and the target conic (both held
        /// static, primaries fixed at the call time): the smallest separations between the two curves,
        /// each flagged as an intersection when the gap is ~ 0 ("orbits meet").</summary>
        public static List<ProximityPoint> OrbitProximity(in OrbitalElements you, Vec2d youPrimaryAbs,
                                                          in OrbitalElements tgt, Vec2d tgtPrimaryAbs)
        {
            var res = new List<ProximityPoint>();
            if (double.IsNaN(you.A) || double.IsNaN(tgt.A)) return res;

            double youLim = CurveLimit(you), tgtLim = CurveLimit(tgt);
            var youEl = you; var tgtEl = tgt;   // copy for capture in the local function
            double SepAt(double nu)
            {
                NearestNu(PointAt(youEl, youPrimaryAbs, nu), tgtEl, tgtPrimaryAbs, tgtLim, out double d);
                return d;
            }

            const int N = 240;
            var sep = new double[N + 1];
            var nuYou = new double[N + 1];
            for (int i = 0; i <= N; i++)
            {
                double nu = -youLim + 2 * youLim * i / N;
                nuYou[i] = nu;
                double r = you.SemiLatus / (1 + you.E * Math.Cos(nu));
                sep[i] = r <= 0 ? double.MaxValue : SepAt(nu);
            }

            bool closed = !you.Hyperbolic;
            int count = closed ? N : N + 1;   // for closed curves index N duplicates index 0
            var cand = new List<int>();
            for (int i = 0; i < count; i++)
            {
                double s = sep[i];
                if (s == double.MaxValue) continue;
                double prev, next;
                if (closed) { prev = sep[(i - 1 + count) % count]; next = sep[(i + 1) % count]; }
                else { prev = i == 0 ? double.MaxValue : sep[i - 1]; next = i == count - 1 ? double.MaxValue : sep[i + 1]; }
                if (s <= prev && s <= next) cand.Add(i);
            }
            cand.Sort((a, b) => sep[a].CompareTo(sep[b]));

            double tol = Math.Max(1.0, 1e-3 * Math.Min(you.Periapsis, tgt.Periapsis));
            double half = 2 * youLim / N;
            foreach (int i in cand)
            {
                if (res.Count >= 2) break;
                double lo = nuYou[i] - half, hi = nuYou[i] + half;
                for (int it = 0; it < 40; it++)
                {
                    double a = lo + (hi - lo) / 3, b = hi - (hi - lo) / 3;
                    if (SepAt(a) < SepAt(b)) hi = b; else lo = a;
                }
                double ny = (lo + hi) / 2;
                // skip a near-duplicate of an already-accepted point
                bool dup = false;
                foreach (var r2 in res) if (Math.Abs(ny - r2.NuYou) < 0.2) { dup = true; break; }
                if (dup) continue;

                Vec2d yp = PointAt(you, youPrimaryAbs, ny);
                double tnu = NearestNu(yp, tgt, tgtPrimaryAbs, tgtLim, out double sepF);
                Vec2d tp = PointAt(tgt, tgtPrimaryAbs, tnu);
                res.Add(new ProximityPoint(ny, yp, tp, sepF, sepF < tol));
            }
            return res;
        }
    }
}
