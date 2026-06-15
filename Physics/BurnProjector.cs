using System;
using Solar.Core;

namespace Solar.Physics
{
    /// <summary>
    /// Forward-integrates a finite burn to predict the orbit the vessel will actually reach,
    /// instead of the instantaneous "if I cut throttle now" conic. Constant-thrust + mass-loss
    /// model with no auto-staging: the burn ends when the current active propellant runs out
    /// (<paramref name="maxBurnTime"/>) or the delta-v cap is hit, whichever comes first.
    ///
    /// Self-contained (does not touch the live vessel, so it can't reuse Integrator.Step which
    /// mutates state). Gravity + thrust only — no drag, no mid-burn SOI change: the burns of
    /// interest are short and in vacuum.
    /// </summary>
    public static class BurnProjector
    {
        /// <param name="thrustDir">SAS-aware steering: returns the unit thrust direction for the
        /// current projected (position, velocity). Re-evaluated once per step.</param>
        public static OrbitalElements Project(
            Vec2d r0, Vec2d v0, double mu, double utStart,
            double thrust, double massFlow, double mass0,
            double maxBurnTime, double dvCap,
            Func<Vec2d, Vec2d, Vec2d> thrustDir,
            out double utEnd)
        {
            utEnd = utStart;
            if (thrust <= 0 || mass0 <= 0 || (double.IsInfinity(maxBurnTime) && double.IsInfinity(dvCap)))
                return Kepler.ElementsFromState(r0, v0, mu, utStart);

            // Burn length: the earlier of fuel depletion and the delta-v cap. With constant thrust
            // and mass flow, the dv cap maps to a time analytically (Tsiolkovsky), so the integral
            // direction never affects when we stop.
            double burnLen = maxBurnTime;
            if (!double.IsInfinity(dvCap))
            {
                double tByDv;
                if (massFlow > 1e-9)
                {
                    double ve = thrust / massFlow;                 // effective exhaust velocity
                    double mEnd = mass0 * Math.Exp(-dvCap / ve);
                    tByDv = (mass0 - mEnd) / massFlow;
                }
                else tByDv = dvCap * mass0 / thrust;               // no mass loss: constant accel
                burnLen = Math.Min(burnLen, tByDv);
            }
            if (!(burnLen > 0)) return Kepler.ElementsFromState(r0, v0, mu, utStart);

            double dt = Math.Clamp(burnLen / 200.0, 0.05, 1.0);
            Vec2d r = r0, v = v0;
            double t = 0;
            int guard = 0;
            while (t < burnLen && guard++ < 100000)
            {
                double h = Math.Min(dt, burnLen - t);
                double mass = Math.Max(1.0, mass0 - massFlow * t);
                Vec2d thrustAcc = thrustDir(r, v) * (thrust / mass);   // held across this step

                Vec2d Accel(Vec2d rr, Vec2d vv)
                {
                    double rl = rr.Length;
                    return rr * (-mu / (rl * rl * rl)) + thrustAcc;
                }

                Vec2d k1r = v, k1v = Accel(r, v);
                Vec2d k2r = v + k1v * (h / 2), k2v = Accel(r + k1r * (h / 2), v + k1v * (h / 2));
                Vec2d k3r = v + k2v * (h / 2), k3v = Accel(r + k2r * (h / 2), v + k2v * (h / 2));
                Vec2d k4r = v + k3v * h, k4v = Accel(r + k3r * h, v + k3v * h);
                r += (k1r + 2 * k2r + 2 * k3r + k4r) * (h / 6);
                v += (k1v + 2 * k2v + 2 * k3v + k4v) * (h / 6);
                t += h;
            }

            utEnd = utStart + burnLen;
            return Kepler.ElementsFromState(r, v, mu, utEnd);
        }
    }
}
