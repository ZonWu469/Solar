using System;
using System.Collections.Generic;
using Solar.Parts;
using Solar.Physics;
using Solar.Vessels;

namespace Solar.Core
{
    /// <summary>A whole savegame: universal time, every persistent ship, and the in-progress
    /// editor design. The solar system and part catalog are not stored (they are static/loaded
    /// from parts.json), so only mutable game state lives here.</summary>
    public sealed class GameState
    {
        public string Name = "game";
        public double UT;
        public List<ShipState> Ships = new();
        public DesignState Design = new();

        // ----- progression (science-point economy) -----
        public double Science;                            // points available to spend in R&D
        public List<string> UnlockedTech = new();         // unlocked tech-node ids
        public List<string> CompletedMilestones = new();  // award-once guard
        public List<string> ScienceCollected = new();     // experiment-situation keys already transmitted
        public List<string> SurveyedBodies = new();        // bodies an ore scanner has surveyed (richness revealed)

        // ----- crew -----
        public List<CrewMember> Roster = new();           // all crew (Active + KIA), shared by reference

        // ----- space weather -----
        public long WeatherSeed;                          // seeds the deterministic solar-storm timeline (SpaceWeather)

        /// <summary>Sandbox cheat: a game whose name begins with this prefix starts with the whole tech
        /// tree unlocked, so every part and module is available from the first launch.</summary>
        public const string SandboxPrefix = "Internal_";

        /// <summary>Whether this is a sandbox game (all tech unlocked via the <see cref="SandboxPrefix"/>).</summary>
        public bool IsSandbox => Name != null && Name.StartsWith(SandboxPrefix);

        public static GameState NewGame(string name)
        {
            string trimmed = string.IsNullOrWhiteSpace(name) ? "game" : name.Trim();
            bool sandbox = trimmed.StartsWith(SandboxPrefix);
            return new()
            {
                Name = trimmed,
                UT = 0,
                Design = DesignState.Default(),
                UnlockedTech = sandbox ? Solar.Progression.TechTree.AllNodeIds()
                                       : Solar.Progression.TechTree.StartingNodes(),
                Science = sandbox ? 1_000_000 : 0,
                Roster = CrewRoster.NewPool(),
                WeatherSeed = NewSeed(),
            };
        }

        /// <summary>A fresh, non-zero seed for this save's solar-storm timeline (unique per new game).</summary>
        private static long NewSeed()
        {
            long s = unchecked(DateTime.UtcNow.Ticks ^ ((long)Guid.NewGuid().GetHashCode() << 32));
            return s == 0 ? 1 : s;
        }

        /// <summary>Names of all crew currently aboard a saved/flying ship — the "spent" set. These are
        /// unavailable for assignment in the editor until their ship leaves the fleet (destroyed, scrapped,
        /// or docked away). Walks each ship's part tree, including radially-mounted sub-parts.</summary>
        public HashSet<string> DeployedCrewNames()
        {
            var names = new HashSet<string>();
            foreach (var s in Ships)
                if (s.Parts != null)
                    foreach (var p in s.Parts) CollectCrew(p, names);
            return names;
        }

        private static void CollectCrew(PartState p, HashSet<string> into)
        {
            if (p == null) return;
            if (p.Crew != null) foreach (var n in p.Crew) into.Add(n);
            if (p.Radials != null) foreach (var r in p.Radials) CollectCrew(r, into);
        }

        /// <summary>Insert or replace a ship by name; destroyed ships are dropped.</summary>
        public void UpsertShip(ShipState s)
        {
            Ships.RemoveAll(x => x.Name == s.Name);
            if (!s.Destroyed) Ships.Add(s);
        }

        /// <summary>Drop a ship by name (e.g. once it has docked into another vessel).</summary>
        public void RemoveShip(string name) => Ships.RemoveAll(x => x.Name == name);

        /// <summary>A ship name not already used by a saved ship: returns <paramref name="desired"/>
        /// unchanged when free, otherwise appends " 2", " 3", … so launching a duplicate name keeps
        /// both ships instead of overwriting (see <see cref="UpsertShip"/>).</summary>
        public string UniqueShipName(string desired)
        {
            string baseName = string.IsNullOrWhiteSpace(desired) ? "Ship" : desired.Trim();
            if (Ships.TrueForAll(x => x.Name != baseName)) return baseName;
            for (int n = 2; ; n++)
            {
                string candidate = $"{baseName} {n}";
                if (Ships.TrueForAll(x => x.Name != candidate)) return candidate;
            }
        }
    }

    /// <summary>A design stack entry: a part name, the names of modules fitted to its slots, and the
    /// names of parts mounted radially to it (each = one symmetric pair).</summary>
    /// <summary>A radial mount in a saved design: a vertical sub-stack of part names plus the whole-mount
    /// staging choice. Replaces the legacy single-part <see cref="PartEntryState.Radials"/> arrays.</summary>
    public sealed class RadialMountState
    {
        public List<string> Parts = new();
        public bool Separate = true;
        public int Stage = -1;       // drop stage (-1 = derive from geometry)
        public int FireStage = -1;   // ignition/deploy stage (-1 = derive from geometry)
        public int Side = -1;        // -1 = mirrored pair, 0 = right only, 1 = left only (lateral thrusters)
    }

    public sealed class PartEntryState
    {
        public string Def;
        public int Stage = -1;   // activation stage (-1 = derive from geometry)
        public double? Fuel;     // user-chosen propellant load in kg (null = full capacity)
        public List<string> Modules = new();
        public List<RadialMountState> Mounts;   // current format (sub-stacks)
        public List<string> Radials;            // legacy: parallel single-part list, still read for old saves
        public List<bool> RadialSep;            // legacy: parallel to Radials (true = own stage when absent)
        public List<string> CrewNames;    // crew assigned to this part's seats in the editor
    }

    /// <summary>The editor's current rocket. <see cref="Entries"/> is the current format; the legacy
    /// <see cref="Parts"/> name list is still read so older saves keep loading.</summary>
    public sealed class DesignState
    {
        public string Name = "Ship 1";
        public List<string> Parts = new();        // legacy (names only)
        public List<PartEntryState> Entries;      // current (parts + modules)

        public static DesignState Default()
        {
            var d = new DesignState { Entries = new() };
            foreach (var p in PartCatalog.DefaultDesign()) d.Entries.Add(new PartEntryState { Def = p.Name });
            return d;
        }

        public static DesignState From(VesselDesign vd)
        {
            var d = new DesignState { Name = vd.Name, Entries = new() };
            foreach (var e in vd.Stack)
            {
                var pe = new PartEntryState { Def = e.Def.Name, Stage = e.Stage, Fuel = e.FuelOverride };
                foreach (var m in e.Modules) pe.Modules.Add(m.Name);
                if (e.Mounts.Count > 0)
                {
                    pe.Mounts = new();
                    foreach (var mount in e.Mounts)
                    {
                        var ms = new RadialMountState { Separate = mount.Separate, Stage = mount.Stage, FireStage = mount.FireStage, Side = mount.Side };
                        foreach (var rp in mount.Parts) ms.Parts.Add(rp.Name);
                        pe.Mounts.Add(ms);
                    }
                }
                if (e.CrewNames != null && e.CrewNames.Count > 0) { pe.CrewNames = new(); pe.CrewNames.AddRange(e.CrewNames); }
                d.Entries.Add(pe);
            }
            return d;
        }

        public void ApplyTo(VesselDesign vd)
        {
            vd.Name = string.IsNullOrWhiteSpace(Name) ? "Ship 1" : Name;
            vd.Stack.Clear();
            if (Entries != null)
            {
                foreach (var pe in Entries)
                {
                    var def = PartCatalog.Get(pe.Def);
                    if (def == null) continue;
                    var entry = new StackEntry(def) { Stage = pe.Stage, FuelOverride = pe.Fuel };
                    if (pe.Modules != null)
                        foreach (var mn in pe.Modules) { var md = ModuleCatalog.Get(mn); if (md != null) entry.Modules.Add(md); }
                    if (pe.Mounts != null)
                        foreach (var ms in pe.Mounts)
                        {
                            var mount = new RadialMount { Separate = ms.Separate, Stage = ms.Stage, FireStage = ms.FireStage, Side = ms.Side };
                            if (ms.Parts != null)
                                foreach (var pn in ms.Parts) { var rdef = PartCatalog.Get(pn); if (rdef != null) mount.Parts.Add(rdef); }
                            if (mount.Parts.Count > 0) entry.Mounts.Add(mount);
                        }
                    else if (pe.Radials != null)   // legacy single-part radials
                        for (int i = 0; i < pe.Radials.Count; i++)
                        {
                            var rdef = PartCatalog.Get(pe.Radials[i]);
                            if (rdef == null) continue;
                            bool sep = pe.RadialSep == null || i >= pe.RadialSep.Count || pe.RadialSep[i];
                            entry.AddRadial(rdef, sep);
                        }
                    if (pe.CrewNames != null) entry.CrewNames.AddRange(pe.CrewNames);
                    vd.Stack.Add(entry);
                }
            }
            else
            {
                foreach (var n in Parts) { var def = PartCatalog.Get(n); if (def != null) vd.Stack.Add(new StackEntry(def)); }
            }
        }
    }

    public sealed class ModuleStateEntry
    {
        public string Name;
        public bool Active;
        public bool Broken;     // malfunctioned module (see ModuleInstance.Broken)
        public double Wear;     // accumulated wear 0..1 (see ModuleInstance.Wear)
    }

    public sealed class PartState
    {
        public string DefName;
        public double Fuel;
        public bool Ignited;
        public bool Deployed;
        public int Stage = -1;               // activation stage (-1 = derive from geometry on load)
        public bool RadialSeparate = true;   // when this part is a radial attachment: own stage vs welded
        // design round-trip tags (see Part); default -1 keeps older saves regrouping as single-part mounts
        public int RadialMountId = -1, RadialSide = -1, RadialSlot = -1;
        public List<ModuleStateEntry> Modules;
        public List<PartState> Radials;   // materialized radial parts (one per side)
        public List<string> Crew;         // names of crew aboard this part (resolved against the roster)

        public static PartState Of(Part p)
        {
            var ps = new PartState { DefName = p.Def.Name, Fuel = p.Fuel, Ignited = p.Ignited, Deployed = p.Deployed, Stage = p.Stage,
                                     RadialSeparate = p.RadialSeparate, RadialMountId = p.RadialMountId, RadialSide = p.RadialSide, RadialSlot = p.RadialSlot };
            if (p.Modules.Count > 0)
            {
                ps.Modules = new List<ModuleStateEntry>();
                foreach (var m in p.Modules) ps.Modules.Add(new ModuleStateEntry { Name = m.Def.Name, Active = m.Active, Broken = m.Broken, Wear = m.Wear });
            }
            if (p.Crew.Count > 0)
            {
                ps.Crew = new List<string>();
                foreach (var c in p.Crew) ps.Crew.Add(c.Name);
            }
            if (p.Radials.Count > 0)
            {
                ps.Radials = new List<PartState>();
                foreach (var r in p.Radials) ps.Radials.Add(Of(r));
            }
            return ps;
        }

        public Part ToPart(List<CrewMember> roster = null)
        {
            var def = PartCatalog.Get(DefName);
            if (def == null) return null;
            var part = new Part(def) { Fuel = Fuel, Ignited = Ignited, Deployed = Deployed, Stage = Stage, RadialSeparate = RadialSeparate,
                                       RadialMountId = RadialMountId, RadialSide = RadialSide, RadialSlot = RadialSlot };
            if (Modules != null)
                foreach (var ms in Modules)
                {
                    var md = ModuleCatalog.Get(ms.Name);
                    if (md != null) part.Modules.Add(new ModuleInstance(md) { Active = ms.Active, Broken = ms.Broken, Wear = ms.Wear });
                }
            if (Crew != null && roster != null)
                foreach (var name in Crew)
                {
                    if (part.Crew.Count >= part.SeatCount) break;
                    var c = roster.Find(x => x.Name == name && x.Status == CrewStatus.Active);
                    if (c != null && !part.Crew.Contains(c)) part.Crew.Add(c);
                }
            if (Radials != null)
                foreach (var rs in Radials) { var rp = rs.ToPart(roster); if (rp != null) part.Radials.Add(rp); }
            return part;
        }
    }

    /// <summary>A docking join, serialized by the axial-stack indices of the two joined ports and the
    /// index where the docked module's parts begin, so a built-up station reloads still-undockable.</summary>
    public sealed class DockLinkState
    {
        public int PortA;
        public int PortB;
        public int ModuleStart;
        // Pose of the docked module in the vessel local frame (see Vessel.DockLink). Null/absent on
        // pre-pose saves, which are migrated to the old straight-stack look on load.
        public int? QuarterTurns;
        public double? OffsetX;
        public double? OffsetY;
    }

    public sealed class ManeuverState
    {
        public double UT;
        public double Prograde;
        public double Radial;
        public OrbitalElements Source;
        public bool HasSource;
        public bool Reached;
        public Vec2d ReachedAbsPos;
        public string BodyName;   // primary the node is planned around; re-resolved/refreshed on load

        public static ManeuverState From(Maneuver m) =>
            m == null ? null : new ManeuverState
            {
                UT = m.UT, Prograde = m.Prograde, Radial = m.Radial, Source = m.Source, HasSource = m.HasSource,
                Reached = m.Reached, ReachedAbsPos = m.ReachedAbsPos, BodyName = m.Body?.Name
            };

        public Maneuver ToManeuver() =>
            new Maneuver
            {
                UT = UT, Prograde = Prograde, Radial = Radial, Source = Source, HasSource = HasSource,
                Reached = Reached, ReachedAbsPos = ReachedAbsPos
            };
    }

    /// <summary>A persistent flying/landed vessel, serializable. Part definitions are referenced by
    /// name and re-resolved against the catalog on load.</summary>
    public sealed class ShipState
    {
        public string Name;
        public string BodyName;
        public Vec2d Position;
        public Vec2d Velocity;
        public double Heading;
        public double Throttle;
        public bool OnRails;
        public OrbitalElements Orbit;
        public bool Landed;
        public bool Destroyed;
        public bool IsColony;        // an established surface base (offline production runs while unattended)
        public double ColonyGrowthTimer;  // accumulated self-sustaining time toward the next colonist
        public double LastUT;        // UT this ship was last simulated, so a colony can catch up production on load
        public bool EnginesIgnited;
        public bool MissionCancelable = true;   // flight can still be scrapped back to the editor (pre first touch/dock)
        public bool HasLeftLaunchSite;
        public int CurrentStage;     // next stage to fire, so a resumed ship stages correctly
        public double ElectricCharge;
        public double Monoprop;      // monopropellant for RCS translation
        public double Ore;           // raw ore awaiting ISRU refining
        public double LifeSupport;   // legacy single life-support pool (still read for old-save migration)
        public double Water, Oxygen, Food;   // current life-support resources
        public string TargetName;    // remembered rendezvous target (body or ship name), resolved on resume
        public List<PartState> Parts = new();
        public List<DockLinkState> Links;   // docking joins on a compound vessel (station), if any
        public List<ManeuverState> Nodes;
        public ManeuverState Node;   // legacy single-node field, still read on load

        public static ShipState From(Vessel v, string name, IReadOnlyList<Maneuver> nodes = null, string targetName = null, double ut = 0)
        {
            var s = new ShipState
            {
                Name = name,
                BodyName = v.Body?.Name,
                Position = v.Position,
                Velocity = v.Velocity,
                Heading = v.Heading,
                Throttle = v.Throttle,
                OnRails = v.OnRails,
                Orbit = v.Orbit,
                Landed = v.Landed,
                Destroyed = v.Destroyed,
                IsColony = v.IsColony,
                ColonyGrowthTimer = v.ColonyGrowthTimer,
                LastUT = ut,
                EnginesIgnited = v.EnginesIgnited,
                MissionCancelable = v.MissionCancelable,
                HasLeftLaunchSite = v.HasLeftLaunchSite,
                CurrentStage = v.CurrentStage,
                ElectricCharge = v.ElectricCharge,
                Monoprop = v.Monoprop,
                Ore = v.Ore,
                Water = v.Water,
                Oxygen = v.Oxygen,
                Food = v.Food,
                TargetName = targetName,
            };
            if (nodes != null && nodes.Count > 0)
            {
                s.Nodes = new List<ManeuverState>();
                foreach (var n in nodes) s.Nodes.Add(ManeuverState.From(n));
            }
            foreach (var p in v.Parts) s.Parts.Add(PartState.Of(p));
            if (v.DockLinks.Count > 0)
            {
                s.Links = new List<DockLinkState>();
                foreach (var dl in v.DockLinks)
                    s.Links.Add(new DockLinkState
                    {
                        PortA = v.Parts.IndexOf(dl.PortA),
                        PortB = v.Parts.IndexOf(dl.PortB),
                        ModuleStart = dl.ModuleStart,
                        QuarterTurns = dl.QuarterTurns,
                        OffsetX = dl.Offset.X,
                        OffsetY = dl.Offset.Y,
                    });
            }
            return s;
        }

        public Vessel ToVessel(Universe u, List<CrewMember> roster = null)
        {
            var v = new Vessel
            {
                Body = u[BodyName],
                Position = Position,
                Velocity = Velocity,
                Heading = Heading,
                Throttle = Throttle,
                OnRails = OnRails,
                Orbit = Orbit,
                Landed = Landed,
                Destroyed = Destroyed,
                IsColony = IsColony,
                ColonyGrowthTimer = ColonyGrowthTimer,
                EnginesIgnited = EnginesIgnited,
                MissionCancelable = MissionCancelable,
                HasLeftLaunchSite = HasLeftLaunchSite,
                CurrentStage = CurrentStage,
                ElectricCharge = ElectricCharge,
                Monoprop = Monoprop,
                Ore = Ore,
                Water = Water,
                Oxygen = Oxygen,
                Food = Food,
            };
            foreach (var ps in Parts)
            {
                var part = ps.ToPart(roster);
                if (part != null) v.Parts.Add(part);
            }
            // restore docking joins (skip any that don't map to two valid ports: vessel stays welded)
            if (Links != null)
                foreach (var ls in Links)
                {
                    if (ls.PortA < 0 || ls.PortA >= v.Parts.Count || ls.PortB < 0 || ls.PortB >= v.Parts.Count) continue;
                    if (ls.ModuleStart < 0 || ls.ModuleStart > v.Parts.Count) continue;
                    var a = v.Parts[ls.PortA]; var b = v.Parts[ls.PortB];
                    if (a.Def.Kind != PartKind.DockingPort || b.Def.Kind != PartKind.DockingPort) continue;
                    int q; Vec2d off;
                    if (ls.QuarterTurns.HasValue && ls.OffsetX.HasValue && ls.OffsetY.HasValue)
                    {
                        q = ls.QuarterTurns.Value & 3;
                        off = new Vec2d(ls.OffsetX.Value, ls.OffsetY.Value);
                    }
                    else
                    {
                        // legacy save: the old model appended a docked module to the END of the part list,
                        // so it rendered as a straight stack hanging below the root (higher index = base).
                        // Reproduce that by placing the module directly below the root: its nose at y=0,
                        // extending down by the module's own height.
                        q = 0;
                        int end = v.Parts.Count;
                        if (Links != null)
                            foreach (var other in Links)
                                if (other.ModuleStart > ls.ModuleStart && other.ModuleStart < end) end = other.ModuleStart;
                        double modH = 0;
                        for (int i = ls.ModuleStart; i < end && i < v.Parts.Count; i++) modH += v.Parts[i].Def.Height;
                        off = new Vec2d(0, -modH);
                    }
                    v.DockLinks.Add(new Vessel.DockLink { PortA = a, PortB = b, ModuleStart = ls.ModuleStart, QuarterTurns = q, Offset = off });
                }
            // migrate pre-resource saves: if no per-resource data but the old pool had supply, top up
            if (Water <= 0 && Oxygen <= 0 && Food <= 0 && LifeSupport > 0)
            { v.Water = v.WaterCapacity; v.Oxygen = v.OxygenCapacity; v.Food = v.FoodCapacity; }
            return v;
        }
    }
}
