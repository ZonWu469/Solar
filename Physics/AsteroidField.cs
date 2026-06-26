using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Solar.Core;

namespace Solar.Physics
{
    /// <summary>Procedural asteroid field. Asteroids are ordinary on-rails <see cref="CelestialBody"/>s — so
    /// targeting, SOI capture, landing and drilling all work for free — but start hidden: they live in
    /// <see cref="Universe.AsteroidCatalog"/> and only enter the live hierarchy once discovered (telescope
    /// survey or fly-by radar, in <c>FlightScene</c>). Generation is fully deterministic from a seed, so a
    /// save persists only the seed plus the names already discovered (no orbital elements).</summary>
    public static class AsteroidField
    {
        private const double L = 0.1;             // length scale, matching SolarSystemData
        private const double AuKm = 149_597_870;  // 1 astronomical unit, km (pre-scale)

        /// <summary>Generate the full (hidden) asteroid catalog for a game from its seed. Names are stable by
        /// index (<c>AX-001</c>…), so the same seed always yields the same asteroids in the same order.</summary>
        public static List<CelestialBody> Generate(CelestialBody sun, long seed)
        {
            var list = new List<CelestialBody>();
            if (sun == null) return list;
            var rng = new Random(unchecked((int)seed ^ (int)(seed >> 32)));
            int count = Math.Max(0, Balance.AsteroidCount);

            for (int i = 0; i < count; i++)
            {
                bool neo = rng.NextDouble() < 0.12;                       // ~1 in 8 is a near-Earth crosser
                double aKm = (neo ? 0.9 + rng.NextDouble() * 0.6         // 0.9–1.5 AU
                                  : 2.1 + rng.NextDouble() * 1.2) * AuKm; // 2.1–3.3 AU main belt
                double ecc = neo ? 0.10 + rng.NextDouble() * 0.28 : rng.NextDouble() * 0.18;
                double radius = (0.2 + rng.NextDouble() * 4.8) * 1e3 * L; // 0.2–5 km, post-scale
                int tseed = rng.Next();

                var a = new CelestialBody
                {
                    Name = $"AX-{i + 1:000}",
                    TextureId = "asteroid",
                    IsAsteroid = true,
                    Parent = sun,
                    Mu = AsteroidMu(radius),
                    Radius = radius,
                    OreRichness = 0.55 + rng.NextDouble() * 0.4,          // 0.55–0.95: asteroids are rich ore
                    BodyColor = AsteroidColor(rng),
                    SoiRadius = Math.Max(radius * 60, Balance.AsteroidSoiM),
                    Terrain = new Terrain(radius, radius * 0.28, tseed, plains: 2),  // lumpy, but with flat spots to land
                    Orbit = new OrbitalElements
                    {
                        A = aKm * 1e3 * L,
                        E = ecc,
                        ArgPe = rng.NextDouble() * Math.PI * 2,
                        M0 = rng.NextDouble() * Math.PI * 2,
                        Epoch = 0,
                        Mu = sun.Mu,
                        Dir = 1,
                    },
                };
                list.Add(a);
            }
            return list;
        }

        /// <summary>Rebuild a game's asteroid field into the (persistent, shared) universe: clear any asteroids
        /// from a previous game, regenerate the catalog from the save's seed, then promote every
        /// already-discovered asteroid back into the live hierarchy. Call once when a game is started/loaded.</summary>
        public static void Sync(Universe u, GameState gs)
        {
            if (u == null || gs == null) return;
            // a save made before asteroids existed has no seed: derive a stable, save-specific one from its
            // weather seed so old games still get a (consistent) belt.
            if (gs.AsteroidSeed == 0) gs.AsteroidSeed = gs.WeatherSeed != 0 ? gs.WeatherSeed : 1;
            u.ClearAsteroids();
            // The belt orbits the home star (the Sun), not the galactic barycenter root. AU-scale orbits
            // only make sense around a star.
            u.AsteroidCatalog.AddRange(Generate(u["Sun"] ?? u.Root, gs.AsteroidSeed));
            if (gs.DiscoveredAsteroids != null)
                foreach (var name in gs.DiscoveredAsteroids)
                {
                    var a = u.AsteroidCatalog.Find(x => x.Name == name);
                    if (a != null && !u.Bodies.Contains(a)) u.Add(a);
                }
        }

        /// <summary>Mark an asteroid discovered: record its name on the save and promote it into the live
        /// hierarchy so it renders, can be targeted and can be encountered. Returns false if already known.</summary>
        public static bool Discover(Universe u, GameState gs, CelestialBody a)
        {
            if (u == null || gs == null || a == null || !a.IsAsteroid) return false;
            if (gs.DiscoveredAsteroids.Contains(a.Name)) return false;
            gs.DiscoveredAsteroids.Add(a.Name);
            if (!u.Bodies.Contains(a)) u.Add(a);
            return true;
        }

        // A gentle, size-scaled surface gravity (g = Mu/R^2 grows ~linearly with radius): big rocks pull a
        // little harder, but a touchdown is always slow and a descent always needs thrust.
        private static double AsteroidMu(double radius) => 2e-5 * radius * radius * radius;

        private static Color AsteroidColor(Random rng)
        {
            int v = 90 + rng.Next(55);   // grey base
            int warm = rng.Next(28);     // faint brown/red cast
            return new Color(Math.Min(255, v + warm), v, Math.Max(0, v - 12));
        }
    }
}
