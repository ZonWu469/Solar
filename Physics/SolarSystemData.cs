using Microsoft.Xna.Framework;

namespace Solar.Physics
{
    /// <summary>
    /// The real solar system at 1/10 length scale. All lengths (radii, semi-major axes)
    /// are divided by 10 and every Mu by 100, which preserves real surface gravity
    /// (g = mu/r^2) and shortens orbital periods by sqrt(10) (~115-day Earth year).
    /// Earth's SOI comes out at ~92,500 km with the Moon at 38,440 km - safely inside.
    /// </summary>
    public static class SolarSystemData
    {
        private const double L = 0.1;    // length scale
        private const double GM = 0.01;  // mu scale

        // Terrain defaults applied when a body omits the values in bodies.json.
        private const double DefaultTerrainAmpFrac = 0.02;   // peak relief = 2% of radius
        private const int DefaultTerrainPlains = 3;

        public static Universe Create()
        {
            var u = new Universe();

            // Bodies come from BodyCatalog (Content/bodies.json, or the in-code fallback), ordered
            // parent-before-child so each parent already exists when its children are wired up.
            foreach (var d in BodyCatalog.All)
            {
                var parent = string.IsNullOrEmpty(d.Parent) ? null : u[d.Parent];
                var b = new CelestialBody
                {
                    Name = d.Name,
                    TextureId = d.Id,
                    Mu = d.MuReal * GM,
                    Radius = d.RadiusKm * 1e3 * L,
                    Parent = parent,
                    Atmo = d.HasAtmosphere ? new Atmosphere(d.AtmoSeaLevelDensity, d.AtmoScaleHeight, d.AtmoTop) : null,
                    BodyColor = new Color(d.R, d.G, d.B),
                    AtmoColor = d.HasAtmosphere ? new Color(d.AtmoR, d.AtmoG, d.AtmoB) : default,
                };

                // Terrain: the root star is smooth; every orbiting body gets relief (default amplitude
                // unless overridden, 0 disables it explicitly).
                if (parent != null)
                {
                    double ampFrac = d.TerrainAmp ?? DefaultTerrainAmpFrac;
                    if (ampFrac > 0)
                    {
                        double amp = b.Radius * ampFrac;
                        int seed = d.TerrainSeed ?? d.Name.GetHashCode();
                        int plains = d.TerrainPlains ?? DefaultTerrainPlains;
                        b.Terrain = new Terrain(b.Radius, amp, seed, plains: plains);
                    }
                }
                if (parent != null)
                {
                    b.Orbit = new OrbitalElements
                    {
                        A = d.AKm * 1e3 * L,
                        E = d.Ecc,
                        ArgPe = d.OrbitIndex * 1.7,       // arbitrary spread
                        M0 = d.OrbitIndex * 2.399963,     // golden-angle phase spread
                        Epoch = 0,
                        Mu = parent.Mu,
                        Dir = 1,
                    };
                }
                u.Add(b);
            }

            u.ComputeSoiRadii();
            return u;
        }
    }
}
