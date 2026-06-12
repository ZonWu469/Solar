using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solar.Physics
{
    /// <summary>JSON-friendly definition of one celestial body, pre-scale. <see cref="SolarSystemData"/>
    /// applies the length/Mu scaling and wires up parent/orbit; this class only carries raw data.
    /// Mirrors the part/module catalog pattern (<see cref="Solar.Parts.ModuleCatalog"/>).</summary>
    public sealed class BodyDef
    {
        public string Name { get; set; }
        public string Id { get; set; }          // stable slug, used to locate a texture asset
        public string Parent { get; set; }       // parent body name; null/empty for the root (Sun)
        public int OrbitIndex { get; set; }      // creation order; drives the ArgPe/M0 phase spread
        public double AKm { get; set; }          // semi-major axis (km, pre-scale); 0 for the root
        public double Ecc { get; set; }
        public double MuReal { get; set; }       // gravitational parameter (m^3/s^2, pre-scale)
        public double RadiusKm { get; set; }     // body radius (km, pre-scale)
        public int R { get; set; }
        public int G { get; set; }
        public int B { get; set; }
        public bool HasAtmosphere { get; set; }
        public double AtmoSeaLevelDensity { get; set; }
        public double AtmoScaleHeight { get; set; }
        public double AtmoTop { get; set; }
        public int AtmoR { get; set; }
        public int AtmoG { get; set; }
        public int AtmoB { get; set; }
    }

    /// <summary>The celestial-body catalog. Loaded from Content/bodies.json at startup, falling back to
    /// <see cref="BuiltIn"/> (the in-code solar system) when the file is missing or unreadable.</summary>
    public static class BodyCatalog
    {
        public static List<BodyDef> All = BuiltIn();

        /// <summary>The real solar system at 1/10 length scale (see <see cref="SolarSystemData"/> for the
        /// scaling rationale). Ordered parent-before-child; OrbitIndex is the creation order.</summary>
        public static List<BodyDef> BuiltIn()
        {
            int i = 0;
            BodyDef Body(string name, string parent, double aKm, double ecc, double muReal, double radiusKm,
                         int r, int g, int b,
                         double atmoRho = 0, double atmoH = 0, double atmoTop = 0, int ar = 0, int ag = 0, int ab = 0)
                => new BodyDef
                {
                    Name = name, Id = name.ToLowerInvariant(), Parent = parent, OrbitIndex = i++,
                    AKm = aKm, Ecc = ecc, MuReal = muReal, RadiusKm = radiusKm,
                    R = r, G = g, B = b,
                    HasAtmosphere = atmoTop > 0,
                    AtmoSeaLevelDensity = atmoRho, AtmoScaleHeight = atmoH, AtmoTop = atmoTop,
                    AtmoR = ar, AtmoG = ag, AtmoB = ab,
                };

            return new List<BodyDef>
            {
                Body("Sun", null, 0, 0, 1.32712440018e20, 696340, 255, 220, 120),
                Body("Mercury", "Sun", 57_909e3, 0.2056, 2.2032e13, 2439.7, 150, 140, 130),
                Body("Venus", "Sun", 108_209e3, 0.0068, 3.24859e14, 6051.8, 225, 195, 130, 65.0, 6400, 80_000, 235, 210, 150),
                Body("Earth", "Sun", 149_596e3, 0.0167, 3.986004418e14, 6371, 70, 120, 200, 1.225, 5600, 56_000, 120, 170, 255),
                Body("Moon", "Earth", 384_400, 0.0549, 4.9048695e12, 1737.4, 170, 170, 175),
                Body("Mars", "Sun", 227_923e3, 0.0934, 4.282837e13, 3389.5, 200, 110, 70, 0.020, 8000, 50_000, 220, 160, 120),
                Body("Jupiter", "Sun", 778_570e3, 0.0489, 1.26686534e17, 69911, 210, 170, 130),
                Body("Io", "Jupiter", 421_800, 0.0040, 5.959916e12, 1821.6, 215, 205, 120),
                Body("Europa", "Jupiter", 671_100, 0.0090, 3.202739e12, 1560.8, 200, 190, 175),
                Body("Ganymede", "Jupiter", 1_070_400, 0.0013, 9.887834e12, 2634.1, 150, 140, 125),
                Body("Callisto", "Jupiter", 1_882_700, 0.0074, 7.179289e12, 2410.3, 110, 100, 95),
                Body("Saturn", "Sun", 1_433_530e3, 0.0565, 3.7931187e16, 58232, 220, 200, 150),
                Body("Titan", "Saturn", 1_221_870, 0.0288, 8.978138e12, 2574.7, 210, 160, 90, 5.4, 9000, 90_000, 225, 180, 110),
                Body("Uranus", "Sun", 2_872_460e3, 0.0457, 5.793939e15, 25362, 150, 210, 220),
                Body("Neptune", "Sun", 4_495_060e3, 0.0113, 6.836529e15, 24622, 70, 100, 220),
            };
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string FilePath => Path.Combine(System.AppContext.BaseDirectory, "Content", "bodies.json");

        /// <summary>Load the catalog from Content/bodies.json next to the exe. Falls back to the built-in
        /// list and rewrites a template file when the file is missing or invalid.</summary>
        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var defs = JsonSerializer.Deserialize<List<BodyDef>>(File.ReadAllText(FilePath), JsonOpts);
                    if (defs != null && defs.Count > 0)
                    {
                        All = defs;
                        return;
                    }
                }
            }
            catch { /* fall through to built-in + rewrite a clean template */ }

            All = BuiltIn();
            Save();
        }

        private static void Save()
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(All, JsonOpts)); }
            catch { /* read-only install dir: keep running with the in-memory catalog */ }
        }
    }
}
