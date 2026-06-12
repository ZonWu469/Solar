using System;
using Solar.Core;

namespace Solar.Physics
{
    /// <summary>Kepler equation solvers and state-vector / orbital-element conversions (2D).</summary>
    public static class Kepler
    {
        public const double Tol = 1e-13;

        public static double WrapPi(double a) => Math.IEEERemainder(a, 2 * Math.PI);

        /// <summary>Solve M = E - e*sinE for E (elliptic). Newton with safe start.</summary>
        public static double SolveEccentric(double M, double e)
        {
            M = WrapPi(M);
            double E = e < 0.8 ? M : Math.PI * (M >= 0 ? 1 : -1);
            for (int i = 0; i < 60; i++)
            {
                double f = E - e * Math.Sin(E) - M;
                double dE = -f / (1 - e * Math.Cos(E));
                E += dE;
                if (Math.Abs(dE) < Tol) break;
            }
            return E;
        }

        /// <summary>Solve M = e*sinhH - H for H (hyperbolic). Newton with log start, bisection fallback.</summary>
        public static double SolveHyperbolic(double M, double e)
        {
            double H = Math.Sign(M == 0 ? 1 : M) * Math.Log(2 * Math.Abs(M) / e + 1.8);
            for (int i = 0; i < 60; i++)
            {
                double f = e * Math.Sinh(H) - H - M;
                double d = e * Math.Cosh(H) - 1;
                double dH = -f / d;
                if (double.IsNaN(dH) || double.IsInfinity(dH)) break;
                if (Math.Abs(dH) > 50) dH = Math.Sign(dH) * 50; // dampen wild steps
                H += dH;
                if (Math.Abs(dH) < Tol) return H;
            }
            return BisectHyperbolic(M, e);
        }

        private static double BisectHyperbolic(double M, double e)
        {
            double lo = -1, hi = 1;
            while (e * Math.Sinh(lo) - lo > M && lo > -700) lo *= 2;
            while (e * Math.Sinh(hi) - hi < M && hi < 700) hi *= 2;
            for (int i = 0; i < 200; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (e * Math.Sinh(mid) - mid < M) lo = mid; else hi = mid;
            }
            return 0.5 * (lo + hi);
        }

        /// <summary>Position and velocity relative to the primary at universal time ut.</summary>
        public static (Vec2d pos, Vec2d vel) StateAtTime(in OrbitalElements el, double ut)
        {
            double M = el.M0 + el.MeanMotion * (ut - el.Epoch);
            double nu, r;
            if (!el.Hyperbolic)
            {
                double E = SolveEccentric(M, el.E);
                nu = 2 * Math.Atan2(Math.Sqrt(1 + el.E) * Math.Sin(E / 2), Math.Sqrt(1 - el.E) * Math.Cos(E / 2));
                r = el.A * (1 - el.E * Math.Cos(E));
            }
            else
            {
                double H = SolveHyperbolic(M, el.E);
                nu = 2 * Math.Atan(Math.Sqrt((el.E + 1) / (el.E - 1)) * Math.Tanh(H / 2));
                r = el.A * (1 - el.E * Math.Cosh(H));
            }
            return StateAtTrueAnomaly(el, nu, r);
        }

        /// <summary>Position and velocity at a given true anomaly. r may be passed to skip recomputation.</summary>
        public static (Vec2d pos, Vec2d vel) StateAtTrueAnomaly(in OrbitalElements el, double nu, double r = -1)
        {
            double p = el.SemiLatus;
            double c = Math.Cos(nu), s = Math.Sin(nu);
            if (r < 0) r = p / (1 + el.E * c);
            Vec2d ph = Vec2d.FromAngle(el.ArgPe);   // unit toward periapsis
            Vec2d qh = ph.Perp() * el.Dir;          // unit along motion at periapsis
            Vec2d pos = ph * (r * c) + qh * (r * s);
            double k = Math.Sqrt(el.Mu / p);
            Vec2d vel = ph * (-k * s) + qh * (k * (el.E + c));
            return (pos, vel);
        }

        /// <summary>Orbital elements from a primary-relative state vector.</summary>
        public static OrbitalElements ElementsFromState(Vec2d r, Vec2d v, double mu, double ut)
        {
            double rl = r.Length;
            double h = r.Cross(v);
            int dir = h >= 0 ? 1 : -1;
            double hMin = 1e-6 * Math.Sqrt(mu * rl);
            if (Math.Abs(h) < hMin) h = dir * hMin; // avoid radial-trajectory singularity

            double p = h * h / mu;
            double v2 = v.LengthSquared;
            Vec2d evec = (r * (v2 - mu / rl) - v * r.Dot(v)) / mu;
            double e = evec.Length;
            if (e > 1 - 1e-6 && e < 1) e = 1 - 1e-6;       // clamp away from parabolic
            else if (e >= 1 && e < 1 + 1e-6) e = 1 + 1e-6;

            double argPe = e > 1e-9 ? evec.Angle() : 0.0;
            double a = p / (1 - e * e);
            double nu = WrapPi(dir * (r.Angle() - argPe));
            double m0 = MeanFromTrue(e, nu);

            return new OrbitalElements { A = a, E = e, ArgPe = argPe, M0 = m0, Epoch = ut, Mu = mu, Dir = dir };
        }

        /// <summary>Mean anomaly corresponding to true anomaly nu (same revolution, [-pi, pi] sense).</summary>
        public static double MeanFromTrue(double e, double nu)
        {
            if (e < 1)
            {
                double E = 2 * Math.Atan2(Math.Sqrt(1 - e) * Math.Sin(nu / 2), Math.Sqrt(1 + e) * Math.Cos(nu / 2));
                return E - e * Math.Sin(E);
            }
            else
            {
                double x = Math.Sqrt((e - 1) / (e + 1)) * Math.Tan(nu / 2);
                x = Math.Clamp(x, -1 + 1e-12, 1 - 1e-12);
                double H = Math.Log((1 + x) / (1 - x));
                return e * Math.Sinh(H) - H;
            }
        }

        /// <summary>Earliest UT >= utFrom at which the orbit reaches true anomaly nu.
        /// For hyperbolic orbits the (single) passage time is returned even if it is before utFrom.</summary>
        public static double TimeAtTrueAnomaly(in OrbitalElements el, double nu, double utFrom)
        {
            double M = MeanFromTrue(el.E, nu);
            double n = el.MeanMotion;
            double t = el.Epoch + (M - el.M0) / n;
            if (el.Hyperbolic) return t;
            double T = el.Period;
            double k = Math.Ceiling((utFrom - t) / T);
            return t + Math.Max(0, k) * T;
        }

        /// <summary>Next UT >= utFrom at which radius crosses rTarget while descending, or null.</summary>
        public static double? NextRadiusCrossingInbound(in OrbitalElements el, double rTarget, double utFrom)
        {
            if (el.Periapsis > rTarget) return null;
            if (!el.Hyperbolic && el.Apoapsis < rTarget) return null;
            double cnu = (el.SemiLatus / rTarget - 1) / el.E;
            if (cnu < -1 || cnu > 1) return null;
            double t = TimeAtTrueAnomaly(el, -Math.Acos(cnu), utFrom);
            return t >= utFrom ? t : (double?)null;
        }

        /// <summary>Next UT >= utFrom at which radius crosses rTarget while ascending, or null.</summary>
        public static double? NextRadiusCrossingOutbound(in OrbitalElements el, double rTarget, double utFrom)
        {
            if (!el.Hyperbolic && el.Apoapsis < rTarget) return null;
            if (el.Periapsis > rTarget) return null;
            double cnu = (el.SemiLatus / rTarget - 1) / el.E;
            if (cnu < -1 || cnu > 1) return null;
            double t = TimeAtTrueAnomaly(el, Math.Acos(cnu), utFrom);
            return t >= utFrom ? t : (double?)null;
        }
    }
}
