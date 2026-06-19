using System;
using Solar.Core;

namespace Solar.Physics
{
    public enum TransitionType { None, Escape, Encounter, AtmoEntry }

    /// <summary>One predicted trajectory: the current conic plus (at most) one SOI transition.</summary>
    public sealed class Prediction
    {
        public OrbitalElements Orbit;
        public CelestialBody Body;
        public double StartUT;
        public TransitionType Type = TransitionType.None;
        public double TransitionUT;
        public CelestialBody NextBody;
        public OrbitalElements NextOrbit;
    }

    /// <summary>
    /// Predicts SOI escapes/encounters and atmosphere entry along an on-rails conic.
    /// Escape and atmosphere entry are solved analytically (radius crossings); encounters
    /// are found by time-sampling against each child body's on-rails position, then
    /// refined by bisection. One level of prediction depth, KSP map-view style.
    /// </summary>
    public static class TrajectoryPredictor
    {
        private const int Samples = 800;
        private const double MaxHorizon = 3.0e8; // ~9.5 game-years cap for weird orbits

        public static Prediction Predict(in OrbitalElements el, CelestialBody primary, double utNow)
        {
            var pred = new Prediction { Orbit = el, Body = primary, StartUT = utNow };
            if (double.IsNaN(el.A) || double.IsNaN(el.E)) return pred;

            // analytic events
            double? escapeT = double.IsInfinity(primary.SoiRadius)
                ? null
                : Kepler.NextRadiusCrossingOutbound(el, primary.SoiRadius, utNow + 1e-6);
            double rEntry = primary.Radius + (primary.Atmo?.Top ?? 2000);
            double? atmoT = Kepler.NextRadiusCrossingInbound(el, rEntry, utNow + 1e-6);

            double horizon = utNow + (el.Hyperbolic ? MaxHorizon : Math.Min(el.Period, MaxHorizon));
            if (escapeT.HasValue) horizon = Math.Min(horizon, escapeT.Value);
            if (atmoT.HasValue) horizon = Math.Min(horizon, atmoT.Value);

            // sampled encounters with children of the primary
            double encounterT = double.PositiveInfinity;
            CelestialBody encounterBody = null;
            double vMin = el.Periapsis;                                  // >0 even for hyperbolic (A<0, E>1)
            double vMax = el.Hyperbolic ? double.PositiveInfinity : el.Apoapsis;
            foreach (var child in primary.Children)
            {
                if (double.IsInfinity(child.SoiRadius)) continue;
                // Cheap radial-band rejection: an SOI encounter needs the two distance-from-primary bands
                // to overlap (triangle inequality), so a disjoint band rules it out without sampling.
                double cMin = child.Orbit.Periapsis - child.SoiRadius;
                double cMax = child.Orbit.Apoapsis + child.SoiRadius;
                if (vMax < cMin || vMin > cMax) continue;
                double? t = FindEncounter(el, child, utNow, horizon);
                if (t.HasValue && t.Value < encounterT)
                {
                    encounterT = t.Value;
                    encounterBody = child;
                }
            }

            // earliest event wins
            double best = double.PositiveInfinity;
            if (atmoT.HasValue) { best = atmoT.Value; pred.Type = TransitionType.AtmoEntry; pred.TransitionUT = best; }
            if (escapeT.HasValue && escapeT.Value < best) { best = escapeT.Value; pred.Type = TransitionType.Escape; pred.TransitionUT = best; }
            if (encounterBody != null && encounterT < best) { best = encounterT; pred.Type = TransitionType.Encounter; pred.TransitionUT = best; pred.NextBody = encounterBody; }

            if (pred.Type == TransitionType.Encounter)
            {
                var (pos, vel) = Kepler.StateAtTime(el, pred.TransitionUT);
                Vec2d rel = pos - Kepler.StateAtTime(encounterBody.Orbit, pred.TransitionUT).pos;
                Vec2d relV = vel - Kepler.StateAtTime(encounterBody.Orbit, pred.TransitionUT).vel;
                pred.NextOrbit = Kepler.ElementsFromState(rel, relV, encounterBody.Mu, pred.TransitionUT);
            }
            else if (pred.Type == TransitionType.Escape && primary.Parent != null)
            {
                pred.NextBody = primary.Parent;
                var (pos, vel) = Kepler.StateAtTime(el, pred.TransitionUT);
                Vec2d abs = pos + Kepler.StateAtTime(primary.Orbit, pred.TransitionUT).pos;
                Vec2d absV = vel + Kepler.StateAtTime(primary.Orbit, pred.TransitionUT).vel;
                pred.NextOrbit = Kepler.ElementsFromState(abs, absV, primary.Parent.Mu, pred.TransitionUT);
            }

            return pred;
        }

        private static double? FindEncounter(in OrbitalElements el, CelestialBody child, double ut0, double ut1)
        {
            if (ut1 <= ut0) return null;
            OrbitalElements orbit = el; // local copy: 'in' params can't be captured by local functions
            double Dist(double t)
            {
                Vec2d v = Kepler.StateAtTime(orbit, t).pos - Kepler.StateAtTime(child.Orbit, t).pos;
                return v.Length - child.SoiRadius;
            }

            double step = (ut1 - ut0) / Samples;
            double prevT = ut0 + 1e-6;
            double prevD = Dist(prevT);
            if (prevD < 0) return null; // already inside (boundary jitter) - ignore

            for (int i = 1; i <= Samples; i++)
            {
                double t = ut0 + (ut1 - ut0) * i / Samples;
                double d = Dist(t);
                if (d < 0)
                {
                    // bisect the entry time
                    double lo = prevT, hi = t;
                    for (int k = 0; k < 60; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (Dist(mid) < 0) hi = mid; else lo = mid;
                    }
                    return 0.5 * (lo + hi);
                }
                prevT = t; prevD = d;
            }
            return null;
        }
    }
}
