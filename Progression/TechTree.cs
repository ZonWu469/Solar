using System.Collections.Generic;
using Solar.Core;

namespace Solar.Progression
{
    /// <summary>One node in the R&D tree: unlocking it (for <see cref="Cost"/> science, once its
    /// prerequisites are unlocked) makes all of its parts and modules available in the editor.</summary>
    public sealed class TechNode
    {
        public string Id;
        public string Title;
        public double Cost;
        public string[] Prereqs;
        public string[] Parts;     // PartDef names this node unlocks
        public string[] Modules;   // ModuleDef names this node unlocks
        public string Description; // optional flavor / hint text
    }

    /// <summary>The progression tech tree. Parts/modules are mapped to nodes here (in code) rather than
    /// in parts.json/modules.json, so the catalog files need no migration and progression config lives
    /// in one place. Anything not listed defaults to the always-unlocked <c>start</c> node.</summary>
    public static class TechTree
    {
        public static readonly List<TechNode> Nodes = new()
        {
            // ════════════════════════════════════════════════════════════════
            // TIER 0 — always unlocked
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "start", Title = "Start", Cost = 0, Prereqs = new string[0],
                Parts = new[] { "Pod Mk1", "Parachute", "Tank T100", "Tank T200", "Tank T400", "Tank T800",
                                "Spark", "Terrier", "Swivel", "Decoupler", "Fin Set", "Nose Cone",
                                "Service Bay 1.7m", "Cargo Truss" },
                Modules = new[] { "Battery Z-100", "Mystery Goo", "Comm-16 Antenna" },
                Description = "Basic rocketry: command pod, parachute, engines, fuel tanks, decoupler, and simple structural bays.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 1 — early unlocks (8–20 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "field-science", Title = "Field Science", Cost = 8, Prereqs = new[] { "start" },
                Parts = new string[0],
                Modules = new[] { "Thermometer", "Barometer" },
                Description = "Basic instruments to take temperature and pressure readings — the first building blocks of a science program.",
            },
            new TechNode {
                Id = "electrics", Title = "Electrics", Cost = 12, Prereqs = new[] { "start" },
                Parts = new string[0],
                Modules = new[] { "Solar Panel", "Battery", "Battery Bank", "Fuel Cell", "Utility Light", "EC Storage Pod" },
                Description = "Solar panels, batteries, fuel cells, and storage pods to keep your vessel powered beyond the launchpad.",
            },
            new TechNode {
                Id = "control", Title = "Flight Control", Cost = 18, Prereqs = new[] { "start" },
                Parts = new string[0],
                Modules = new[] { "Reaction Wheel" },
                Description = "Reaction wheels give you attitude authority in vacuum — no atmosphere required.",
            },
            new TechNode {
                Id = "solids", Title = "Solid Boosters", Cost = 20, Prereqs = new[] { "start" },
                Parts = new[] { "Thumper SRB", "Thumper-R", "Radial Decoupler" },
                Modules = new string[0],
                Description = "Solid-fuel boosters give a powerful kick off the pad. Once lit they burn until empty.",
            },
            new TechNode {
                Id = "landing", Title = "Landing", Cost = 14, Prereqs = new[] { "start" },
                Parts = new string[0],
                Modules = new[] { "Landing Legs" },
                Description = "Extendable legs absorb touchdown shock — essential for targeted landings on the Moon and beyond.",
            },
            new TechNode {
                Id = "miniaturization", Title = "Miniaturization", Cost = 18, Prereqs = new[] { "start" },
                Parts = new string[0],
                Modules = new[] { "RTG" },
                Description = "Radioisotope thermoelectric generators provide a trickle of power anywhere, even in deep shadow.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 2 — midgame (20–55 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "science", Title = "Science Tech", Cost = 25, Prereqs = new[] { "field-science" },
                Parts = new string[0],
                Modules = new[] { "Science Jr", "Antenna", "Radiation Detector" },
                Description = "Dedicated science instruments and antennas to transmit findings back home for full credit.",
            },
            new TechNode {
                Id = "survival", Title = "Life Support", Cost = 40, Prereqs = new[] { "electrics" },
                Parts = new string[0],
                Modules = new[] { "Life Support", "Drill" },
                Description = "Life support systems keep your crew alive on long journeys. Drills refuel tanks by mining on the surface.",
            },
            new TechNode {
                Id = "fuel-cells", Title = "Fuel Cells", Cost = 30, Prereqs = new[] { "electrics" },
                Parts = new string[0],
                Modules = new[] { "Spotlight", "Large Light Array" },
                Description = "Compact power from liquid fuel — fuel cells convert propellant into electricity on demand. High-power lights for night landings.",
            },
            new TechNode {
                Id = "radial", Title = "Radial Construction", Cost = 30, Prereqs = new[] { "solids" },
                Parts = new[] { "Radial Tank", "Stack Bi-Adapter" },
                Modules = new string[0],
                Description = "Mount tanks and equipment to the sides of your stack. Stack adapters bridge different diameters.",
            },
            new TechNode {
                Id = "heavy", Title = "Heavy Rocketry", Cost = 45, Prereqs = new[] { "start" },
                Parts = new[] { "Jumbo T2000", "Mainsail" },
                Modules = new string[0],
                Description = "Bigger is better. The Mainsail and Jumbo tank lift serious payloads into orbit.",
            },
            new TechNode {
                Id = "gimbaled", Title = "Gimbaled Engines", Cost = 40, Prereqs = new[] { "control", "heavy" },
                Parts = new[] { "Vector" },
                Modules = new string[0],
                Description = "The mighty Vector engine gimbals hard — excellent control authority for unwieldy stacks.",
            },
            new TechNode {
                Id = "reentry", Title = "Re-entry", Cost = 35, Prereqs = new[] { "landing" },
                Parts = new string[0],
                Modules = new string[0],
                Description = "Foundational re-entry research — a prerequisite for heavy landing systems and crewed survival.",
            },
            new TechNode {
                Id = "probes", Title = "Probes", Cost = 35, Prereqs = new[] { "miniaturization" },
                Parts = new[] { "Probe Core" },
                Modules = new string[0],
                Description = "Lightweight probe cores let you fly unmanned missions — no life support needed.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 3 — advanced (55–160 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "advanced", Title = "Advanced Propulsion", Cost = 80, Prereqs = new[] { "heavy" },
                Parts = new[] { "Nerv", "Tank T1600" },
                Modules = new[] { "Relay Antenna" },
                Description = "The nuclear Nerv engine delivers incredible efficiency — 800 s Isp opens up the outer solar system.",
            },
            new TechNode {
                Id = "advanced-science", Title = "Advanced Science", Cost = 90, Prereqs = new[] { "science" },
                Parts = new string[0],
                Modules = new[] { "Gravioli Detector", "Magnetometer" },
                Description = "Detect gravity waves and magnetic fields for high-value science returns.",
            },
            new TechNode {
                Id = "sustainability", Title = "Sustainability", Cost = 100, Prereqs = new[] { "survival" },
                Parts = new string[0],
                Modules = new[] { "Crew Cabin", "Hydroponics Bay", "CO₂ Scrubber", "Water Recycler", "LS Storage Pod" },
                Description = "Crew cabins, recyclers, and hydroponics turn short-hop capsules into long-duration habitats.",
            },
            new TechNode {
                Id = "vacuum-engines", Title = "Vacuum Engines", Cost = 75, Prereqs = new[] { "gimbaled" },
                Parts = new[] { "Poodle" },
                Modules = new string[0],
                Description = "The Poodle is optimized for vacuum operation — efficient, reliable, and perfectly sized for upper stages.",
            },
            new TechNode {
                Id = "megarocketry", Title = "Megarocketry", Cost = 120, Prereqs = new[] { "heavy" },
                Parts = new[] { "Tank T4000", "Rhino", "Toroidal Tank T1200", "Stack Tri-Adapter" },
                Modules = new string[0],
                Description = "The Rhino engine, T4000 tank, and toroidal fuel stores push into the mega-lift class — think interplanetary motherships.",
            },
            new TechNode {
                Id = "radial-advanced", Title = "Advanced Radial", Cost = 70, Prereqs = new[] { "radial" },
                Parts = new[] { "Tank T400-R", "Service Bay 2.3m" },
                Modules = new string[0],
                Description = "Larger radial tanks and 2.3m service bays let you strap on serious boost without extending the stack.",
            },
            new TechNode {
                Id = "ultra-heavy", Title = "Ultra-Heavy", Cost = 160, Prereqs = new[] { "megarocketry" },
                Parts = new[] { "Tank T8000", "Sledgehammer SRB" },
                Modules = new string[0],
                Description = "The T8000 and massive Sledgehammer SRB — for missions that rewrite the record books.",
            },
            new TechNode {
                Id = "aerospace", Title = "Aerospace", Cost = 110, Prereqs = new[] { "megarocketry" },
                Parts = new[] { "Aerospike" },
                Modules = new string[0],
                Description = "The altitude-compensating aerospike engine — efficient thrust across the whole climb to orbit.",
            },
            new TechNode {
                Id = "heavy-landing", Title = "Heavy Landing", Cost = 90, Prereqs = new[] { "reentry" },
                Parts = new[] { "Heavy Landing Gear", "Mk1 Lander Can" },
                Modules = new string[0],
                Description = "Reinforced landing gear and the Mk1 Lander Can support crewed surface missions on any world.",
            },
            new TechNode {
                Id = "monoprop", Title = "Monopropellant", Cost = 80, Prereqs = new[] { "probes", "control" },
                Parts = new[] { "Dawn" },
                Modules = new[] { "Monoprop Tank", "RCS Thruster Block", "Cryo Tank", "Large RCS Block", "Fuel Storage Pod" },
                Description = "RCS thrusters, monopropellant tanks, and cryo storage for fine translation control — crucial for docking.",
            },
            new TechNode {
                Id = "docking", Title = "Docking", Cost = 100, Prereqs = new[] { "monoprop" },
                Parts = new[] { "Docking Port Jr", "Docking Port Sr" },
                Modules = new[] { "Docking Sensor" },
                Description = "Docking ports allow two vessels to join in orbit — the gateway to space stations and orbital assembly.",
            },
            new TechNode {
                Id = "planetary-science", Title = "Planetary Science", Cost = 100, Prereqs = new[] { "advanced-science" },
                Parts = new string[0],
                Modules = new[] { "Atmosphere Analyzer", "Ore Scanner" },
                Description = "Instruments tailored for planetary exploration — analyze atmospheres and scan for ore deposits.",
            },
            new TechNode {
                Id = "deep-space-science", Title = "Deep Space Science", Cost = 130, Prereqs = new[] { "planetary-science" },
                Parts = new string[0],
                Modules = new[] { "Comm-88 Antenna", "Deep Space Lab" },
                Description = "Long-range comms and deep-space laboratories keep your probes productive far from home. Science never sleeps.",
            },
            new TechNode {
                Id = "advanced-electrics", Title = "Advanced Electrics", Cost = 90, Prereqs = new[] { "electrics", "science" },
                Parts = new string[0],
                Modules = new[] { "Large Solar Array", "Battery Z-4000" },
                Description = "Bigger panels and denser batteries — power-hungry craft need serious infrastructure.",
            },
            new TechNode {
                Id = "nuclear-power", Title = "Nuclear Power", Cost = 140, Prereqs = new[] { "sustainability", "advanced-electrics" },
                Parts = new string[0],
                Modules = new[] { "Nuclear Reactor", "Stirling RTG" },
                Description = "A compact fission reactor delivers abundant passive power anywhere. Advanced Stirling RTGs complete the nuclear suite.",
            },
            new TechNode {
                Id = "power-systems", Title = "Power Systems", Cost = 150, Prereqs = new[] { "nuclear-power" },
                Parts = new string[0],
                Modules = new[] { "Gigantor XL", "Battery Z-10k" },
                Description = "The Gigantor array and massive battery banks let you run energy-intensive equipment indefinitely.",
            },
            new TechNode {
                Id = "resource-processing", Title = "Resource Processing", Cost = 150, Prereqs = new[] { "sustainability", "planetary-science" },
                Parts = new string[0],
                Modules = new[] { "ISRU Converter", "Seismic Accelerometer", "Advanced Harvester" },
                Description = "In-situ resource utilization — convert ore to fuel anywhere. Deep-core drills extract fuel from even the poorest deposits.",
            },
            new TechNode {
                Id = "advanced-probes", Title = "Advanced Probes", Cost = 80, Prereqs = new[] { "probes" },
                Parts = new[] { "Probe Core XL" },
                Modules = new string[0],
                Description = "Larger probe cores carry more experiments and on-board power — robotic explorers par excellence.",
            },
            new TechNode {
                Id = "crew-systems", Title = "Crew Systems", Cost = 85, Prereqs = new[] { "survival", "reentry" },
                Parts = new[] { "Pod Mk2" },
                Modules = new string[0],
                Description = "The Mk2 pod seats three crew — more hands, more science, more ambition.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 4 — endgame (150–300 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "ion-prop", Title = "Ion Propulsion", Cost = 300, Prereqs = new[] { "advanced-electrics", "deep-space-science" },
                Parts = new[] { "Ion Drive" },
                Modules = new string[0],
                Description = "Xenon ion thrusters achieve breathtaking Isp at the expense of thrust — the ultimate deep-space drive.",
            },
            new TechNode {
                Id = "space-stations", Title = "Space Stations", Cost = 200, Prereqs = new[] { "docking", "crew-systems" },
                Parts = new[] { "Shielded Port" },
                Modules = new string[0],
                Description = "Shielded docking ports and modular construction open the door to permanent orbital outposts.",
            },
            new TechNode {
                Id = "surface-science", Title = "Surface Science", Cost = 180, Prereqs = new[] { "deep-space-science", "heavy-landing" },
                Parts = new string[0],
                Modules = new[] { "Sample Return Capsule" },
                Description = "Sample return capsules and landed experiments yield the highest-value science returns in the solar system.",
            },
            new TechNode {
                Id = "deep-space-net", Title = "Deep Space Network", Cost = 250, Prereqs = new[] { "deep-space-science", "advanced-probes" },
                Parts = new string[0],
                Modules = new[] { "Comm-DSN Dish" },
                Description = "The giant DSN dish can phone home from the outer planets — no data left behind.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 5 — grand finale (400–600 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "heavy-crew", Title = "Heavy Crew", Cost = 400, Prereqs = new[] { "crew-systems", "ultra-heavy" },
                Parts = new[] { "Pod Mk3" },
                Modules = new string[0],
                Description = "The Mk3 pod carries five crew — a flying command center for the most ambitious missions.",
            },
            new TechNode {
                Id = "near-future", Title = "Near Future Propulsion", Cost = 450, Prereqs = new[] { "nuclear-power", "megarocketry" },
                Parts = new[] { "Nuclear Lightbulb", "PIT Thruster" },
                Modules = new string[0],
                Description = "Advanced nuclear propulsion: the closed-cycle gas-core lightbulb and pulsed inductive thruster push Isp into four digits.",
            },
            new TechNode {
                Id = "grand-finale", Title = "Grand Finale", Cost = 500, Prereqs = new[] { "ion-prop", "space-stations", "deep-space-net" },
                Parts = new string[0],
                Modules = new[] { "Signal Booster" },
                Description = "The pinnacle of aerospace research. Extends comms to the farthest reaches of the solar system.",
            },
        };

        public static TechNode Node(string id) => Nodes.Find(n => n.Id == id);

        /// <summary>Node ids that are unlocked for free in a new game (everything with zero cost).</summary>
        public static List<string> StartingNodes()
        {
            var list = new List<string>();
            foreach (var n in Nodes) if (n.Cost <= 0) list.Add(n.Id);
            return list;
        }

        /// <summary>Every node id (used by the "Internal_" sandbox cheat to unlock the whole tree).</summary>
        public static List<string> AllNodeIds()
        {
            var list = new List<string>(Nodes.Count);
            foreach (var n in Nodes) list.Add(n.Id);
            return list;
        }

        /// <summary>The node a part name belongs to (defaults to the always-unlocked start node).</summary>
        public static string TechForPart(string partName)
        {
            foreach (var n in Nodes)
                foreach (var p in n.Parts) if (p == partName) return n.Id;
            return "start";
        }

        public static string TechForModule(string moduleName)
        {
            foreach (var n in Nodes)
                foreach (var m in n.Modules) if (m == moduleName) return n.Id;
            return "start";
        }

        public static bool PartAvailable(GameState gs, string partName) => Available(gs, TechForPart(partName));

        public static bool ModuleAvailable(GameState gs, string moduleName) => Available(gs, TechForModule(moduleName));

        /// <summary>The free start node is always available (so default parts work even for old saves
        /// that predate the tech tree); everything else must be explicitly unlocked.</summary>
        private static bool Available(GameState gs, string nodeId) => nodeId == "start" || gs.UnlockedTech.Contains(nodeId);

        public static bool IsUnlocked(GameState gs, string nodeId) => gs.UnlockedTech.Contains(nodeId);

        /// <summary>Whether the prerequisites for a (not-yet-unlocked) node are all satisfied.</summary>
        public static bool PrereqsMet(GameState gs, TechNode n)
        {
            foreach (var p in n.Prereqs) if (!gs.UnlockedTech.Contains(p)) return false;
            return true;
        }

        public static bool CanUnlock(GameState gs, TechNode n) =>
            n != null && !IsUnlocked(gs, n.Id) && PrereqsMet(gs, n) && gs.Science >= n.Cost;

        /// <summary>Spend science and unlock the node. Returns false if it isn't currently unlockable.</summary>
        public static bool Unlock(GameState gs, TechNode n)
        {
            if (!CanUnlock(gs, n)) return false;
            gs.Science -= n.Cost;
            gs.UnlockedTech.Add(n.Id);
            return true;
        }
    }
}