using System;
using Solar.Core;

namespace Solar.Physics
{
    /// <summary>
    /// Plans a single prograde(/radial) burn that sends a coasting vessel onto an intercept with
    /// a target sharing the same primary (a Hohmann-style transfer between two orbits, plus the
    /// phasing that makes the ship and target arrive together). The result is a seed the player
    /// fine-tunes by watching the closest-approach markers converge -- not a guaranteed capture.
    ///
    /// Scope: transfers between orbits of <b>different</b> radius (planet/moon intercept). It does
    /// not solve near-co-orbital phasing (catching a vessel in the same orbit) or cross-SOI
    /// interplanetary ejection -- those return a best effort, not a real rendezvous.
    /// </summary>
    public static class TransferPlanner
    {
        /// <summary>Find a burn (time + prograde/radial delta-v, relative to <paramref name="ship"/>)
        /// that brings the post-burn coast close to <paramref name="targetOrbit"/> around the shared
        /// <paramref name="primary"/>. Returns false only for degenerate inputs.</summary>
        public static bool PlanIntercept(in OrbitalElements ship, CelestialBody primary, double utNow,
            in OrbitalElements targetOrbit, out double utBurn, out double prograde, out double radial)
        {
            utBurn = utNow; prograde = 0; radial = 0;
            if (primary == null || ship.Hyperbolic || double.IsNaN(ship.A) || double.IsNaN(targetOrbit.A))
                return false;

            double mu = ship.Mu;
            double r1 = ship.A, r2 = targetOrbit.A;          // circular approximation for sizing
            if (!(r1 > 0) || !(r2 > 0) || mu <= 0) return false;

            // 'in' params can't be captured by the local scoring function -- copy to locals.
            OrbitalElements shipEl = ship, tgtEl = targetOrbit;
            CelestialBody prim = primary;
            Vec2d TargetAbs(double t) => prim.AbsolutePositionAt(t) + Kepler.StateAtTime(tgtEl, t).pos;

            // Hohmann sizing: transfer half-ellipse time and the tangential prograde delta-v at r1.
            double aT = 0.5 * (r1 + r2);
            double tHalf = Math.PI * Math.Sqrt(aT * aT * aT / mu);
            double dvSeed = Math.Sqrt(mu / r1) * (Math.Sqrt(2 * r2 / (r1 + r2)) - 1);
            double window = 1.4 * tHalf;                      // CA search horizon (covers the arrival)

            // Closest approach of the post-burn coast to the target, scored over [utB, utB+window].
            double Score(double utB, double pro, double rad)
            {
                var (r, v) = Kepler.StateAtTime(shipEl, utB);
                if (v.Length < 1e-9) return double.MaxValue;
                Vec2d proDir = v.Normalized();
                Vec2d radOut = proDir.Perp();
                if (radOut.Dot(r) < 0) radOut = -radOut;      // outward-positive, matches Maneuver.BurnDelta
                Vec2d v2 = v + proDir * pro + radOut * rad;
                var result = Kepler.ElementsFromState(r, v2, mu, utB);
                if (double.IsNaN(result.A)) return double.MaxValue;
                return Rendezvous.ClosestApproach(result, prim, TargetAbs, utB, window,
                    out _, out double sep, out _) ? sep : double.MaxValue;
            }

            double shipPeriod = ship.Period;

            // Stage A -- coarse scan of the burn time across one synodic period (the cycle over which
            // the ship/target phase sweeps a full turn), holding prograde at the Hohmann seed.
            double n1 = Math.Sqrt(mu / (r1 * r1 * r1)), n2 = Math.Sqrt(mu / (r2 * r2 * r2));
            double dn = Math.Abs(n1 - n2);
            double scanSpan = dn > 1e-12 ? Math.Min(2 * Math.PI / dn, 6 * shipPeriod) : 4 * shipPeriod;
            double bestT = utNow, bestSep = double.MaxValue;
            const int CoarseN = 160;
            for (int i = 0; i <= CoarseN; i++)
            {
                double t = utNow + scanSpan * i / CoarseN;
                double s = Score(t, dvSeed, 0);
                if (s < bestSep) { bestSep = s; bestT = t; }
            }

            if (bestSep == double.MaxValue) return false;
            double resT = bestT, resP = dvSeed, resR = 0;

            // Stage B -- refine burn time (+-half a ship period) against prograde magnitude together.
            double bracket = Math.Max(0.5 * Math.Abs(dvSeed), 0.05 * Math.Sqrt(mu / r1));
            const int Tn = 20, Pn = 14;
            for (int it = 0; it <= Tn; it++)
            {
                double t = bestT + (it - Tn / 2.0) * (shipPeriod / Tn);
                if (t < utNow) continue;
                for (int ip = 0; ip <= Pn; ip++)
                {
                    double p = dvSeed - bracket + 2 * bracket * ip / Pn;
                    double s = Score(t, p, 0);
                    if (s < bestSep) { bestSep = s; resT = t; resP = p; }
                }
            }

            // Stage B2 -- a fine local pass around the Stage-B optimum to tighten the intercept.
            double tFine = shipPeriod / (Tn * 6.0), pFine = bracket / (Pn * 3.0);
            for (int it = -3; it <= 3; it++)
            {
                double t = resT + it * tFine;
                if (t < utNow) continue;
                for (int ip = -3; ip <= 3; ip++)
                {
                    double p = resP + ip * pFine;
                    double s = Score(t, p, 0);
                    if (s < bestSep) { bestSep = s; resT = t; resP = p; }
                }
            }

            // Stage C -- a small radial polish at the chosen (time, prograde).
            double radSpan = 0.3 * Math.Max(Math.Abs(resP), bracket);
            const int Rn = 12;
            for (int ir = 0; ir <= Rn; ir++)
            {
                double rad = -radSpan + 2 * radSpan * ir / Rn;
                double s = Score(resT, resP, rad);
                if (s < bestSep) { bestSep = s; resR = rad; }
            }

            utBurn = resT; prograde = resP; radial = resR;
            return true;
        }
    }
}
