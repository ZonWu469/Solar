using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Solar.Core;

namespace Solar.Physics
{
    /// <summary>An on-rails celestial body (star, planet or moon).</summary>
    public sealed class CelestialBody
    {
        public string Name;
        public string TextureId;   // stable slug used to locate an optional body texture asset
        public double Mu;          // m^3/s^2
        public double Radius;      // m
        public CelestialBody Parent;
        public OrbitalElements Orbit;  // valid when Parent != null
        public double SoiRadius = double.PositiveInfinity;
        public double OreRichness;  // 0..1 surface ore concentration; scales drill yield (0 = barren, e.g. a star/gas giant)
        public Atmosphere Atmo;
        public Terrain Terrain;     // null = perfectly smooth (the star); otherwise organic surface relief
        public Color BodyColor;
        public Color AtmoColor;
        public double RadBeltInner, RadBeltOuter;  // radiation belt altitude band above the surface (m); 0..0 = none
        public double RadBeltDose;                  // dose rate (units/s) crew accumulate inside the belt
        public bool IsAsteroid;                     // a discoverable minor body (see AsteroidField); carries an authored SoiRadius
        public readonly List<CelestialBody> Children = new();
        public readonly List<LivableNiche> Niches = new();  // natural sheltered spots that halve life-support draw

        /// <summary>Surface radius (m) at a body-local angle, including terrain relief.</summary>
        public double SurfaceRadiusAt(double angle) => Terrain == null ? Radius : Radius + Terrain.HeightAt(angle);

        /// <summary>The livable niche whose footprint covers the given body-local angle, or null if none.
        /// Bodies don't rotate, so a landed vessel's <c>Position.Angle()</c> is a fixed longitude.</summary>
        public LivableNiche NicheAt(double angle)
        {
            foreach (var n in Niches)
            {
                double d = (angle - n.CenterAngle) % (Math.PI * 2);
                if (d > Math.PI) d -= Math.PI * 2;
                else if (d < -Math.PI) d += Math.PI * 2;
                if (Math.Abs(d) <= n.HalfWidth) return n;
            }
            return null;
        }

        /// <summary>Radiation dose rate (units/s) at the given altitude above this body's surface: the belt
        /// dose inside its altitude band, zero outside it (and zero for bodies with no belt).</summary>
        public double RadiationAt(double altitude)
            => RadBeltDose > 0 && altitude >= RadBeltInner && altitude <= RadBeltOuter ? RadBeltDose : 0;

        /// <summary>Highest the surface ever reaches (m), for culling and view-extent math.</summary>
        public double MaxRadius => Radius + (Terrain?.MaxAmplitude ?? 0);

        // per-frame memo: LocalPositionAt is called many times with the same ut
        private double _posUt = double.NaN, _velUt = double.NaN;
        private Vec2d _posCache, _velCache;

        public Vec2d LocalPositionAt(double ut)
        {
            if (Parent == null) return Vec2d.Zero;
            if (ut != _posUt) { _posCache = Kepler.StateAtTime(Orbit, ut).pos; _posUt = ut; }
            return _posCache;
        }

        public Vec2d LocalVelocityAt(double ut)
        {
            if (Parent == null) return Vec2d.Zero;
            if (ut != _velUt) { _velCache = Kepler.StateAtTime(Orbit, ut).vel; _velUt = ut; }
            return _velCache;
        }

        public Vec2d AbsolutePositionAt(double ut)
        {
            Vec2d p = Vec2d.Zero;
            for (var b = this; b != null; b = b.Parent) p += b.LocalPositionAt(ut);
            return p;
        }

        public Vec2d AbsoluteVelocityAt(double ut)
        {
            Vec2d v = Vec2d.Zero;
            for (var b = this; b != null; b = b.Parent) v += b.LocalVelocityAt(ut);
            return v;
        }
    }
}
