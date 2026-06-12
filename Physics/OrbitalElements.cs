using System;
using System.Text.Json.Serialization;

namespace Solar.Physics
{
    /// <summary>
    /// 2D Keplerian orbital elements. Anomalies (nu, E/H, M) are measured in the
    /// direction of motion so they always increase with time; Dir maps orbital
    /// angles into world space (+1 = counter-clockwise, -1 = clockwise):
    /// world angle of a point at true anomaly nu is ArgPe + Dir * nu.
    /// </summary>
    public struct OrbitalElements
    {
        public double A;      // semi-major axis (m); negative for hyperbolic
        public double E;      // eccentricity, clamped away from 1
        public double ArgPe;  // world angle of periapsis direction (rad)
        public double M0;     // mean anomaly at Epoch
        public double Epoch;  // UT (s)
        public double Mu;     // gravitational parameter of the primary (m^3/s^2)
        public int Dir;       // +1 CCW, -1 CW

        [JsonIgnore] public bool Hyperbolic => E > 1.0;
        [JsonIgnore] public double SemiLatus => A * (1 - E * E);
        [JsonIgnore] public double MeanMotion => Math.Sqrt(Mu / Math.Abs(A * A * A));
        [JsonIgnore] public double Period => 2 * Math.PI / MeanMotion;  // meaningful for elliptic only
        [JsonIgnore] public double Periapsis => A * (1 - E);
        [JsonIgnore] public double Apoapsis => A * (1 + E);             // negative/meaningless for hyperbolic
    }
}
