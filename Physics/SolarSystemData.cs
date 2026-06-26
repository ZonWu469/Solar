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

        /// <summary>Body-local longitude of the launch pad (Earth). Bodies don't rotate, so this is a
        /// fixed world angle; the spawn code and the pad plain both reference it so they stay aligned.</summary>
        public const double LaunchPadAngle = System.Math.PI / 2;

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
                    OreRichness = d.OreRichness,
                    Parent = parent,
                    Atmo = d.HasAtmosphere ? new Atmosphere(d.AtmoSeaLevelDensity, d.AtmoScaleHeight, d.AtmoTop) : null,
                    BodyColor = new Color(d.R, d.G, d.B),
                    AtmoColor = d.HasAtmosphere ? new Color(d.AtmoR, d.AtmoG, d.AtmoB) : default,
                    RadBeltInner = d.RadBeltInnerKm * 1e3 * L,
                    RadBeltOuter = d.RadBeltOuterKm * 1e3 * L,
                    RadBeltDose = d.RadBeltDose,
                    IsStar = d.IsStar,
                };

                // Terrain: stars and the galactic barycenter are smooth (no surface); every orbiting
                // planet/moon gets relief (default amplitude unless overridden, 0 disables it explicitly).
                if (parent != null && !d.IsStar)
                {
                    double ampFrac = d.TerrainAmp ?? DefaultTerrainAmpFrac;
                    if (ampFrac > 0)
                    {
                        double amp = b.Radius * ampFrac;
                        int seed = d.TerrainSeed ?? d.Name.GetHashCode();
                        int plains = d.TerrainPlains ?? DefaultTerrainPlains;
                        // the home/launch body gets a guaranteed flat plain under the pad
                        double[] fixedPlains = d.Name == "Earth" ? new[] { LaunchPadAngle } : null;
                        b.Terrain = new Terrain(b.Radius, amp, seed, plains: plains, fixedPlains: fixedPlains);
                        AddNiches(b, seed);
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

        /// <summary>Place this body's natural livable niches on existing flat plains (deterministic from the
        /// terrain seed, so they regenerate identically each load — like the terrain itself). Airless/hostile
        /// worlds, where shelter matters most, are likelier to have them. Skips the Earth launch pad so the
        /// home site isn't a free niche.</summary>
        private static void AddNiches(CelestialBody b, int terrainSeed)
        {
            var plains = b.Terrain?.PlainCenters;
            if (plains == null || plains.Count == 0) return;
            var rng = new System.Random(terrainSeed * 31 + 7);
            int maxNiches = b.Atmo == null ? 2 : 1;           // airless worlds can host more
            double chance = b.Atmo == null ? 0.6 : 0.25;
            var used = new System.Collections.Generic.HashSet<int>();
            for (int k = 0; k < maxNiches && used.Count < plains.Count; k++)
            {
                if (rng.NextDouble() >= chance) continue;
                int idx;
                int guard = 0;
                do { idx = rng.Next(plains.Count); } while (!used.Add(idx) && ++guard < 16);
                if (b.Name == "Earth" && System.Math.Abs(plains[idx] - LaunchPadAngle) < 1e-6) continue;  // not the pad
                double half = 0.06 + rng.NextDouble() * 0.04;  // footprint within the flat well (~3.5-5.7 deg)
                b.Niches.Add(new LivableNiche(plains[idx], half, $"{b.Name} Niche {b.Niches.Count + 1}"));
            }
        }
    }
}
