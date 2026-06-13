using System.Collections.Generic;
using Solar.Vessels;

namespace Solar.Parts
{
    /// <summary>A slot module mounted on a part at runtime. <see cref="Active"/> covers both
    /// "deployed" (solar panels) and "running" (drills) for activatable modules.</summary>
    public sealed class ModuleInstance
    {
        public readonly ModuleDef Def;
        public bool Active;
        public ModuleInstance(ModuleDef def) { Def = def; }
    }

    /// <summary>Runtime instance of a part on a flying vessel.</summary>
    public sealed class Part
    {
        public readonly PartDef Def;
        public double Fuel;       // kg remaining
        public bool Ignited;      // engines
        public bool Deployed;     // parachutes
        /// <summary>Activation stage index (0 fires first). For an axial part it is when its action fires:
        /// an engine/booster ignites, a decoupler separates, a parachute deploys. For a radial part it is
        /// the stage at which the (separate) mount is jettisoned; a radial's engines ignite / chutes deploy
        /// at their own <see cref="FireStage"/> instead. Seeded from geometry by
        /// <see cref="Vessels.Staging.AssignDefaultStages"/>, then editable in the VAB. Drives both the
        /// staging order and the stage list. -1 = not yet assigned (filled from geometry by
        /// AssignDefaultStages before flight/stage computation).</summary>
        public int Stage = -1;
        /// <summary>Radial-only: the stage at which this radial part activates — an engine/booster ignites
        /// or a parachute deploys — independent of its host axial part and of its (separate) drop
        /// <see cref="Stage"/>. -1 = derive from geometry (engine fires with host; chute deploys last).
        /// Ignored for axial parts, which activate at <see cref="Stage"/>.</summary>
        public int FireStage = -1;
        public readonly List<ModuleInstance> Modules = new();
        /// <summary>Crew members aboard this part (never more than <see cref="SeatCount"/>).</summary>
        public readonly List<CrewMember> Crew = new();
        /// <summary>Parts radially attached to this one as a symmetric pair (each side is its own
        /// <see cref="Part"/> so fuel/ignition track independently). They ride this part's stage.</summary>
        public readonly List<Part> Radials = new();
        /// <summary>When this part is itself a radial attachment: true = it jettisons as its own stage;
        /// false = it stays welded to its host part and only leaves when that part is decoupled.</summary>
        public bool RadialSeparate = true;
        /// <summary>Design round-trip tags (ignored by physics): which radial mount this part belongs to
        /// on its host, its side (0 = left, 1 = right), and its slot in the mount's vertical sub-stack
        /// (0 = against the host). Let <see cref="VesselDesign.FromVessel"/> regroup the flat
        /// <see cref="Radials"/> list back into design mounts. -1 means "not a materialized radial".</summary>
        public int RadialMountId = -1, RadialSide = -1, RadialSlot = -1;

        public Part(PartDef def)
        {
            Def = def;
            Fuel = def.FuelCapacity;
        }

        /// <summary>How many crew this part can hold: the pod's built-in seat plus any crew-cabin
        /// modules fitted to its slots.</summary>
        public int SeatCount
        {
            get { int n = Def.BaseCrew; foreach (var m in Modules) n += m.Def.CrewCapacity; return n; }
        }

        public double Mass
        {
            get
            {
                double m = Def.DryMass + Fuel;
                foreach (var mod in Modules) m += mod.Def.DryMass;
                return m;
            }
        }
    }
}
