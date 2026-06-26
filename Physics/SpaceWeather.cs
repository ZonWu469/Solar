using System;
using Solar.Core;

namespace Solar.Physics
{
    public enum StormPhase { None, Incoming, Active }

    /// <summary>What the space-weather model reports for one vessel at one instant.</summary>
    public struct StormState
    {
        public StormPhase Phase;
        public double DoseRate;     // radiation dose rate (units/s) at the vessel, distance-scaled, BEFORE shielding
        public double Intensity;    // storm envelope 0..1 at the current instant (0 while Incoming)
        public double ArrivalUt;    // UT the front reaches the vessel (already past while Active)
        public double EndUt;        // UT the storm ends at the vessel
        public Vec2d SunDir;        // unit vector from the vessel toward the Sun (the direction to shield against)
        public double SunDistance;  // metres to the Sun

        public bool IsActive => Phase == StormPhase.Active;
        /// <summary>Seconds until the front arrives (Incoming) — for the HUD countdown.</summary>
        public double TimeToArrival(double ut) => Math.Max(0, ArrivalUt - ut);
    }

    /// <summary>Deterministic, analytic solar-storm timeline. Like everything else on-rails, a storm is a
    /// pure function of UT (plus a per-save seed), so it works under unlimited time warp and for offline /
    /// on-rails vessels and colonies with no per-frame RNG and nothing to persist beyond the seed.
    ///
    /// The Sun emits storms on a seeded quasi-periodic cadence. A storm detected (telemetry, instant) at
    /// <c>emitUt</c> launches a particle front that travels outward at <see cref="Solar.Core.Balance.StormFrontSpeed"/>,
    /// so it reaches a vessel <c>sunDistance / frontSpeed</c> later — the warning window grows with distance
    /// from the Sun. On arrival the dose ramps up and decays over the storm's duration, and its strength
    /// falls off inverse-square with distance (mirroring <see cref="Vessel.Vessel"/>'s solar-panel scaling),
    /// so the inner system is hammered while the outer system sees weak storms with long lead time.</summary>
    public static class SpaceWeather
    {
        /// <summary>The active savegame's storm seed, mirrored here (like <see cref="Solar.Core.Balance"/>'s
        /// static tunables) so hazard code reached deep in the simulation — <see cref="Vessel.Threats"/>,
        /// offline colony catch-up — can evaluate the timeline without threading the seed through every call.
        /// Scenes set it from <c>GameState.WeatherSeed</c> when a save becomes active.</summary>
        public static long ActiveSeed;

        // search a few storm indices either side of the estimate (jitter can shuffle neighbours slightly)
        private const int SearchSpan = 3;

        /// <summary>Storm state for a vessel at <paramref name="absPos"/> (absolute) and time <paramref name="ut"/>.
        /// Convenience overload that reads tunables from <see cref="Balance"/> and the Sun/Earth from the universe.</summary>
        public static StormState ForVessel(long seed, double ut, Vec2d absPos, Universe u)
        {
            Vec2d sunPos = u?.NearestStar(absPos, ut)?.AbsolutePositionAt(ut) ?? Vec2d.Zero;
            double earthDist = u?["Earth"]?.Orbit.A ?? 1.0;
            return ForVessel(seed, ut, absPos, sunPos, earthDist,
                             Balance.StormFrontSpeed, Balance.StormIntervalS, Balance.StormDurationS, Balance.StormPeakDose);
        }

        /// <summary>Pure core (no Universe / Balance) so the self-tests can drive it deterministically.</summary>
        public static StormState ForVessel(long seed, double ut, Vec2d absPos, Vec2d sunPos, double earthDist,
                                           double frontSpeed, double intervalS, double durationS, double peakDose)
        {
            Vec2d toSun = sunPos - absPos;
            double sunDist = toSun.Length;
            var st = new StormState
            {
                Phase = StormPhase.None,
                SunDir = sunDist > 1e-6 ? toSun / sunDist : new Vec2d(1, 0),
                SunDistance = sunDist,
            };
            if (intervalS <= 0 || frontSpeed <= 0 || durationS <= 0 || peakDose <= 0) return st;

            double travel = sunDist / frontSpeed;                       // front transit time to this vessel
            double falloff = DistanceFalloff(earthDist, sunDist);       // inverse-square strength multiplier

            // The front arriving now was emitted ~travel ago; scan storms around that index.
            long iEst = (long)Math.Floor((ut - travel) / intervalS);
            double bestIncomingArrival = double.PositiveInfinity;
            bool haveIncoming = false;

            for (long i = iEst - SearchSpan; i <= iEst + SearchSpan; i++)
            {
                if (i < 0) continue;
                double emit = EmitUt(seed, i, intervalS);
                double arrival = emit + travel;
                double dur = durationS * (0.5 + U01(seed, i * 4 + 1));   // 0.5x .. 1.5x
                double end = arrival + dur;
                double peak = peakDose * (0.4 + 1.4 * U01(seed, i * 4 + 2)); // some storms much stronger than others

                if (ut >= arrival && ut <= end)
                {
                    double env = Envelope((ut - arrival) / dur);
                    st.Phase = StormPhase.Active;
                    st.Intensity = env;
                    st.DoseRate = peak * env * falloff;
                    st.ArrivalUt = arrival;
                    st.EndUt = end;
                    return st;                                          // an active storm dominates
                }
                if (emit <= ut && arrival > ut && arrival < bestIncomingArrival)
                {
                    bestIncomingArrival = arrival;                      // detected but not yet arrived
                    st.EndUt = end;
                    haveIncoming = true;
                }
            }

            if (haveIncoming)
            {
                st.Phase = StormPhase.Incoming;
                st.ArrivalUt = bestIncomingArrival;
            }
            return st;
        }

        /// <summary>UT the next storm front reaches this vessel, at or after <paramref name="ut"/> (using the
        /// vessel's current distance to the Sun). Used to clamp time warp so a single huge step can never skip
        /// over a storm's onset. Returns +inf if none is found in the near horizon.</summary>
        public static double NextArrivalUt(long seed, double ut, Vec2d absPos, Universe u)
        {
            Vec2d sunPos = u?.NearestStar(absPos, ut)?.AbsolutePositionAt(ut) ?? Vec2d.Zero;
            double travel = (sunPos - absPos).Length / Math.Max(1.0, Balance.StormFrontSpeed);
            double interval = Balance.StormIntervalS;
            if (interval <= 0) return double.PositiveInfinity;
            long iEst = (long)Math.Floor((ut - travel) / interval);
            double best = double.PositiveInfinity;
            for (long i = Math.Max(0, iEst - SearchSpan); i <= iEst + 16; i++)
            {
                double arrival = EmitUt(seed, i, interval) + travel;
                if (arrival > ut && arrival < best) best = arrival;
            }
            return best;
        }

        /// <summary>Emission time of storm <paramref name="i"/>: evenly spaced by <paramref name="intervalS"/>
        /// with a deterministic jitter so the cadence is irregular but reproducible.</summary>
        private static double EmitUt(long seed, long i, double intervalS)
            => i * intervalS + (U01(seed, i * 4) - 0.5) * intervalS * 0.6;

        /// <summary>Dose envelope across a storm: a smooth rise to a mid-storm peak and decay (0 at the edges).</summary>
        private static double Envelope(double t) => t <= 0 || t >= 1 ? 0 : Math.Sin(Math.PI * t);

        /// <summary>Inverse-square strength vs Earth's distance, clamped so the inner system is hammered and
        /// the outer system sees weak storms (never zero, never unbounded).</summary>
        public static double DistanceFalloff(double earthDist, double sunDist)
        {
            if (sunDist <= 1 || earthDist <= 1) return 1;
            double r = earthDist / sunDist;
            return Math.Clamp(r * r, 0.001, 16.0);
        }

        /// <summary>SplitMix64-style hash of (seed, i) mapped to [0,1). Deterministic and well-distributed.</summary>
        private static double U01(long seed, long i)
        {
            ulong x = unchecked((ulong)seed + (ulong)i * 0x9E3779B97F4A7C15UL);
            x ^= x >> 30; x = unchecked(x * 0xBF58476D1CE4E5B9UL);
            x ^= x >> 27; x = unchecked(x * 0x94D049BB133111EBUL);
            x ^= x >> 31;
            return (x >> 11) * (1.0 / 9007199254740992.0);   // top 53 bits -> [0,1)
        }
    }
}
