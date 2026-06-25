using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;

namespace Solar.Parts
{
    /// <summary>The slot-module catalog. The authoritative source is Content/modules.json, loaded by
    /// <see cref="Load"/> at startup; empty until then (there is no in-code fallback catalog).</summary>
    public static class ModuleCatalog
    {
        public static List<ModuleDef> All = new();

        public static ModuleDef Get(string name) => All.Find(m => m.Name == name);

        /// <summary>Look up a module by its stable kebab-case id (the key used by part DefaultModules).</summary>
        public static ModuleDef GetById(string id) => All.Find(m => m.Id == id);

        // NOTE: the module catalog lives entirely in Content/modules.json (the authoritative, moddable source).
        // There is no in-code fallback list — Load() populates All from JSON, leaving it empty if absent.
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string FilePath => Path.Combine(System.AppContext.BaseDirectory, "Content", "modules.json");

        /// <summary>Load the catalog from Content/modules.json (the sole source of truth). The DTO
        /// <see cref="ModuleDefDto.ToModule"/> applies migration-safe fallbacks for fields a hand-edited
        /// file may omit. If the file is missing or invalid the catalog is left empty (by design —
        /// there is no in-code fallback list); the build ships modules.json via CopyToOutputDirectory.</summary>
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
                        All = list;
                        return;
                    }
                }
            }
            catch { /* unreadable/corrupt modules.json: leave the catalog empty */ }

            All = new List<ModuleDef>();
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
        public double ScienceValue { get; set; }
        public double Torque { get; set; }
        public double RcsThrust { get; set; }
        public double RcsIsp { get; set; }
        public double Reliability { get; set; }
        public double ShieldFactor { get; set; }
        public bool LocalShield { get; set; }
        public double CureRate { get; set; }
        public double StormHardening { get; set; }
        public double RepairSkill { get; set; }
        public double ScanRange { get; set; }
        public double ScanRate { get; set; }
        public double FuelProduce { get; set; }
        public double FuelDraw { get; set; }
        public double FuelCapacity { get; set; }
        public double OreProduce { get; set; }
        public double OreDraw { get; set; }
        public double OreCapacity { get; set; }
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
            ScienceValue = m.ScienceValue, Torque = m.Torque, RcsThrust = m.RcsThrust, RcsIsp = m.RcsIsp,
            Reliability = m.Reliability, ShieldFactor = m.ShieldFactor, LocalShield = m.LocalShield,
            CureRate = m.CureRate, StormHardening = m.StormHardening, RepairSkill = m.RepairSkill,
            ScanRange = m.ScanRange, ScanRate = m.ScanRate,
            FuelProduce = m.FuelProduce, FuelDraw = m.FuelDraw,
            FuelCapacity = m.FuelCapacity,
            OreProduce = m.OreProduce, OreDraw = m.OreDraw, OreCapacity = m.OreCapacity,
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
            // migration-safe fallbacks so an entry that omits these still behaves like the old hardcoded values:
            ScienceValue = ScienceValue > 0 ? ScienceValue : (Kind == ModuleKind.Science ? 10 : 0),
            Torque = Torque > 0 ? Torque : (Kind == ModuleKind.ReactionWheel ? 45000 : Kind == ModuleKind.RCS ? 8000 : 0),
            RcsThrust = RcsThrust > 0 ? RcsThrust : (Kind == ModuleKind.RCS ? 1000 : 0),
            RcsIsp = RcsIsp > 0 ? RcsIsp : (Kind == ModuleKind.RCS ? 240 : 0),
            Reliability = Reliability > 0 ? Reliability : DefaultReliability(Kind),
            ShieldFactor = ShieldFactor,   // 0 unless authored (only meaningful for RadShield)
            LocalShield = LocalShield,     // false unless authored (only meaningful for RadShield)
            CureRate = CureRate > 0 ? CureRate : (Kind == ModuleKind.Medbay ? 0.02 : 0),
            StormHardening = StormHardening > 0 ? StormHardening : (Kind == ModuleKind.Radiator ? 0.6 : 0),
            RepairSkill = RepairSkill > 0 ? RepairSkill : (Kind == ModuleKind.MaintenanceDrone ? 0.4 : 0),
            // migration-safe fallbacks so a telescope entry that omits these still works:
            ScanRange = ScanRange > 0 ? ScanRange : (Kind == ModuleKind.Telescope ? 5e10 : 0),
            ScanRate = ScanRate > 0 ? ScanRate : (Kind == ModuleKind.Telescope ? 0.0008 : 0),
            FuelProduce = FuelProduce, FuelDraw = FuelDraw,
            FuelCapacity = FuelCapacity,
            // migration-safe fallback: a drill authored before ore existed inherits its old FuelProduce
            // as ore extraction, so it now mines ore (the ISRU converter refines it back into fuel).
            OreProduce = OreProduce > 0 ? OreProduce : (Kind == ModuleKind.Harvester ? FuelProduce : 0),
            OreDraw = OreDraw, OreCapacity = OreCapacity,
            Tint = new Color(TintR, TintG, TintB),
        };

        /// <summary>Per-kind failure-resistance fallback for modules that don't author <see cref="Reliability"/>.
        /// Hard-working converters / generators wear and fail sooner; passive structure rarely does.</summary>
        private static double DefaultReliability(ModuleKind k) => k switch
        {
            ModuleKind.Harvester or ModuleKind.IsruConverter or ModuleKind.FuelCell => 0.7,
            ModuleKind.SolarPanel or ModuleKind.ReactionWheel or ModuleKind.RCS or ModuleKind.MaintenanceDrone => 0.85,
            ModuleKind.Battery or ModuleKind.Tank or ModuleKind.Storage or ModuleKind.LandingLeg => 4.0,
            _ => 1.0,
        };
    }
}
