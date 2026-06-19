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

        /// <summary>The live catalog. The authoritative source is Content/parts.json, loaded by
        /// <see cref="Load"/> at startup; empty until then (there is no in-code fallback catalog).</summary>
        public static List<PartDef> All = new();

        public static PartDef Get(string name) => All.Find(p => p.Name == name);

        /// <summary>Look up a part by its stable kebab-case id (the key used by the tech tree and textures).</summary>
        public static PartDef GetById(string id) => All.Find(p => p.Id == id);

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

        // NOTE: the part catalog lives entirely in Content/parts.json (the authoritative, moddable source).
        // There is no in-code fallback list — Load() populates All from JSON, leaving it empty if absent.
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "Content", "parts.json");

        /// <summary>Load the catalog from Content/parts.json (the sole source of truth). The DTO
        /// <see cref="PartDefDto.ToPart"/> applies migration-safe fallbacks for fields a hand-edited
        /// file may omit. If the file is missing or invalid the catalog is left empty (by design —
        /// there is no in-code fallback list); the build ships parts.json via CopyToOutputDirectory.</summary>
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
                        All = list;
                        return;
                    }
                }
            }
            catch { /* unreadable/corrupt parts.json: leave the catalog empty */ }

            All = new List<PartDef>();
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
        // optional exhaust appearance (all zero = omitted -> legacy orange fallback in ToPart)
        public int ExhaustR { get; set; }
        public int ExhaustG { get; set; }
        public int ExhaustB { get; set; }
        public int ExhaustCoreR { get; set; }
        public int ExhaustCoreG { get; set; }
        public int ExhaustCoreB { get; set; }
        public float ExhaustLengthScale { get; set; }
        public float ExhaustWidthScale { get; set; }
        public List<string> DefaultModules { get; set; }   // module ids pre-fitted in this part's slots (optional)

        public static PartDefDto FromPart(PartDef p) => new()
        {
            Name = p.Name, Id = p.Id, Kind = p.Kind, DryMass = p.DryMass, FuelCapacity = p.FuelCapacity,
            Thrust = p.Thrust, Isp = p.Isp, Width = p.Width, Height = p.Height, CdA = p.CdA,
            DeployedCdA = p.DeployedCdA, ControlAuthority = p.ControlAuthority, Sas = p.Sas, ImpactTolerance = p.ImpactTolerance,
            Slots = p.Slots,
            TintR = p.Tint.R, TintG = p.Tint.G, TintB = p.Tint.B,
            ExhaustR = p.ExhaustColor.R, ExhaustG = p.ExhaustColor.G, ExhaustB = p.ExhaustColor.B,
            ExhaustCoreR = p.ExhaustCoreColor.R, ExhaustCoreG = p.ExhaustCoreColor.G, ExhaustCoreB = p.ExhaustCoreColor.B,
            ExhaustLengthScale = p.ExhaustLengthScale, ExhaustWidthScale = p.ExhaustWidthScale,
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
            // exhaust appearance: fall back to the legacy orange plume when JSON omits the fields (all zero)
            ExhaustColor = (ExhaustR | ExhaustG | ExhaustB) != 0 ? new Color(ExhaustR, ExhaustG, ExhaustB, 200) : new Color(255, 140, 40, 200),
            ExhaustCoreColor = (ExhaustCoreR | ExhaustCoreG | ExhaustCoreB) != 0 ? new Color(ExhaustCoreR, ExhaustCoreG, ExhaustCoreB, 230) : new Color(255, 230, 120, 230),
            ExhaustLengthScale = ExhaustLengthScale > 0 ? ExhaustLengthScale : 1f,
            ExhaustWidthScale = ExhaustWidthScale > 0 ? ExhaustWidthScale : 1f,
            DefaultModules = DefaultModules ?? new List<string>(),
        };

        // by-size defaults used only when an entry omits the field (older/edited JSON)
        private static double DefaultPodAuthority(double dryMass) =>
            dryMass < 500 ? 0.10 : dryMass < 2000 ? 0.14 : dryMass < 4000 ? 0.20 : 0.28;
        private static double DefaultGearTolerance(double dryMass) => dryMass < 500 ? 14.0 : 24.0;
    }
}
