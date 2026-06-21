using System;
using System.Collections.Generic;
using Solar.Parts;

namespace Solar.Vessels
{
    /// <summary>A radially-mounted attachment in the editor design: a small vertical sub-stack of parts
    /// (index 0 against the host, increasing downward toward the engines) plus a single staging choice for
    /// the whole mount. Materialized as a symmetric pair (one runtime side-stack per side).</summary>
    public sealed class RadialMount
    {
        public List<PartDef> Parts = new();
        /// <summary>true = the mount jettisons as its own stage (a spent booster you drop); false = it stays
        /// attached and rides its host part's stage ("included in another stage").</summary>
        public bool Separate = true;
        /// <summary>Stage at which this (separate) mount is jettisoned; -1 = derive from geometry. Ignored
        /// when <see cref="Separate"/> is false (the mount then rides its host and drops with it).</summary>
        public int Stage = -1;
        /// <summary>Stage at which this mount's engines ignite (or its parachute deploys), independent of
        /// the host axial part and of the drop <see cref="Stage"/>. -1 = derive from geometry (fires with
        /// the host; a radial chute deploys last).</summary>
        public int FireStage = -1;
        /// <summary>Which side this mount occupies: -1 = mirrored symmetric pair (the default for boosters,
        /// decouplers, tanks, gear), 0 = right side only, 1 = left side only. Lateral thrusters mount on a
        /// single chosen side; <see cref="MaterializeRadials"/> emits one runtime part instead of a pair.</summary>
        public int Side = -1;
        public PartDef Root => Parts.Count > 0 ? Parts[0] : null;
        public RadialMount() { }
        public RadialMount(PartDef root, bool separate = true) { if (root != null) Parts.Add(root); Separate = separate; }
    }

    /// <summary>One slot in the editor design: a part, the modules in its slots, and any radial mounts
    /// attached to it. Each entry in <see cref="Mounts"/> is a symmetric pair (materialized as two runtime
    /// side-stacks, one per side).</summary>
    public sealed class StackEntry
    {
        public PartDef Def;
        public List<ModuleDef> Modules = new();
        public List<RadialMount> Mounts = new();
        /// <summary>Activation stage for this axial part (0 fires first); -1 = derive from geometry on
        /// launch. Mirrors the runtime <see cref="Solar.Parts.Part.Stage"/>; persisted with the design.</summary>
        public int Stage = -1;
        /// <summary>Names of crew assigned to this part's seats in the editor (resolved against the
        /// savegame roster at launch).</summary>
        public List<string> CrewNames = new();
        /// <summary>User-chosen propellant load for this part in the editor, in kg. null = fill to
        /// capacity (the default, so existing designs/old saves load full). See <see cref="CurrentFuel"/>.</summary>
        public double? FuelOverride;
        public StackEntry() { }
        public StackEntry(PartDef def) { Def = def; }

        /// <summary>The propellant this entry launches with, in kg: the user's <see cref="FuelOverride"/>
        /// clamped to the part's capacity, or full capacity when unset.</summary>
        public double CurrentFuel => Def == null ? 0
            : (FuelOverride.HasValue ? Math.Clamp(FuelOverride.Value, 0, Def.FuelCapacity) : Def.FuelCapacity);

        /// <summary>Mount a radial part as a new symmetric pair. When <paramref name="separate"/> is left
        /// unset, the staging choice defaults by part type: a booster/engine or a radial decoupler drops as
        /// its own stage (STG), while a fuel tank or structural part rides the core (KEEP) so it actually
        /// feeds the rocket. A lateral thruster is a flight control, so it defaults to KEEP too (welded,
        /// ignites with its host stage, never jettisoned) rather than dropping like a booster.</summary>
        public void AddRadial(PartDef def, bool? separate = null, int side = -1)
            => Mounts.Add(new RadialMount(def, separate ?? (def != null && !def.IsLateralThruster
                                                            && (def.Kind == PartKind.Engine
                                                                          || def.Kind == PartKind.SolidBooster
                                                                          || def.Kind == PartKind.RadialDecoupler)))
               {
                   // lateral thrusters mount single-sided; default to the right side if no side was chosen
                   Side = def != null && def.IsLateralThruster ? (side >= 0 ? side : 0) : -1
               });

        /// <summary>Attach <paramref name="def"/> to the bottom of an existing radial mount's sub-stack
        /// (e.g. an engine below a radial tank).</summary>
        public void AppendToMount(int mountIndex, PartDef def)
        {
            if (mountIndex < 0 || mountIndex >= Mounts.Count || def == null) return;
            Mounts[mountIndex].Parts.Add(def);
        }

        public void RemoveRadial(int i)
        {
            if (i < 0 || i >= Mounts.Count) return;
            Mounts.RemoveAt(i);
        }

        /// <summary>Remove one part from a mount's sub-stack; if the sub-stack empties, drop the mount.</summary>
        public void RemoveFromMount(int mountIndex, int partIndex)
        {
            if (mountIndex < 0 || mountIndex >= Mounts.Count) return;
            var m = Mounts[mountIndex];
            if (partIndex < 0 || partIndex >= m.Parts.Count) return;
            m.Parts.RemoveAt(partIndex);
            if (m.Parts.Count == 0) Mounts.RemoveAt(mountIndex);
        }

        /// <summary>Whether radial mount <paramref name="i"/> jettisons as its own stage. Missing
        /// entries default to true to match the previous always-jettison behavior.</summary>
        public bool IsRadialSeparate(int i) => i < 0 || i >= Mounts.Count || Mounts[i].Separate;

        /// <summary>Seats this entry exposes: pod base + any crew-cabin modules fitted to it.</summary>
        public int SeatCount
        {
            get { int n = Def.BaseCrew; foreach (var m in Modules) n += m.CrewCapacity; return n; }
        }
    }

    /// <summary>The editor's rocket design: an ordered part stack, index 0 = top.</summary>
    public sealed class VesselDesign
    {
        public string Name = "Ship 1";
        public List<StackEntry> Stack = new();

        public bool HasPod
        {
            get { foreach (var e in Stack) if (e.Def.Kind == PartKind.Pod) return true; return false; }
        }

        public string Validate()
        {
            if (Stack.Count == 0) return "Add parts to the rocket";
            if (!HasPod) return "A command pod is required";
            return null;
        }

        /// <summary>Materialize a design entry's radial mounts onto a runtime host part's flat
        /// <see cref="Part.Radials"/> list: each mount becomes two side-stacks (left fully, then right),
        /// tagged so <see cref="FromVessel"/> can regroup them back into design mounts. Shared by
        /// <see cref="Instantiate"/> and the editor's staging preview.</summary>
        public static void MaterializeRadials(StackEntry e, Part host)
        {
            for (int mi = 0; mi < e.Mounts.Count; mi++)
            {
                var mount = e.Mounts[mi];
                // single-sided mount (lateral thruster): one runtime side-stack; otherwise a mirrored pair
                int sideLo = mount.Side >= 0 ? mount.Side : 0;
                int sideHi = mount.Side >= 0 ? mount.Side : 1;
                for (int side = sideLo; side <= sideHi; side++)
                    for (int slot = 0; slot < mount.Parts.Count; slot++)
                        host.Radials.Add(new Part(mount.Parts[slot])
                        {
                            RadialSeparate = mount.Separate,
                            RadialMountId = mi, RadialSide = side, RadialSlot = slot,
                            Stage = mount.Stage,         // drop stage for a separate mount (-1 => derive)
                            FireStage = mount.FireStage, // ignition/deploy stage (-1 => derive)
                        });
            }
        }

        /// <summary>Rebuild an editable design from a live vessel (used by "Cancel mission" to reopen a
        /// pre-launch ship in the editor). Each axial part becomes one stack entry; flat radials are
        /// regrouped into design mounts via their <see cref="Part.RadialMountId"/>/<see cref="Part.RadialSide"/>
        /// tags (taking one side per mount, ordered by slot).</summary>
        public static VesselDesign FromVessel(Vessel v, string name)
        {
            var d = new VesselDesign { Name = string.IsNullOrWhiteSpace(name) ? "Ship 1" : name };
            foreach (var p in v.Parts)
            {
                var e = new StackEntry(p.Def) { Stage = p.Stage };
                if (p.Def.FuelCapacity > 0 && p.Fuel < p.Def.FuelCapacity) e.FuelOverride = p.Fuel;
                foreach (var m in p.Modules) e.Modules.Add(m.Def);

                // Tagged radials (post-change): group by mount id, keep one side, order by slot.
                var byMount = new SortedDictionary<int, List<Part>>();
                var untagged = new List<Part>();
                foreach (var r in p.Radials)
                {
                    if (r.RadialMountId < 0) { untagged.Add(r); continue; }
                    if (!byMount.TryGetValue(r.RadialMountId, out var list)) { list = new List<Part>(); byMount[r.RadialMountId] = list; }
                    list.Add(r);
                }
                foreach (var kv in byMount)
                {
                    var group = kv.Value;
                    int side0 = int.MaxValue; bool side0Seen = false, side1Seen = false;
                    foreach (var r in group)
                    {
                        if (r.RadialSide == 0) side0Seen = true; else if (r.RadialSide == 1) side1Seen = true;
                        if (r.RadialSide >= 0 && r.RadialSide < side0) side0 = r.RadialSide;
                    }
                    group.Sort((a, b) => a.RadialSlot.CompareTo(b.RadialSlot));
                    // present on one side only => single-sided mount (lateral thruster); both => mirrored pair
                    int mountSide = (side0Seen ^ side1Seen) ? (side0Seen ? 0 : 1) : -1;
                    var mount = new RadialMount { Separate = group[0].RadialSeparate, Stage = group[0].Stage, FireStage = group[0].FireStage, Side = mountSide };
                    foreach (var r in group)
                        if (r.RadialSide == side0 || r.RadialSide < 0) mount.Parts.Add(r.Def);
                    if (mount.Parts.Count > 0) e.Mounts.Add(mount);
                }
                // Legacy (untagged) radials predate sub-stacks: stored as consecutive single-part pairs.
                for (int i = 0; i < untagged.Count; i += 2)
                    e.Mounts.Add(new RadialMount(untagged[i].Def, untagged[i].RadialSeparate));
                foreach (var c in p.Crew) e.CrewNames.Add(c.Name);
                d.Stack.Add(e);
            }
            return d;
        }

        public Vessel Instantiate(List<CrewMember> roster = null)
        {
            var v = new Vessel();
            var boarded = new HashSet<CrewMember>();
            foreach (var e in Stack)
            {
                var p = new Part(e.Def) { Stage = e.Stage, Fuel = e.CurrentFuel };
                foreach (var m in e.Modules) p.Modules.Add(new ModuleInstance(m));
                MaterializeRadials(e, p);
                // board assigned crew from the roster, capped at this part's seats (no double-boarding)
                if (roster != null && e.CrewNames != null)
                    foreach (var name in e.CrewNames)
                    {
                        if (p.Crew.Count >= p.SeatCount) break;
                        var c = roster.Find(x => x.Name == name && x.Status == CrewStatus.Active);
                        if (c != null && boarded.Add(c)) p.Crew.Add(c);
                    }
                v.Parts.Add(p);
            }
            v.ElectricCharge = v.EcCapacity;
            v.Monoprop = v.MonopropCapacity;
            v.Water = v.WaterCapacity; v.Oxygen = v.OxygenCapacity; v.Food = v.FoodCapacity;
            Staging.AssignDefaultStages(v.Parts);   // fill any stage tags the design left to geometry
            return v;
        }
    }
}
