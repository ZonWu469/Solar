using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Solar.Parts
{
    public enum PartKind { Pod, Tank, Engine, Decoupler, Fins, Parachute, SolidBooster, Aero, RadialDecoupler, DockingPort, LandingGear, StructuralBay }

    /// <summary>Immutable part definition (the catalog entry).</summary>
    public sealed class PartDef
    {
        public string Name;
        public string Id;           // stable kebab-case slug, used to locate a texture asset
        public PartKind Kind;
        public double DryMass;       // kg
        public double FuelCapacity;  // kg
        public double Thrust;        // N
        public double Isp;           // s
        public double Width;         // m
        public double Height;        // m
        public double CdA;           // drag coefficient * reference area, m^2
        public double DeployedCdA;   // extra CdA a deployed parachute adds (0 = not a chute / use legacy default)
        public double ControlAuthority; // command-part minimum steering authority, rad/s^2 (0 = no control)
        public bool Sas;                // provides attitude-hold / SAS (a command part the player can engage)
        public double ImpactTolerance;  // m/s added to the safe landing speed (landing gear)
        public Color Tint;

        // Engine/booster exhaust appearance. All optional in parts.json; entries that omit them
        // fall back (in PartDefDto.ToPart) to the legacy orange plume so untagged engines are unchanged.
        public Color ExhaustColor;       // outer flame color
        public Color ExhaustCoreColor;   // inner (hot core) flame color
        public float ExhaustLengthScale; // multiplies flame length (1 = legacy)
        public float ExhaustWidthScale;  // multiplies flame width  (1 = legacy)

        /// <summary>Module ids this part ships with pre-installed in its slots (an "advanced" part,
        /// e.g. a service pod that already carries a solar panel + battery). Seeded into a fresh
        /// <see cref="Vessel.StackEntry"/> when the part is placed in the editor; the player can still
        /// remove or swap them. Empty for ordinary parts.</summary>
        public List<string> DefaultModules = new();

        public double FuelFlowAtMax => Thrust / (Isp * 9.81); // kg/s

        /// <summary>Whether this part mounts to the side of a stack part (as a symmetric pair) rather
        /// than stacking inline. Drives the editor's radial-attach interaction.</summary>
        public bool Radial => Kind == PartKind.RadialDecoupler || Kind == PartKind.LandingGear
                              || (Id != null && (Id.EndsWith("-r") || Id.StartsWith("radial-")));

        /// <summary>Number of module slots this part exposes (set per-part in parts.json). Pods and
        /// tanks carry equipment; structural parts don't. Entries that omit it fall back to
        /// <see cref="DefaultSlots"/>.</summary>
        public int Slots;

        /// <summary>Per-kind slot defaults, used to backfill entries whose JSON omits <see cref="Slots"/>.</summary>
        public static int DefaultSlots(PartKind kind) => kind switch
        {
            PartKind.Pod => 3,
            PartKind.Tank => 2,
            PartKind.DockingPort => 1,
            PartKind.StructuralBay => 2,
            _ => 0,
        };

        /// <summary>Built-in crew seats from the part itself (a command pod seats one). Crew cabins add
        /// more seats via their module's <see cref="ModuleDef.CrewCapacity"/>. Derived from kind so it
        /// needs no catalog/JSON migration (like <see cref="Slots"/>).</summary>
        public int BaseCrew => Kind == PartKind.Pod ? 1 : 0;

        public string StatLine => Kind switch
        {
            PartKind.Engine => $"{Thrust / 1000:0} kN  Isp {Isp:0}s  {DryMass:0} kg",
            PartKind.SolidBooster => $"{Thrust / 1000:0} kN SRB  {FuelCapacity:0} kg fuel",
            PartKind.Tank => $"{FuelCapacity:0} kg fuel  {DryMass:0} kg dry",
            PartKind.Aero => $"nose cone (low drag)  {DryMass:0} kg",
            PartKind.DockingPort => $"docking port  {DryMass:0} kg",
            PartKind.LandingGear => $"landing gear  {DryMass:0} kg",
            PartKind.StructuralBay => $"structural bay  {Slots} slots  {DryMass:0} kg",
            _ => $"{DryMass:0} kg",
        };

        /// <summary>Lowercase kebab-case slug of a display name (used as a texture-asset id fallback,
        /// so a def loaded from older JSON without an explicit Id still resolves textures).</summary>
        public static string Slug(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "part";
            var sb = new System.Text.StringBuilder(name.Length);
            bool dash = false;
            foreach (char ch in name.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch)) { sb.Append(ch); dash = false; }
                else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
            }
            string s = sb.ToString().Trim('-');
            return s.Length == 0 ? "part" : s;
        }
    }
}
