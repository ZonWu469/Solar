using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace Solar.Parts
{
    /// <summary>The slot-module catalog. Loaded from Content/modules.json at startup,
    /// falling back to <see cref="BuiltIn"/> when the file is missing or unreadable.</summary>
    public static class ModuleCatalog
    {
        public static List<ModuleDef> All = BuiltIn();

        public static ModuleDef Get(string name) => All.Find(m => m.Name == name);

        public static List<ModuleDef> BuiltIn() => new()
        {
            new ModuleDef { Name = "Solar Panel", Id = "solar-panel", Description = "Converts sunlight into electric charge. Must be deployed. Efficiency decreases with distance from the Sun.", Kind = ModuleKind.SolarPanel, DryMass = 40,  Activatable = true,  EcProduce = 8.0,  Tint = new Color(70, 130, 235) },
            new ModuleDef { Name = "RTG",         Id = "rtg",         Description = "Radioisotope Thermoelectric Generator. Produces a trickle of power anywhere, even in deep shadow.", Kind = ModuleKind.Rtg,        DryMass = 90,  Activatable = false, EcProduce = 1.5,  Tint = new Color(200, 120, 70) },
            new ModuleDef { Name = "Battery",     Id = "battery",     Description = "Stores electric charge for later use. Essential for surviving the night side of orbits.", Kind = ModuleKind.Battery,    DryMass = 30,  Activatable = false, EcCapacity = 2000, Tint = new Color(220, 210, 120) },
            new ModuleDef { Name = "Life Support", Id = "life-support", Description = "Provides water, oxygen, and food storage for crewed missions. Consumes a trickle of EC.", Kind = ModuleKind.LifeSupport, DryMass = 140, Activatable = false, OxygenCapacity = 216000, WaterCapacity = 120000, FoodCapacity = 80000, EcDraw = 0.2, Tint = new Color(120, 210, 150) },
            new ModuleDef { Name = "Drill",       Id = "drill",       Description = "Surface mining drill. Produces fuel from ore when landed and active.", Kind = ModuleKind.Harvester,  DryMass = 260, Activatable = true,  FuelProduce = 3.0, EcDraw = 4.0, Tint = new Color(170, 160, 175) },
            // progression-gated modules (see Progression/TechTree.cs)
            new ModuleDef { Name = "Reaction Wheel", Id = "reaction-wheel", Description = "Gyroscopic attitude control. Provides torque for turning in vacuum without RCS.", Kind = ModuleKind.ReactionWheel, DryMass = 50, Activatable = false, EcDraw = 0.4, Tint = new Color(150, 170, 210) },
            new ModuleDef { Name = "Science Jr",     Id = "science-jr",     Description = "Multi-purpose science bay. Runs diverse experiments and returns valuable data.", Kind = ModuleKind.Science,       DryMass = 120, Activatable = true, EcDraw = 0.3, Tint = new Color(120, 200, 220) },
            new ModuleDef { Name = "Antenna",        Id = "antenna",        Description = "Transmits science data back to Kerbin for full credit. Limited range — stay within 40 Mm.", Kind = ModuleKind.Antenna,       DryMass = 30,  Activatable = true, EcDraw = 0.6, Range = 4e7, Tint = new Color(210, 210, 220) },
            // start-unlocked smalls + new mechanics (see Progression/TechTree.cs)
            new ModuleDef { Name = "Battery Z-100", Id = "battery-z100", Description = "Compact battery pack. Light enough to tack onto any probe.", Kind = ModuleKind.Battery, DryMass = 15, Activatable = false, EcCapacity = 200, Tint = new Color(220, 210, 120) },
            new ModuleDef { Name = "Mystery Goo",   Id = "mystery-goo",  Description = "Exposes a sealed canister of... something... to the space environment. Purely for science!", Kind = ModuleKind.Science, DryMass = 60, Activatable = true, EcDraw = 0.2, Tint = new Color(150, 210, 120) },
            new ModuleDef { Name = "Comm-16 Antenna", Id = "comm-16",    Description = "Compact whip antenna for short-range communications and science transmission.", Kind = ModuleKind.Antenna, DryMass = 20, Activatable = true, EcDraw = 0.3, Range = 2e7, Tint = new Color(200, 200, 210) },
            new ModuleDef { Name = "Fuel Cell",     Id = "fuel-cell",    Description = "Converts liquid fuel and oxidizer into electric charge on demand.", Kind = ModuleKind.FuelCell, DryMass = 80, Activatable = true, EcProduce = 6.0, FuelDraw = 0.4, Tint = new Color(190, 160, 110) },
            new ModuleDef { Name = "Battery Bank",  Id = "battery-bank", Description = "High-capacity battery bank. Powers demanding equipment through long orbital nights.", Kind = ModuleKind.Battery, DryMass = 120, Activatable = false, EcCapacity = 8000, Tint = new Color(230, 215, 110) },
            new ModuleDef { Name = "Thermometer",   Id = "thermometer",  Description = "Measures ambient temperature. Simple, reliable, and always earns a few science points.", Kind = ModuleKind.Science, DryMass = 40, Activatable = true, EcDraw = 0.2, Tint = new Color(120, 200, 220) },
            new ModuleDef { Name = "Barometer",     Id = "barometer",    Description = "Measures atmospheric pressure. Works best on bodies with an atmosphere.", Kind = ModuleKind.Science, DryMass = 40, Activatable = true, EcDraw = 0.2, Tint = new Color(120, 180, 220) },
            new ModuleDef { Name = "Relay Antenna", Id = "relay-antenna", Description = "Long-range relay dish. Rebroadcasts signals from distant probes to extend your comms network.", Kind = ModuleKind.Antenna, DryMass = 80, Activatable = true, EcDraw = 1.2, Range = 2e12, Relay = true, Tint = new Color(215, 215, 230) },
            new ModuleDef { Name = "Landing Legs",  Id = "landing-legs", Description = "Extendable landing legs. Absorb touchdown shock for controlled surface landings.", Kind = ModuleKind.LandingLeg, DryMass = 60, Activatable = false, Tint = new Color(150, 155, 165) },
            // ---- expanded modules (see Progression/TechTree.cs for unlock nodes) ----
            // science instruments
            new ModuleDef { Name = "Gravioli Detector",     Id = "gravioli-detector",     Description = "Detects gravity waves and subtle variations in the local gravitational field.", Kind = ModuleKind.Science, DryMass = 80,  Activatable = true, EcDraw = 0.5, Tint = new Color(130, 210, 230) },
            new ModuleDef { Name = "Atmosphere Analyzer",   Id = "atmosphere-analyzer",   Description = "Analyzes atmospheric composition in detail. Ideal for probing unfamiliar worlds.", Kind = ModuleKind.Science, DryMass = 65,  Activatable = true, EcDraw = 0.4, Tint = new Color(130, 190, 225) },
            new ModuleDef { Name = "Seismic Accelerometer", Id = "seismic-accelerometer", Description = "Measures ground vibrations. Deploy on a planet's surface to study its interior.", Kind = ModuleKind.Science, DryMass = 90,  Activatable = true, EcDraw = 0.6, Tint = new Color(130, 200, 215) },
            new ModuleDef { Name = "Magnetometer",          Id = "magnetometer",          Description = "Maps planetary magnetic fields. Higher returns near magnetospheres.", Kind = ModuleKind.Science, DryMass = 70,  Activatable = true, EcDraw = 0.3, Tint = new Color(140, 215, 235) },
            new ModuleDef { Name = "Ore Scanner",           Id = "ore-scanner",           Description = "Surveys surface ore concentrations from orbit. Find the best mining spots.", Kind = ModuleKind.Science, DryMass = 60,  Activatable = true, EcDraw = 0.4, Tint = new Color(145, 205, 225) },
            new ModuleDef { Name = "Docking Sensor",        Id = "docking-sensor",        Description = "Short-range proximity sensor. Helps guide the final meters of a docking approach.", Kind = ModuleKind.Science, DryMass = 25,  Activatable = true, EcDraw = 0.1, Tint = new Color(200, 185, 210) },
            // power
            new ModuleDef { Name = "Large Solar Array", Id = "large-solar-array", Description = "Large deployable solar array. Generates substantial power for energy-hungry vessels.", Kind = ModuleKind.SolarPanel, DryMass = 100, Activatable = true,  EcProduce = 20.0, Tint = new Color(65, 135, 245) },
            new ModuleDef { Name = "Gigantor XL",       Id = "gigantor-xl",      Description = "Massive solar array. Powers the largest stations and deep-space motherships.", Kind = ModuleKind.SolarPanel, DryMass = 300, Activatable = true,  EcProduce = 45.0, Tint = new Color(60, 140, 255) },
            new ModuleDef { Name = "Nuclear Reactor",   Id = "nuclear-reactor",  Description = "Compact fission reactor. Abundant passive power anywhere — but heavy.", Kind = ModuleKind.Rtg,        DryMass = 450, Activatable = false, EcProduce = 8.0,  Tint = new Color(240, 100, 60) },
            // batteries
            new ModuleDef { Name = "Battery Z-4000", Id = "battery-z4000", Description = "Heavy-duty battery bank. Enough reserve to survive long eclipses.", Kind = ModuleKind.Battery, DryMass = 240, Activatable = false, EcCapacity = 20000, Tint = new Color(235, 220, 105) },
            new ModuleDef { Name = "Battery Z-10k",  Id = "battery-z10k",  Description = "Massive battery array. Stores enough EC to run entire outposts through the night.", Kind = ModuleKind.Battery, DryMass = 500, Activatable = false, EcCapacity = 50000, Tint = new Color(240, 225, 100) },
            // antennas
            new ModuleDef { Name = "Comm-88 Antenna", Id = "comm-88",        Description = "High-gain relay antenna. Keeps deep-space missions connected to the home world.", Kind = ModuleKind.Antenna, DryMass = 80,  Activatable = true, EcDraw = 1.0, Range = 2e13, Relay = true, Tint = new Color(215, 215, 235) },
            new ModuleDef { Name = "Comm-DSN Dish",   Id = "comm-dsn-dish",  Description = "Giant Deep Space Network dish. Can phone home from the outer planets.", Kind = ModuleKind.Antenna, DryMass = 150, Activatable = true, EcDraw = 2.5, Range = 2e14, Relay = true, Tint = new Color(220, 220, 240) },
            // resource
            new ModuleDef { Name = "ISRU Converter", Id = "isru-converter", Description = "In-Situ Resource Utilization converter. Refines ore into usable fuel anywhere.", Kind = ModuleKind.Harvester, DryMass = 400, Activatable = true, FuelProduce = 8.0, EcDraw = 15.0, Tint = new Color(180, 150, 140) },
            // lights
            new ModuleDef { Name = "Utility Light", Id = "utility-light", Description = "Small electric light. Illuminates nearby terrain for night operations.", Kind = ModuleKind.Light, DryMass = 15,  Activatable = true, EcDraw = 0.05, Tint = new Color(255, 240, 200) },
            new ModuleDef { Name = "Spotlight",      Id = "spotlight",      Description = "Powerful directed spotlight. Cuts through darkness for precise night landings.", Kind = ModuleKind.Light, DryMass = 30,  Activatable = true, EcDraw = 0.15, Tint = new Color(255, 245, 210) },
            // monoprop / RCS
            new ModuleDef { Name = "Monoprop Tank",         Id = "monoprop-tank",         Description = "Stores monopropellant for RCS thrusters. Essential for docking maneuvers.", Kind = ModuleKind.Tank, DryMass = 80,  FuelCapacity = 500,  Tint = new Color(220, 180, 80) },
            new ModuleDef { Name = "RCS Thruster Block",   Id = "rcs-thruster-block",    Description = "Reaction Control System thruster block. Provides fine translation control.", Kind = ModuleKind.RCS,  DryMass = 50,  Activatable = false, EcDraw = 0.3, Tint = new Color(170, 175, 190) },
            // life support
            new ModuleDef { Name = "Crew Cabin",        Id = "crew-cabin",        Description = "Habitation module with four seats and integrated life support storage.", Kind = ModuleKind.LifeSupport, DryMass = 600, Activatable = false, CrewCapacity = 4, OxygenCapacity = 432000, WaterCapacity = 240000, FoodCapacity = 160000, EcDraw = 0.4, SlotCost = 2, Tint = new Color(130, 220, 155) },
            new ModuleDef { Name = "Hydroponics Bay",   Id = "hydroponics-bay",   Description = "Grows food and regenerates oxygen from CO₂. Greatly extends mission endurance.", Kind = ModuleKind.LifeSupport, DryMass = 350, Activatable = true,  FoodCapacity = 40000, OxygenRegen = 0.6, FoodRegen = 0.4, EcDraw = 4.0, SlotCost = 2, Tint = new Color(100, 210, 130) },
            // ---- NEW: near-future modules (Progression/TechTree.cs for unlock nodes) ----
            // advanced power
            new ModuleDef { Name = "Stirling RTG",       Id = "stirling-rtg",       Description = "Advanced radioisotope generator with a Stirling-cycle converter. Higher efficiency than standard RTGs.", Kind = ModuleKind.Rtg,        DryMass = 120, Activatable = false, EcProduce = 4.0, Tint = new Color(210, 130, 60) },
            // advanced life support
            new ModuleDef { Name = "CO₂ Scrubber",       Id = "co2-scrubber",       Description = "Advanced CO₂ recycling system. Regenerates breathable oxygen from cabin atmosphere.", Kind = ModuleKind.LifeSupport, DryMass = 180, Activatable = true, OxygenRegen = 1.2, EcDraw = 1.5, SlotCost = 2, Tint = new Color(90, 200, 140) },
            new ModuleDef { Name = "Water Recycler",     Id = "water-recycler",     Description = "Purifies wastewater back into drinkable water. Dramatically extends crew endurance.", Kind = ModuleKind.LifeSupport, DryMass = 200, Activatable = true, WaterRegen = 0.8, EcDraw = 2.0, SlotCost = 2, Tint = new Color(80, 180, 210) },
            // advanced science
            new ModuleDef { Name = "Deep Space Lab",     Id = "deep-space-lab",     Description = "Full pressurized laboratory for long-duration science missions. High-value data returns.", Kind = ModuleKind.Science,      DryMass = 250, Activatable = true, EcDraw = 1.0, SlotCost = 2, Tint = new Color(110, 190, 225) },
            new ModuleDef { Name = "Radiation Detector", Id = "radiation-detector", Description = "Measures cosmic rays and solar particle flux. Essential data for crewed deep-space missions.", Kind = ModuleKind.Science,      DryMass = 50,  Activatable = true, EcDraw = 0.3, Tint = new Color(140, 205, 220) },
            new ModuleDef { Name = "Sample Return Capsule", Id = "sample-return-capsule", Description = "Armored container for returning pristine surface samples to Kerbin. Survives re-entry.", Kind = ModuleKind.Science,  DryMass = 40,  Activatable = true, EcDraw = 0.1, Tint = new Color(180, 160, 120) },
            // advanced comms
            new ModuleDef { Name = "Signal Booster",     Id = "signal-booster",     Description = "Amplifies and repeats weak signals across vast distances. The backbone of the outer-planet relay network.", Kind = ModuleKind.Antenna,  DryMass = 200, Activatable = true, Range = 5e13, Relay = true, EcDraw = 2.0, SlotCost = 2, Tint = new Color(210, 210, 240) },
            // advanced propulsion support
            new ModuleDef { Name = "Cryo Tank",          Id = "cryo-tank",          Description = "Insulated cryogenic tank for long-term monopropellant storage. Minimizes boil-off.", Kind = ModuleKind.Tank,     DryMass = 60,  FuelCapacity = 1200, Tint = new Color(200, 200, 220) },
            new ModuleDef { Name = "Large RCS Block",    Id = "large-rcs-block",    Description = "Heavy RCS thruster block for station-keeping and docking of large vessels.", Kind = ModuleKind.RCS,      DryMass = 120, Activatable = false, EcDraw = 0.8, Tint = new Color(160, 165, 180) },
            // advanced resource
            new ModuleDef { Name = "Advanced Harvester", Id = "advanced-harvester", Description = "Deep-core mining drill with improved yield. Extracts fuel from even the poorest ore deposits.", Kind = ModuleKind.Harvester, DryMass = 500, Activatable = true, FuelProduce = 6.0, EcDraw = 8.0, SlotCost = 2, Tint = new Color(185, 160, 145) },
            // lights
            new ModuleDef { Name = "Large Light Array",  Id = "large-light-array",  Description = "High-power illumination array for night landings and surface operations.", Kind = ModuleKind.Light,    DryMass = 60,  Activatable = true, EcDraw = 0.4, Tint = new Color(255, 250, 220) },
            // storage pods
            new ModuleDef { Name = "EC Storage Pod",     Id = "ec-storage-pod",     Description = "Dedicated electric charge storage module. More efficient than batteries for raw capacity.", Kind = ModuleKind.Storage,  DryMass = 80,  EcCapacity = 4000, Tint = new Color(225, 220, 140) },
            new ModuleDef { Name = "LS Storage Pod",     Id = "ls-storage-pod",     Description = "Compact life-support consumables storage. Packs water, oxygen, and food for long hauls.", Kind = ModuleKind.Storage,  DryMass = 120, WaterCapacity = 60000, OxygenCapacity = 108000, FoodCapacity = 40000, Tint = new Color(140, 220, 160) },
            new ModuleDef { Name = "Fuel Storage Pod",   Id = "fuel-storage-pod",   Description = "Auxiliary fuel storage module. Bolsters monopropellant reserves without a full tank part.", Kind = ModuleKind.Storage,  DryMass = 40,  FuelCapacity = 800, Tint = new Color(220, 190, 100) },
        };

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string FilePath => Path.Combine(System.AppContext.BaseDirectory, "Content", "modules.json");

        /// <summary>Load the catalog from Content/modules.json next to the exe. Falls back to
        /// built-in list and rewrites a template file when the file is missing or invalid.</summary>
        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var dtos = JsonSerializer.Deserialize<List<ModuleDefDto>>(File.ReadAllText(FilePath), JsonOpts);
                    if (dtos != null && dtos.Count > 0)
                    {
                        var list = new List<ModuleDef>(dtos.Count);
                        foreach (var d in dtos) list.Add(d.ToModule());
                        // upgrade older files in place: backfill a missing Id and merge in newer built-in
                        // modules the on-disk file predates (see PartCatalog.Load). Re-save when changed.
                        bool changed = false;
                        foreach (var d in dtos) if (string.IsNullOrEmpty(d.Id)) changed = true;
                        foreach (var b in BuiltIn())
                            if (list.Find(m => m.Name == b.Name) == null) { list.Add(b); changed = true; }
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
                var dtos = new List<ModuleDefDto>(All.Count);
                foreach (var m in All) dtos.Add(ModuleDefDto.FromModule(m));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(dtos, JsonOpts));
            }
            catch { /* read-only install dir: keep running with in-memory catalog */ }
        }
    }

    /// <summary>JSON-friendly mirror of <see cref="ModuleDef"/> (MonoGame's Color isn't serializable).</summary>
    public sealed class ModuleDefDto
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string Description { get; set; }
        public ModuleKind Kind { get; set; }
        public double DryMass { get; set; }
        public int SlotCost { get; set; } = 1;
        public bool Activatable { get; set; }
        public double EcProduce { get; set; }
        public double EcCapacity { get; set; }
        public double EcDraw { get; set; }
        public double WaterCapacity { get; set; }
        public double OxygenCapacity { get; set; }
        public double FoodCapacity { get; set; }
        public double WaterRegen { get; set; }
        public double OxygenRegen { get; set; }
        public double FoodRegen { get; set; }
        public int CrewCapacity { get; set; }
        public double Range { get; set; }
        public bool Relay { get; set; }
        public double FuelProduce { get; set; }
        public double FuelDraw { get; set; }
        public double FuelCapacity { get; set; }
        public int TintR { get; set; }
        public int TintG { get; set; }
        public int TintB { get; set; }

        public static ModuleDefDto FromModule(ModuleDef m) => new()
        {
            Name = m.Name, Id = m.Id, Description = m.Description, Kind = m.Kind, DryMass = m.DryMass,
            SlotCost = m.SlotCost, Activatable = m.Activatable,
            EcProduce = m.EcProduce, EcCapacity = m.EcCapacity, EcDraw = m.EcDraw,
            WaterCapacity = m.WaterCapacity, OxygenCapacity = m.OxygenCapacity, FoodCapacity = m.FoodCapacity,
            WaterRegen = m.WaterRegen, OxygenRegen = m.OxygenRegen, FoodRegen = m.FoodRegen,
            CrewCapacity = m.CrewCapacity, Range = m.Range, Relay = m.Relay,
            FuelProduce = m.FuelProduce, FuelDraw = m.FuelDraw,
            FuelCapacity = m.FuelCapacity,
            TintR = m.Tint.R, TintG = m.Tint.G, TintB = m.Tint.B,
        };

        public ModuleDef ToModule() => new()
        {
            Name = Name, Id = string.IsNullOrEmpty(Id) ? PartDef.Slug(Name) : Id,
            Description = Description, Kind = Kind, DryMass = DryMass,
            SlotCost = SlotCost > 0 ? SlotCost : 1, Activatable = Activatable,
            EcProduce = EcProduce, EcCapacity = EcCapacity, EcDraw = EcDraw,
            WaterCapacity = WaterCapacity, OxygenCapacity = OxygenCapacity, FoodCapacity = FoodCapacity,
            WaterRegen = WaterRegen, OxygenRegen = OxygenRegen, FoodRegen = FoodRegen,
            CrewCapacity = CrewCapacity, Range = Range, Relay = Relay,
            FuelProduce = FuelProduce, FuelDraw = FuelDraw,
            FuelCapacity = FuelCapacity,
            Tint = new Color(TintR, TintG, TintB),
        };
    }
}
