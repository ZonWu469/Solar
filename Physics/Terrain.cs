using System;
using System.Collections.Generic;

namespace Solar.Physics
{
    /// <summary>Deterministic 1-D surface relief as a function of world-frame angle. Bodies don't rotate,
    /// so a body-local angle (e.g. <c>Position.Angle()</c>) is a fixed longitude — one height field feeds
    /// physics, the flight horizon and the map view identically. Built from a few sine octaves (fbm) so it
    /// is smooth, periodic in theta, and cheap to sample analytically.</summary>
    public sealed class Terrain
    {
        /// <summary>Slope (dimensionless rise/run) at or below which a touchdown is considered landable;
        /// above it a craft topples/crashes regardless of speed. Also the green/amber split for the overlay.</summary>
        public const double LandableSlope = 0.18;

        private readonly int[] _wave;       // integer harmonics -> exactly 2*pi periodic
        private readonly double[] _amp;     // per-octave weight, normalized so |raw height| <= 1
        private readonly double[] _phase;
        private readonly (double center, double sigma)[] _plains;
        private readonly double _radius;

        /// <summary>Maximum |elevation| above the base radius (m).</summary>
        public double Amplitude { get; }
        public double MaxAmplitude => Amplitude;

        /// <summary>World-frame angles (rad) of the guaranteed flat plains, for landing-site markers.</summary>
        public IReadOnlyList<double> PlainCenters { get; }

        public Terrain(double radius, double amplitude, int seed,
                       int octaves = 6, double lacunarity = 2.0, double persistence = 0.6, int plains = 3)
        {
            _radius = radius;
            Amplitude = amplitude;
            var rnd = new Random(seed);

            octaves = Math.Max(1, octaves);
            _wave = new int[octaves];
            _amp = new double[octaves];
            _phase = new double[octaves];
            double baseFreq = 2 + rnd.NextDouble() * 2;   // 2..4: broad first-order undulation
            double sum = 0;
            int prev = 0;
            for (int k = 0; k < octaves; k++)
            {
                int w = (int)Math.Round(baseFreq * Math.Pow(lacunarity, k));
                if (w <= prev) w = prev + 1;              // strictly increasing integer harmonics
                prev = w;
                _wave[k] = w;
                _amp[k] = Math.Pow(persistence, k);
                _phase[k] = rnd.NextDouble() * Math.PI * 2;
                sum += _amp[k];
            }
            for (int k = 0; k < octaves; k++) _amp[k] /= sum;   // now |raw height| <= 1

            int np = Math.Max(0, plains);
            _plains = new (double, double)[np];
            var centers = new double[np];
            for (int j = 0; j < np; j++)
            {
                double c = rnd.NextDouble() * Math.PI * 2;
                double sigma = 0.10 + rnd.NextDouble() * 0.05;   // wide, smooth flat wells (~6-9 deg)
                _plains[j] = (c, sigma);
                centers[j] = c;
            }
            PlainCenters = centers;
        }

        /// <summary>Signed elevation above the base radius (m): + hills/mountains, - depressions.</summary>
        public double HeightAt(double angle)
        {
            double raw = 0;
            for (int k = 0; k < _wave.Length; k++)
                raw += _amp[k] * Math.Sin(_wave[k] * angle + _phase[k]);
            return Amplitude * raw * FlatMask(angle);
        }

        /// <summary>Elevation normalized to [-1, 1] for elevation-band coloring.</summary>
        public double NormalizedHeight(double angle) => Amplitude > 0 ? HeightAt(angle) / Amplitude : 0;

        /// <summary>Local slope magnitude (rise/run): |d(height) / d(arc length)|, with arc length = R*dtheta.</summary>
        public double SlopeAt(double angle)
        {
            const double e = 1e-3;
            double dh = HeightAt(angle + e) - HeightAt(angle - e);
            double ds = 2 * e * _radius;
            return ds > 0 ? Math.Abs(dh / ds) : 0;
        }

        // 1 over open terrain, dipping smoothly to ~0 inside each plain well, so the plains are flat
        // (slope -> 0) and sit at the base radius: a safe touchdown always exists.
        private double FlatMask(double angle)
        {
            double m = 1;
            foreach (var (c, sigma) in _plains)
            {
                double d = AngleDelta(angle, c);
                m *= 1 - Math.Exp(-(d * d) / (sigma * sigma));
            }
            return m;
        }

        private static double AngleDelta(double a, double b)
        {
            double d = (a - b) % (Math.PI * 2);
            if (d > Math.PI) d -= Math.PI * 2;
            else if (d < -Math.PI) d += Math.PI * 2;
            return d;
        }
    }
}
