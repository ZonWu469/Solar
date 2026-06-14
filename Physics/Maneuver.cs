using System;
using Solar.Core;

namespace Solar.Physics
{
    /// <summary>
    /// A single planned burn: a delta-v applied to the vessel's coast orbit at a chosen
    /// time. In 2D the burn has only prograde (along velocity) and radial (perpendicular,
    /// outward-positive) components. The resulting orbit is obtained by applying the delta-v
    /// to the state vector and re-deriving elements, reusing the existing Kepler machinery.
    /// </summary>
    public sealed class Maneuver
    {
        public double UT;        // burn time (s)
        public double Prograde;  // m/s along velocity (+forward / -retrograde)
        public double Radial;    // m/s perpendicular to velocity (+radial-out / -radial-in)

        // Snapshot of the pre-burn coast orbit the node is planned against. Refreshed while
        // the vessel coasts, but held frozen during a burn so the planned orbit stays put.
        public OrbitalElements Source;
        public bool HasSource;

        // The primary body Source/result are relative to. Set by the projection walk every
        // frame (so a node can live in a different SOI than the live vessel); runtime-only,
        // not serialized. Consumers fall back to the live body when this is null.
        public CelestialBody Body;

        // Once the burn time passes, the node's absolute world position is frozen here so it
        // stays visible (and correctly placed) regardless of later orbit or SOI changes.
        public bool Reached;
        public Vec2d ReachedAbsPos;

        public double DeltaV => Math.Sqrt(Prograde * Prograde + Radial * Radial);

        /// <summary>State (relative to the primary) just after the burn, given the source coast orbit.</summary>
        public (Vec2d r, Vec2d v) StateAfter(in OrbitalElements src)
        {
            var (r, v) = Kepler.StateAtTime(src, UT);
            return (r, v + BurnDelta(r, v));
        }

        /// <summary>The world-frame delta-v vector for a given pre-burn state (r outward, v velocity).</summary>
        public Vec2d BurnDelta(Vec2d r, Vec2d v)
        {
            Vec2d pro = v.Normalized();
            // Radial-out: the perpendicular to prograde that points away from the primary.
            Vec2d radOut = pro.Perp();
            if (radOut.Dot(r) < 0) radOut = -radOut;
            return pro * Prograde + radOut * Radial;
        }

        /// <summary>Resulting orbital elements after the burn.</summary>
        public OrbitalElements ResultOrbit(in OrbitalElements src, double mu)
        {
            var (r, v) = StateAfter(src);
            return Kepler.ElementsFromState(r, v, mu, UT);
        }
    }
}
