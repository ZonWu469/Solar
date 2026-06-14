using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace Solar.Parts
{
    public static class PartCatalog
    {
        public const double ChuteDeployedCdA = 550;

        /// <summary>The live catalog. Populated from parts.json by <see cref="Load"/> at startup,
        /// falling back to <see cref="BuiltIn"/> when the file is missing or unreadable.</summary>
        public static List<PartDef> All = BuiltIn();

        public static PartDef Get(string name) => All.Find(p => p.Name == name);

        /// <summary>A flyable two-stage rocket so the first launch works out of the box.</summary>
        public static List<PartDef> DefaultDesign() => new()
        {
            Get("Parachute"),
            Get("Pod Mk1"),
            Get("Tank T400"),
            Get("Terrier"),
            Get("Decoupler"),
            Get("Tank T800"),
            Get("Fin Set"),
            Get("Swivel"),
        };

        /// <summary>The built-in fallback catalog, also used to seed parts.json on first run.</summary>
        public static List<PartDef> BuiltIn()
        {
            var list = new List<PartDef>
            {
            new PartDef { Name = "Pod Mk1", Id = "pod-mk1", Kind = PartKind.Pod, DryMass = 800, Width = 1.7, Height = 1.7, CdA = 1.2, ControlAuthority = 0.14, Tint = new Color(225, 228, 235) },
            new PartDef { Name = "Parachute", Id = "parachute", Kind = PartKind.Parachute, DryMass = 100, Width = 1.2, Height = 0.6, CdA = 0.4, DeployedCdA = 550, Tint = new Color(235, 130, 60) },
            new PartDef { Name = "Drogue Chute", Id = "drogue-chute", Kind = PartKind.Parachute, DryMass = 70, Width = 0.9, Height = 0.5, CdA = 0.3, DeployedCdA = 250, Tint = new Color(220, 150, 70) },
            new PartDef { Name = "Large Parachute", Id = "large-parachute", Kind = PartKind.Parachute, DryMass = 220, Width = 1.7, Height = 0.9, CdA = 0.5, DeployedCdA = 1100, Tint = new Color(240, 120, 55) },
            new PartDef { Name = "Radial Parachute", Id = "radial-parachute", Kind = PartKind.Parachute, DryMass = 90, Width = 0.8, Height = 1.1, CdA = 0.25, DeployedCdA = 350, Tint = new Color(235, 135, 65) },
            new PartDef { Name = "Radial Drogue", Id = "radial-drogue", Kind = PartKind.Parachute, DryMass = 60, Width = 0.7, Height = 0.9, CdA = 0.2, DeployedCdA = 180, Tint = new Color(225, 155, 75) },
            new PartDef { Name = "Tank T200", Id = "tank-t200", Kind = PartKind.Tank, DryMass = 240, FuelCapacity = 1800, Width = 1.7, Height = 1.6, CdA = 0.3, Tint = new Color(190, 205, 215) },
            new PartDef { Name = "Tank T400", Id = "tank-t400", Kind = PartKind.Tank, DryMass = 450, FuelCapacity = 3600, Width = 1.7, Height = 3.0, CdA = 0.3, Tint = new Color(190, 205, 215) },
            new PartDef { Name = "Tank T800", Id = "tank-t800", Kind = PartKind.Tank, DryMass = 820, FuelCapacity = 7200, Width = 1.7, Height = 5.4, CdA = 0.3, Tint = new Color(175, 195, 210) },
            new PartDef { Name = "Jumbo T2000", Id = "jumbo-t2000", Kind = PartKind.Tank, DryMass = 2100, FuelCapacity = 19800, Width = 2.3, Height = 7.8, CdA = 0.4, Tint = new Color(215, 170, 90) },
            new PartDef { Name = "Spark", Id = "spark", Kind = PartKind.Engine, DryMass = 280, Thrust = 28_000, Isp = 320, Width = 1.1, Height = 1.0, CdA = 0.3, Tint = new Color(140, 145, 155) },
            new PartDef { Name = "Terrier", Id = "terrier", Kind = PartKind.Engine, DryMass = 560, Thrust = 65_000, Isp = 350, Width = 1.6, Height = 1.4, CdA = 0.35, Tint = new Color(140, 145, 155) },
            new PartDef { Name = "Swivel", Id = "swivel", Kind = PartKind.Engine, DryMass = 1550, Thrust = 215_000, Isp = 305, Width = 1.7, Height = 2.0, CdA = 0.4, Tint = new Color(120, 125, 135) },
            new PartDef { Name = "Mainsail", Id = "mainsail", Kind = PartKind.Engine, DryMass = 3200, Thrust = 680_000, Isp = 290, Width = 2.3, Height = 2.8, CdA = 0.5, Tint = new Color(110, 115, 125) },
            new PartDef { Name = "Decoupler", Id = "decoupler", Kind = PartKind.Decoupler, DryMass = 110, Width = 1.7, Height = 0.45, CdA = 0.1, Tint = new Color(200, 180, 70) },
            new PartDef { Name = "Fin Set", Id = "fin-set", Kind = PartKind.Fins, DryMass = 90, Width = 2.8, Height = 1.0, CdA = 0.6, Tint = new Color(170, 80, 80) },
            // progression-gated parts (see Progression/TechTree.cs)
            new PartDef { Name = "Thumper SRB", Id = "thumper-srb", Kind = PartKind.SolidBooster, DryMass = 450, FuelCapacity = 3000, Thrust = 240_000, Isp = 195, Width = 1.2, Height = 4.4, CdA = 0.35, Tint = new Color(225, 225, 230) },
            new PartDef { Name = "Nerv", Id = "nerv", Kind = PartKind.Engine, DryMass = 3000, Thrust = 60_000, Isp = 800, Width = 1.7, Height = 3.0, CdA = 0.4, Tint = new Color(150, 150, 130) },
            new PartDef { Name = "Tank T1600", Id = "tank-t1600", Kind = PartKind.Tank, DryMass = 1640, FuelCapacity = 14400, Width = 2.3, Height = 6.4, CdA = 0.4, Tint = new Color(175, 195, 210) },
            // aero / utility
            new PartDef { Name = "Nose Cone", Id = "nose-cone", Kind = PartKind.Aero, DryMass = 80, Width = 1.7, Height = 1.4, CdA = 0.2, Tint = new Color(205, 210, 220) },
            // radial-mounted parts (attach as a symmetric pair to a stack part; see Vessel radial layer)
            new PartDef { Name = "Thumper-R", Id = "thumper-r", Kind = PartKind.SolidBooster, DryMass = 300, FuelCapacity = 1800, Thrust = 160_000, Isp = 190, Width = 1.0, Height = 3.6, CdA = 0.3, Tint = new Color(220, 220, 225) },
            new PartDef { Name = "Radial Tank", Id = "radial-tank", Kind = PartKind.Tank, DryMass = 280, FuelCapacity = 2400, Width = 1.0, Height = 3.4, CdA = 0.25, Tint = new Color(185, 200, 212) },
            new PartDef { Name = "Radial Decoupler", Id = "radial-decoupler", Kind = PartKind.RadialDecoupler, DryMass = 50, Width = 0.6, Height = 0.8, CdA = 0.05, Tint = new Color(200, 180, 70) },
            // ---- expanded parts (see Progression/TechTree.cs for unlock nodes) ----
            // pods
            new PartDef { Name = "Pod Mk2", Id = "pod-mk2", Kind = PartKind.Pod, DryMass = 2500, Width = 2.3, Height = 2.2, CdA = 1.5, ControlAuthority = 0.20, Tint = new Color(215, 220, 230) },
            new PartDef { Name = "Pod Mk3", Id = "pod-mk3", Kind = PartKind.Pod, DryMass = 6000, Width = 3.0, Height = 3.0, CdA = 2.0, ControlAuthority = 0.28, Tint = new Color(200, 210, 225) },
            new PartDef { Name = "Probe Core", Id = "probe-core", Kind = PartKind.Pod, DryMass = 150, Width = 0.8, Height = 0.8, CdA = 0.15, ControlAuthority = 0.10, Tint = new Color(180, 180, 200) },
            new PartDef { Name = "Probe Core XL", Id = "probe-core-xl", Kind = PartKind.Pod, DryMass = 450, Width = 1.4, Height = 1.2, CdA = 0.4, ControlAuthority = 0.12, Tint = new Color(170, 170, 195) },
            // tanks
            new PartDef { Name = "Tank T100", Id = "tank-t100", Kind = PartKind.Tank, DryMass = 120, FuelCapacity = 900, Width = 1.2, Height = 0.9, CdA = 0.2, Tint = new Color(195, 210, 220) },
            new PartDef { Name = "Tank T4000", Id = "tank-t4000", Kind = PartKind.Tank, DryMass = 3800, FuelCapacity = 36000, Width = 3.0, Height = 9.0, CdA = 0.5, Tint = new Color(210, 160, 85) },
            new PartDef { Name = "Tank T8000", Id = "tank-t8000", Kind = PartKind.Tank, DryMass = 7200, FuelCapacity = 72000, Width = 3.4, Height = 12.0, CdA = 0.6, Tint = new Color(200, 145, 80) },
            new PartDef { Name = "Tank T400-R", Id = "tank-t400-r", Kind = PartKind.Tank, DryMass = 560, FuelCapacity = 4800, Width = 1.2, Height = 4.8, CdA = 0.3, Tint = new Color(180, 195, 208) },
            // engines
            new PartDef { Name = "Dart", Id = "dart", Kind = PartKind.Engine, DryMass = 1000, Thrust = 140_000, Isp = 340, Width = 1.5, Height = 1.8, CdA = 0.35, Tint = new Color(130, 135, 145) },
            new PartDef { Name = "Ion Drive", Id = "ion-drive", Kind = PartKind.Engine, DryMass = 250, Thrust = 2_000, Isp = 4200, Width = 1.0, Height = 1.2, CdA = 0.15, Tint = new Color(140, 160, 220) },
            new PartDef { Name = "Vector", Id = "vector", Kind = PartKind.Engine, DryMass = 4000, Thrust = 1_000_000, Isp = 295, Width = 2.0, Height = 2.4, CdA = 0.45, Tint = new Color(105, 110, 120) },
            new PartDef { Name = "Poodle", Id = "poodle", Kind = PartKind.Engine, DryMass = 1750, Thrust = 250_000, Isp = 350, Width = 2.0, Height = 2.2, CdA = 0.4, Tint = new Color(150, 155, 170) },
            new PartDef { Name = "Rhino", Id = "rhino", Kind = PartKind.Engine, DryMass = 6500, Thrust = 2_000_000, Isp = 340, Width = 3.0, Height = 3.4, CdA = 0.55, Tint = new Color(100, 105, 115) },
            new PartDef { Name = "Dawn", Id = "dawn", Kind = PartKind.Engine, DryMass = 25, Thrust = 300, Isp = 260, Width = 0.5, Height = 0.6, CdA = 0.05, Tint = new Color(180, 185, 200) },
            // docking
            new PartDef { Name = "Docking Port Sr", Id = "docking-port-sr", Kind = PartKind.DockingPort, DryMass = 250, Width = 2.3, Height = 0.8, CdA = 0.1, Tint = new Color(190, 185, 200) },
            new PartDef { Name = "Docking Port Jr", Id = "docking-port-jr", Kind = PartKind.DockingPort, DryMass = 60, Width = 1.2, Height = 0.6, CdA = 0.06, Tint = new Color(195, 190, 205) },
            new PartDef { Name = "Shielded Port", Id = "shielded-port", Kind = PartKind.DockingPort, DryMass = 350, Width = 1.7, Height = 1.0, CdA = 0.15, Tint = new Color(185, 180, 195) },
            // landing gear
            new PartDef { Name = "Landing Gear", Id = "landing-gear", Kind = PartKind.LandingGear, DryMass = 300, Width = 1.2, Height = 1.5, CdA = 0.1, ImpactTolerance = 14, Tint = new Color(160, 165, 175) },
            new PartDef { Name = "Heavy Landing Gear", Id = "heavy-landing-gear", Kind = PartKind.LandingGear, DryMass = 800, Width = 2.0, Height = 2.2, CdA = 0.2, ImpactTolerance = 24, Tint = new Color(150, 155, 165) },
            // ---- NEW: structural bays & trusses (module carriers) ----
            new PartDef { Name = "Service Bay 1.7m",  Id = "service-bay-1-7m",   Kind = PartKind.StructuralBay, DryMass = 150, Width = 1.7, Height = 1.0, CdA = 0.3, Tint = new Color(140, 150, 165) },
            new PartDef { Name = "Service Bay 2.3m",  Id = "service-bay-2-3m",   Kind = PartKind.StructuralBay, DryMass = 300, Width = 2.3, Height = 1.5, CdA = 0.45, Tint = new Color(135, 145, 160) },
            new PartDef { Name = "Cargo Truss",       Id = "cargo-truss",        Kind = PartKind.StructuralBay, DryMass = 100, Width = 1.7, Height = 2.5, CdA = 0.2, Tint = new Color(160, 165, 175) },
            new PartDef { Name = "Stack Bi-Adapter",  Id = "stack-bi-adapter",   Kind = PartKind.StructuralBay, DryMass = 200, Width = 1.7, Height = 1.2, CdA = 0.25, Tint = new Color(150, 155, 170) },
            new PartDef { Name = "Stack Tri-Adapter", Id = "stack-tri-adapter",  Kind = PartKind.StructuralBay, DryMass = 350, Width = 2.3, Height = 1.5, CdA = 0.35, Tint = new Color(145, 150, 165) },
            // beams & platforms (structural connectors; mount inline or radially with Alt)
            new PartDef { Name = "I-Beam",           Id = "i-beam",            Kind = PartKind.StructuralBay, DryMass = 60,  Width = 0.4, Height = 2.6, CdA = 0.08, Tint = new Color(150, 155, 165) },
            new PartDef { Name = "Long I-Beam",      Id = "long-i-beam",       Kind = PartKind.StructuralBay, DryMass = 110, Width = 0.4, Height = 4.8, CdA = 0.12, Tint = new Color(150, 155, 165) },
            new PartDef { Name = "Flat Platform",    Id = "flat-platform",     Kind = PartKind.StructuralBay, DryMass = 180, Width = 2.8, Height = 0.4, CdA = 0.30, Tint = new Color(158, 163, 173) },
            new PartDef { Name = "Heavy Platform",   Id = "heavy-platform",    Kind = PartKind.StructuralBay, DryMass = 420, Width = 3.6, Height = 0.5, CdA = 0.45, Tint = new Color(152, 157, 167) },
            // ---- NEW: engines ----
            new PartDef { Name = "Aerospike",         Id = "aerospike",          Kind = PartKind.Engine,  DryMass = 1200, Thrust = 180_000, Isp = 345, Width = 1.7, Height = 2.0, CdA = 0.4, Tint = new Color(130, 135, 140) },
            new PartDef { Name = "Nuclear Lightbulb", Id = "nuclear-lightbulb",  Kind = PartKind.Engine,  DryMass = 5000, Thrust = 120_000, Isp = 1500, Width = 2.3, Height = 4.0, CdA = 0.5, Tint = new Color(180, 140, 80) },
            new PartDef { Name = "PIT Thruster",      Id = "pit-thruster",       Kind = PartKind.Engine,  DryMass = 400,  Thrust = 500,     Isp = 5000, Width = 1.2, Height = 1.4, CdA = 0.15, Tint = new Color(140, 160, 230) },
            // ---- NEW: solid booster ----
            new PartDef { Name = "Sledgehammer SRB",  Id = "sledgehammer-srb",   Kind = PartKind.SolidBooster, DryMass = 1200, FuelCapacity = 8000, Thrust = 800_000, Isp = 200, Width = 1.7, Height = 6.0, CdA = 0.5, Tint = new Color(230, 220, 215) },
            // ---- NEW: tank ----
            new PartDef { Name = "Toroidal Tank T1200", Id = "toroidal-tank-t1200", Kind = PartKind.Tank, DryMass = 1300, FuelCapacity = 10800, Width = 2.3, Height = 2.0, CdA = 0.4, Tint = new Color(170, 185, 200) },
            // ---- NEW: pod ----
            new PartDef { Name = "Mk1 Lander Can",    Id = "mk1-lander-can",     Kind = PartKind.Pod, DryMass = 1200, Width = 2.3, Height = 1.8, CdA = 1.0, ControlAuthority = 0.14, Tint = new Color(210, 215, 225) },
            new PartDef { Name = "Lander Can 2",      Id = "lander-can-2",       Kind = PartKind.Pod, DryMass = 1800, Width = 2.5, Height = 2.0, CdA = 1.2, ControlAuthority = 0.20, Sas = true, Slots = 3, Tint = new Color(205, 212, 222) },
            new PartDef { Name = "Big Pod",           Id = "big-pod",            Kind = PartKind.Pod, DryMass = 6500, Width = 2.5, Height = 3.6, CdA = 2.0, ControlAuthority = 0.30, Sas = true, Slots = 4, Tint = new Color(200, 208, 222) },
            // ---- NEW: advanced parts (ship with modules pre-fitted in their slots; still editable) ----
            new PartDef { Name = "Service Pod Mk1", Id = "service-pod-mk1", Kind = PartKind.Pod, DryMass = 1100, Width = 1.7, Height = 1.9, CdA = 1.2, ControlAuthority = 0.14, Tint = new Color(220, 222, 232),
                          DefaultModules = { "Solar Panel", "Battery", "Antenna" } },
            new PartDef { Name = "Comsat Core", Id = "comsat-core", Kind = PartKind.Pod, DryMass = 320, Width = 0.9, Height = 0.9, CdA = 0.2, ControlAuthority = 0.10, Tint = new Color(175, 180, 205),
                          DefaultModules = { "RTG", "Battery", "Relay Antenna" } },
            new PartDef { Name = "Power Service Bay", Id = "power-service-bay", Kind = PartKind.StructuralBay, DryMass = 220, Width = 1.7, Height = 1.1, CdA = 0.3, Tint = new Color(140, 152, 168),
                          DefaultModules = { "Solar Panel", "Battery" } },
            };
            // built-in defs declare Slots only where it differs from the per-kind default; backfill the rest
            foreach (var p in list) if (p.Slots == 0) p.Slots = PartDef.DefaultSlots(p.Kind);
            return list;
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "Content", "parts.json");

        /// <summary>Load the catalog from parts.json next to the exe. If it is missing or invalid,
        /// fall back to the built-in list and (re)write a template file players can edit.</summary>
        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var dtos = JsonSerializer.Deserialize<List<PartDefDto>>(File.ReadAllText(FilePath), JsonOpts);
                    if (dtos != null && dtos.Count > 0)
                    {
                        var list = new List<PartDef>(dtos.Count);
                        foreach (var d in dtos) list.Add(d.ToPart());
                        // upgrade older files in place: backfill a missing Id and merge in any newer
                        // built-in parts the on-disk file predates (so code-defined additions appear
                        // without players deleting parts.json). Re-save when anything changed.
                        bool changed = false;
                        foreach (var d in dtos) if (string.IsNullOrEmpty(d.Id)) changed = true;
                        foreach (var b in BuiltIn())
                            if (list.Find(p => p.Name == b.Name) == null) { list.Add(b); changed = true; }
                        All = list;
                        if (changed) Save();
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
            try
            {
                var dtos = new List<PartDefDto>(All.Count);
                foreach (var p in All) dtos.Add(PartDefDto.FromPart(p));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(dtos, JsonOpts));
            }
            catch { /* read-only install dir: keep running with the in-memory catalog */ }
        }
    }

    /// <summary>JSON-friendly mirror of <see cref="PartDef"/> (MonoGame's Color isn't serializable).</summary>
    public sealed class PartDefDto
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public PartKind Kind { get; set; }
        public double DryMass { get; set; }
        public double FuelCapacity { get; set; }
        public double Thrust { get; set; }
        public double Isp { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double CdA { get; set; }
        public double DeployedCdA { get; set; }
        public double ControlAuthority { get; set; }
        public bool Sas { get; set; }
        public double ImpactTolerance { get; set; }
        public int Slots { get; set; }
        public int TintR { get; set; }
        public int TintG { get; set; }
        public int TintB { get; set; }
        public List<string> DefaultModules { get; set; }   // module names pre-fitted in this part's slots (optional)

        public static PartDefDto FromPart(PartDef p) => new()
        {
            Name = p.Name, Id = p.Id, Kind = p.Kind, DryMass = p.DryMass, FuelCapacity = p.FuelCapacity,
            Thrust = p.Thrust, Isp = p.Isp, Width = p.Width, Height = p.Height, CdA = p.CdA,
            DeployedCdA = p.DeployedCdA, ControlAuthority = p.ControlAuthority, Sas = p.Sas, ImpactTolerance = p.ImpactTolerance,
            Slots = p.Slots,
            TintR = p.Tint.R, TintG = p.Tint.G, TintB = p.Tint.B,
            DefaultModules = p.DefaultModules.Count > 0 ? new List<string>(p.DefaultModules) : null,
        };

        public PartDef ToPart() => new()
        {
            Name = Name, Id = string.IsNullOrEmpty(Id) ? PartDef.Slug(Name) : Id,
            Kind = Kind, DryMass = DryMass, FuelCapacity = FuelCapacity,
            Thrust = Thrust, Isp = Isp, Width = Width, Height = Height, CdA = CdA,
            // migration-safe fallbacks so older parts.json (missing these fields) still behaves:
            DeployedCdA = DeployedCdA > 0 ? DeployedCdA : (Kind == PartKind.Parachute ? PartCatalog.ChuteDeployedCdA : 0),
            ControlAuthority = ControlAuthority > 0 ? ControlAuthority : (Kind == PartKind.Pod ? DefaultPodAuthority(DryMass) : 0),
            Sas = Sas || Kind == PartKind.Pod,   // command pods/probe cores carry SAS unless JSON says otherwise
            ImpactTolerance = ImpactTolerance > 0 ? ImpactTolerance : (Kind == PartKind.LandingGear ? DefaultGearTolerance(DryMass) : 0),
            Slots = Slots > 0 ? Slots : PartDef.DefaultSlots(Kind),
            Tint = new Color(TintR, TintG, TintB),
            DefaultModules = DefaultModules ?? new List<string>(),
        };

        // by-size defaults used only when an entry omits the field (older/edited JSON)
        private static double DefaultPodAuthority(double dryMass) =>
            dryMass < 500 ? 0.10 : dryMass < 2000 ? 0.14 : dryMass < 4000 ? 0.20 : 0.28;
        private static double DefaultGearTolerance(double dryMass) => dryMass < 500 ? 14.0 : 24.0;
    }
}
