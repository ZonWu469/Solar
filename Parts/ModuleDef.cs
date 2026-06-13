using Microsoft.Xna.Framework;

namespace Solar.Parts
{
    public enum ModuleKind { SolarPanel, Rtg, Battery, LifeSupport, Harvester, ReactionWheel, Science, Antenna, FuelCell, LandingLeg, Light, RCS, Tank, Storage }

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
        public double FuelProduce; // fuel kg/s produced when landed + active (harvester)
        public double FuelDraw;    // fuel kg/s consumed when functioning (fuel cell)
        public double FuelCapacity;// extra fuel storage (monopropellant tank module)
        public double Range;       // antenna full-strength reach in metres (0 = not an antenna)
        public bool Relay;         // antenna rebroadcasts: it can relay signal to other vessels
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
            ModuleKind.Harvester => $"+{FuelProduce:0.#} fuel/s — mining drill, {DryMass:0} kg",
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
                                  : $"storage pod, {DryMass:0} kg",
            _ => $"{DryMass:0} kg",
        };
    }
}
