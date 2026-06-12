using System;

namespace Solar.Physics
{
    /// <summary>Simple exponential-density atmosphere.</summary>
    public sealed class Atmosphere
    {
        public double SeaLevelDensity;  // kg/m^3
        public double ScaleHeight;      // m
        public double Top;              // m above surface; density is zero above this

        public Atmosphere(double rho0, double scaleHeight, double top)
        {
            SeaLevelDensity = rho0; ScaleHeight = scaleHeight; Top = top;
        }

        public double DensityAt(double altitude)
        {
            if (altitude >= Top) return 0;
            if (altitude < 0) altitude = 0;
            return SeaLevelDensity * Math.Exp(-altitude / ScaleHeight);
        }
    }
}
