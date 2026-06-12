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
        /// <summary>Names of crew assigned to this part's seats in the editor (resolved against the
        /// savegame roster at launch).</summary>
        public List<string> CrewNames = new();
        public StackEntry() { }
        public StackEntry(PartDef def) { Def = def; }

        /// <summary>Mount a radial part as a new symmetric pair. When <paramref name="separate"/> is left
        /// unset, the staging choice defaults by part type: a booster/engine drops as its own stage (STG),
        /// while a fuel tank or structural part rides the core (KEEP) so it actually feeds the rocket.</summary>
        public void AddRadial(PartDef def, bool? separate = null)
            => Mounts.Add(new RadialMount(def, separate ?? (def != null && (def.Kind == PartKind.Engine || def.Kind == PartKind.SolidBooster))));

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
                for (int side = 0; side <= 1; side++)
                    for (int slot = 0; slot < mount.Parts.Count; slot++)
                        host.Radials.Add(new Part(mount.Parts[slot])
                        {
                            RadialSeparate = mount.Separate,
                            RadialMountId = mi, RadialSide = side, RadialSlot = slot,
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
                var e = new StackEntry(p.Def);
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
                    int side0 = int.MaxValue;
                    foreach (var r in group) if (r.RadialSide >= 0 && r.RadialSide < side0) side0 = r.RadialSide;
                    group.Sort((a, b) => a.RadialSlot.CompareTo(b.RadialSlot));
                    var mount = new RadialMount { Separate = group[0].RadialSeparate };
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
                var p = new Part(e.Def);
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
            return v;
        }
    }
}
