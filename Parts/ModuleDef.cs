using Microsoft.Xna.Framework;

namespace Solar.Parts
{
    public enum ModuleKind { SolarPanel, Rtg, Battery, LifeSupport, Harvester, ReactionWheel, Science, Antenna, FuelCell, LandingLeg, Light, RCS, Tank, Storage, IsruConverter, OreScanner, RadShield, Medbay, Radiator }

    /// <summary>Immutable definition of a slot module attached to a part (power, life support, mining).</summary>
    public sealed class ModuleDef
    {
        public string Name;
        public string Id;          // stable kebab-case slug, used to locate a texture asset
        public string Description; // one-line flavor / function hint shown in tooltips
        public ModuleKind Kind;
        public double DryMass;     // kg
        public int SlotCost = 1;   // how many module slots this consumes (default 1; heavy modules cost more)
        public bool Activatable;   // must be toggled on / deployed before it functions
        public double EcProduce;   // electric charge / s produced when functioning
        public double EcCapacity;  // electric charge storage added
        public double EcDraw;      // electric charge / s consumed when functioning
        // life support is three independent resources (per-crew consumption lives on the Vessel)
        public double WaterCapacity;   // water storage added
        public double OxygenCapacity;  // oxygen storage added
        public double FoodCapacity;    // food storage added
        public double WaterRegen;      // water / s regenerated when active (recycler), gated on EC
        public double OxygenRegen;     // oxygen / s regenerated when active (recycler), gated on EC
        public double FoodRegen;       // food / s regenerated when active (recycler), gated on EC
        public int CrewCapacity;       // crew seats this module adds to its host part
        public double FuelProduce; // fuel kg/s produced when landed + active (harvester); fuel OUTPUT for an ISRU converter
        public double FuelDraw;    // fuel kg/s consumed when functioning (fuel cell)
        public double FuelCapacity;// extra fuel storage (monopropellant tank module)
        public double OreProduce;  // ore kg/s mined when landed + active, before body richness (drill)
        public double OreDraw;     // ore kg/s consumed when active (ISRU converter input)
        public double OreCapacity; // ore storage added (ore tank, or a drill/ISRU's own buffer)
        public double Range;       // antenna full-strength reach in metres (0 = not an antenna)
        public bool Relay;         // antenna rebroadcasts: it can relay signal to other vessels
        public double ScienceValue;// base data worth for a Science-kind instrument (0 = use kind fallback)
        public double Torque;      // rotational authority N*m contributed when powered (reaction wheel / RCS block)
        public double RcsThrust;   // translation thrust N per RCS block (0 = use default)
        public double RcsIsp;      // monopropellant specific impulse s for an RCS block (0 = use default)
        public double Reliability; // failure-resistance multiplier (higher = breaks less often; 0 = kind default)
        public double ShieldFactor;// RadShield: fraction of incoming radiation blocked, 0..1 (storm dose only when its face is sunward)
        public double CureRate;    // Medbay: crew illness cured per second when active + powered
        public double StormHardening;// fraction (0..1) of solar-storm electronics-fry risk removed while powered (radiator / hardened bay)
        public Color Tint;

        private string RangeText => Range >= 1e9 ? $"{Range / 1e9:0.#} Gm"
                                  : Range >= 1e6 ? $"{Range / 1e6:0} Mm" : $"{Range / 1e3:0} km";

        public string StatLine => Kind switch
        {
            ModuleKind.SolarPanel => $"+{EcProduce:0.#} EC/s — deployable solar panel, {DryMass:0} kg",
            ModuleKind.Rtg => $"+{EcProduce:0.#} EC/s — passive radioisotope generator, {DryMass:0} kg",
            ModuleKind.Battery => $"{EcCapacity:0} EC storage, {DryMass:0} kg",
            ModuleKind.LifeSupport => CrewCapacity > 0 ? $"life support  +{CrewCapacity} crew  {DryMass:0} kg"
                                      : (OxygenRegen > 0 || FoodRegen > 0 || WaterRegen > 0) ? $"life-support recycler  {DryMass:0} kg"
                                      : $"life support  {DryMass:0} kg",
            ModuleKind.Harvester => $"+{OreProduce:0.#} ore/s — mining drill, {DryMass:0} kg",
            ModuleKind.IsruConverter => $"{OreDraw:0.#} ore/s -> {FuelProduce:0.#} fuel/s — ISRU converter, {DryMass:0} kg",
            ModuleKind.OreScanner => $"orbital ore survey, {DryMass:0} kg",
            ModuleKind.RadShield => $"-{ShieldFactor * 100:0}% storm dose (face the Sun) — shielding, {DryMass:0} kg",
            ModuleKind.Radiator => $"-{StormHardening * 100:0}% storm fry risk — radiator / hardening, {DryMass:0} kg",
            ModuleKind.Medbay => $"treats crew illness, {DryMass:0} kg",
            ModuleKind.ReactionWheel => $"attitude control, {DryMass:0} kg",
            ModuleKind.Science => $"science instrument, {DryMass:0} kg",
            ModuleKind.Antenna => $"antenna, {RangeText}{(Relay ? " relay" : "")}, {DryMass:0} kg",
            ModuleKind.FuelCell => $"+{EcProduce:0.#} EC/s  -{FuelDraw:0.#} fuel/s — fuel cell, {DryMass:0} kg",
            ModuleKind.LandingLeg => $"landing leg, {DryMass:0} kg",
            ModuleKind.Light => $"-{EcDraw:0.##} EC/s — light, {DryMass:0} kg",
            ModuleKind.RCS => $"RCS thruster, {DryMass:0} kg",
            ModuleKind.Tank => $"extra fuel tank, {FuelCapacity:0} kg",
            ModuleKind.Storage => EcCapacity > 0 ? $"EC storage pod, +{EcCapacity:0} EC, {DryMass:0} kg"
                                  : FuelCapacity > 0 ? $"fuel storage pod, +{FuelCapacity:0} kg fuel, {DryMass:0} kg"
                                  : OreCapacity > 0 ? $"ore storage pod, +{OreCapacity:0} kg ore, {DryMass:0} kg"
                                  : $"storage pod, {DryMass:0} kg",
            _ => $"{DryMass:0} kg",
        };
    }
}
