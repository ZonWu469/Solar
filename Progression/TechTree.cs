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
        public string[] Parts;     // PartDef ids this node unlocks
        public string[] Modules;   // ModuleDef ids this node unlocks
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
                Parts = new[] { "pod-mk1", "parachute", "tank-t100", "tank-t200", "tank-t400", "tank-t800",
                                "spark", "terrier", "swivel", "decoupler", "fin-set", "nose-cone",
                                "service-bay-1-7m", "cargo-truss" },
                Modules = new[] { "battery-z100", "mystery-goo", "comm-16" },
                Description = "Basic rocketry: command pod, parachute, engines, fuel tanks, decoupler, and simple structural bays.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 1 — early unlocks (8–20 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "field-science", Title = "Field Science", Cost = 8, Prereqs = new[] { "start" },
                Parts = new string[0],
                Modules = new[] { "thermometer", "barometer" },
                Description = "Basic instruments to take temperature and pressure readings — the first building blocks of a science program.",
            },
            new TechNode {
                Id = "electrics", Title = "Electrics", Cost = 12, Prereqs = new[] { "start" },
                Parts = new string[0],
                Modules = new[] { "solar-panel", "battery", "battery-bank", "fuel-cell", "utility-light", "ec-storage-pod" },
                Description = "Solar panels, batteries, fuel cells, and storage pods to keep your vessel powered beyond the launchpad.",
            },
            new TechNode {
                Id = "control", Title = "Flight Control", Cost = 18, Prereqs = new[] { "start" },
                Parts = new string[0],
                Modules = new[] { "reaction-wheel" },
                Description = "Reaction wheels give you attitude authority in vacuum — no atmosphere required.",
            },
            new TechNode {
                Id = "solids", Title = "Solid Boosters", Cost = 20, Prereqs = new[] { "start" },
                Parts = new[] { "thumper-srb", "thumper-r", "radial-decoupler" },
                Modules = new string[0],
                Description = "Solid-fuel boosters give a powerful kick off the pad. Once lit they burn until empty.",
            },
            new TechNode {
                Id = "landing", Title = "Landing", Cost = 14, Prereqs = new[] { "start" },
                Parts = new[] { "landing-gear" },
                Modules = new[] { "landing-legs" },
                Description = "Extendable legs absorb touchdown shock — essential for targeted landings on the Moon and beyond.",
            },
            new TechNode {
                Id = "miniaturization", Title = "Miniaturization", Cost = 18, Prereqs = new[] { "start" },
                Parts = new string[0],
                Modules = new[] { "rtg" },
                Description = "Radioisotope thermoelectric generators provide a trickle of power anywhere, even in deep shadow.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 2 — midgame (20–55 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "science", Title = "Science Tech", Cost = 25, Prereqs = new[] { "field-science" },
                Parts = new string[0],
                Modules = new[] { "science-jr", "antenna", "radiation-detector" },
                Description = "Dedicated science instruments and antennas to transmit findings back home for full credit.",
            },
            new TechNode {
                Id = "survival", Title = "Life Support", Cost = 40, Prereqs = new[] { "electrics" },
                Parts = new[] { "solar-shield", "solar-shield-r" },
                Modules = new[] { "life-support", "drill", "snack-container", "storm-shelter", "radiator-panel" },
                Description = "Life support systems keep your crew alive on long journeys. Drills refuel tanks by mining on the surface. Storm shelters, radiators, and deployable solar shields harden a crew against solar storms.",
            },
            new TechNode {
                Id = "fuel-cells", Title = "Fuel Cells", Cost = 30, Prereqs = new[] { "electrics" },
                Parts = new string[0],
                Modules = new[] { "spotlight", "large-light-array" },
                Description = "Compact power from liquid fuel — fuel cells convert propellant into electricity on demand. High-power lights for night landings.",
            },
            new TechNode {
                Id = "radial", Title = "Radial Construction", Cost = 30, Prereqs = new[] { "solids" },
                Parts = new[] { "radial-tank", "i-beam", "long-i-beam" },
                Modules = new string[0],
                Description = "Mount tanks and equipment to the sides of your stack.",
            },
            new TechNode {
                Id = "heavy", Title = "Heavy Rocketry", Cost = 45, Prereqs = new[] { "start" },
                Parts = new[] { "jumbo-t2000", "mainsail", "dart", "skipper" },
                Modules = new string[0],
                Description = "Bigger is better. The Mainsail and Jumbo tank lift serious payloads into orbit.",
            },
            new TechNode {
                Id = "gimbaled", Title = "Gimbaled Engines", Cost = 40, Prereqs = new[] { "control", "heavy" },
                Parts = new[] { "vector" },
                Modules = new[] { "heavy-reaction-wheel" },
                Description = "The mighty Vector engine gimbals hard — excellent control authority for unwieldy stacks.",
            },
            new TechNode {
                Id = "reentry", Title = "Re-entry", Cost = 35, Prereqs = new[] { "landing" },
                Parts = new[] { "drogue-chute", "large-parachute", "radial-parachute", "radial-drogue" },
                Modules = new string[0],
                Description = "Drogue and heavy parachutes for controlled re-entry — and the foundation for heavy landing systems and crewed survival.",
            },
            new TechNode {
                Id = "probes", Title = "Probes", Cost = 35, Prereqs = new[] { "miniaturization" },
                Parts = new[] { "probe-core", "service-pod-mk1", "comsat-core", "oscar-tank" },
                Modules = new string[0],
                Description = "Lightweight probe cores let you fly unmanned missions — no life support needed.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 3 — advanced (55–160 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "advanced", Title = "Advanced Propulsion", Cost = 80, Prereqs = new[] { "heavy" },
                Parts = new[] { "nerv", "tank-t1600" },
                Modules = new[] { "relay-antenna", "comm-32" },
                Description = "The nuclear Nerv engine delivers incredible efficiency — 800 s Isp opens up the outer solar system.",
            },
            new TechNode {
                Id = "advanced-science", Title = "Advanced Science", Cost = 90, Prereqs = new[] { "science" },
                Parts = new string[0],
                Modules = new[] { "gravioli-detector", "magnetometer" },
                Description = "Detect gravity waves and magnetic fields for high-value science returns.",
            },
            new TechNode {
                Id = "sustainability", Title = "Sustainability", Cost = 100, Prereqs = new[] { "survival" },
                Parts = new string[0],
                Modules = new[] { "crew-cabin", "hydroponics-bay", "co2-scrubber", "water-recycler", "ls-storage-pod" },
                Description = "Crew cabins, recyclers, and hydroponics turn short-hop capsules into long-duration habitats.",
            },
            new TechNode {
                Id = "vacuum-engines", Title = "Vacuum Engines", Cost = 75, Prereqs = new[] { "gimbaled" },
                Parts = new[] { "poodle", "wolfhound" },
                Modules = new string[0],
                Description = "The Poodle is optimized for vacuum operation — efficient, reliable, and perfectly sized for upper stages.",
            },
            new TechNode {
                Id = "megarocketry", Title = "Megarocketry", Cost = 120, Prereqs = new[] { "heavy" },
                Parts = new[] { "tank-t4000", "rhino", "toroidal-tank-t1200" },
                Modules = new string[0],
                Description = "The Rhino engine, T4000 tank, and toroidal fuel stores push into the mega-lift class — think interplanetary motherships.",
            },
            new TechNode {
                Id = "radial-advanced", Title = "Advanced Radial", Cost = 70, Prereqs = new[] { "radial" },
                Parts = new[] { "tank-t400-r", "service-bay-2-3m" },
                Modules = new string[0],
                Description = "Larger radial tanks and 2.3m service bays let you strap on serious boost without extending the stack.",
            },
            new TechNode {
                Id = "ultra-heavy", Title = "Ultra-Heavy", Cost = 160, Prereqs = new[] { "megarocketry" },
                Parts = new[] { "tank-t8000", "sledgehammer-srb", "mammoth" },
                Modules = new string[0],
                Description = "The T8000 and massive Sledgehammer SRB — for missions that rewrite the record books.",
            },
            new TechNode {
                Id = "imperator-heavy", Title = "Imperator-Heavy", Cost = 180, Prereqs = new[] { "megarocketry" },
                Parts = new[] { "tank-t16000", "imperator-srb" },
                Modules = new string[0],
                Description = "The T16000 and massive Imperator SRB — for missions that rewrite the record books.",
            },
            new TechNode {
                Id = "aerospace", Title = "Aerospace", Cost = 110, Prereqs = new[] { "megarocketry" },
                Parts = new[] { "aerospike" },
                Modules = new string[0],
                Description = "The altitude-compensating aerospike engine — efficient thrust across the whole climb to orbit.",
            },
            new TechNode {
                Id = "heavy-landing", Title = "Heavy Landing", Cost = 90, Prereqs = new[] { "reentry" },
                Parts = new[] { "heavy-landing-gear", "mk1-lander-can", "lander-can-2" },
                Modules = new string[0],
                Description = "Reinforced landing gear and the Mk1 Lander Can support crewed surface missions on any world.",
            },
            new TechNode {
                Id = "monoprop", Title = "Monopropellant", Cost = 80, Prereqs = new[] { "probes", "control" },
                Parts = new[] { "dawn" },
                Modules = new[] { "monoprop-tank", "rcs-thruster-block", "cryo-tank", "large-rcs-block", "fuel-storage-pod", "rcs-quad" },
                Description = "RCS thrusters, monopropellant tanks, and cryo storage for fine translation control — crucial for docking.",
            },
            new TechNode {
                Id = "docking", Title = "Docking", Cost = 100, Prereqs = new[] { "monoprop" },
                Parts = new[] { "docking-port-jr", "docking-port-sr", "docking-port-md" },
                Modules = new[] { "docking-sensor" },
                Description = "Docking ports allow two vessels to join in orbit — the gateway to space stations and orbital assembly.",
            },
            new TechNode {
                Id = "planetary-science", Title = "Planetary Science", Cost = 100, Prereqs = new[] { "advanced-science" },
                Parts = new string[0],
                Modules = new[] { "atmosphere-analyzer", "ore-scanner", "survey-telescope" },
                Description = "Instruments tailored for planetary exploration — analyze atmospheres, scan for ore deposits, and survey for asteroids.",
            },
            new TechNode {
                Id = "deep-space-science", Title = "Deep Space Science", Cost = 130, Prereqs = new[] { "planetary-science" },
                Parts = new string[0],
                Modules = new[] { "comm-88", "deep-space-lab" },
                Description = "Long-range comms and deep-space laboratories keep your probes productive far from home. Science never sleeps.",
            },
            new TechNode {
                Id = "advanced-electrics", Title = "Advanced Electrics", Cost = 90, Prereqs = new[] { "electrics", "science" },
                Parts = new[] { "power-service-bay" },
                Modules = new[] { "large-solar-array", "battery-z4000" },
                Description = "Bigger panels and denser batteries — power-hungry craft need serious infrastructure.",
            },
            new TechNode {
                Id = "nuclear-power", Title = "Nuclear Power", Cost = 140, Prereqs = new[] { "sustainability", "advanced-electrics" },
                Parts = new string[0],
                Modules = new[] { "nuclear-reactor", "stirling-rtg" },
                Description = "A compact fission reactor delivers abundant passive power anywhere. Advanced Stirling RTGs complete the nuclear suite.",
            },
            new TechNode {
                Id = "power-systems", Title = "Power Systems", Cost = 150, Prereqs = new[] { "nuclear-power" },
                Parts = new string[0],
                Modules = new[] { "gigantor-xl", "battery-z10k" },
                Description = "The Gigantor array and massive battery banks let you run energy-intensive equipment indefinitely.",
            },
            new TechNode {
                Id = "resource-processing", Title = "Resource Processing", Cost = 150, Prereqs = new[] { "sustainability", "planetary-science" },
                Parts = new string[0],
                Modules = new[] { "isru-converter", "seismic-accelerometer", "advanced-harvester", "atmospheric-harvester", "fuel-depot-pod" },
                Description = "In-situ resource utilization — convert ore to fuel anywhere. Deep-core drills extract fuel from even the poorest deposits.",
            },
            new TechNode {
                Id = "advanced-probes", Title = "Advanced Probes", Cost = 80, Prereqs = new[] { "probes" },
                Parts = new[] { "probe-core-xl" },
                Modules = new string[0],
                Description = "Larger probe cores carry more experiments and on-board power — robotic explorers par excellence.",
            },
            new TechNode {
                Id = "crew-systems", Title = "Crew Systems", Cost = 85, Prereqs = new[] { "survival", "reentry" },
                Parts = new[] { "pod-mk2", "big-pod" },
                Modules = new[] { "cupola" },
                Description = "The Mk2 pod seats three crew — more hands, more science, more ambition.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 4 — endgame (150–300 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "ion-prop", Title = "Ion Propulsion", Cost = 300, Prereqs = new[] { "advanced-electrics", "deep-space-science" },
                Parts = new[] { "ion-drive" },
                Modules = new string[0],
                Description = "Xenon ion thrusters achieve breathtaking Isp at the expense of thrust — the ultimate deep-space drive.",
            },
            new TechNode {
                Id = "space-stations", Title = "Space Stations", Cost = 200, Prereqs = new[] { "docking", "crew-systems" },
                Parts = new[] { "shielded-port", "crew-tube", "flat-platform", "heavy-platform" },
                Modules = new string[0],
                Description = "Shielded docking ports and modular construction open the door to permanent orbital outposts.",
            },
            new TechNode {
                Id = "surface-science", Title = "Surface Science", Cost = 180, Prereqs = new[] { "deep-space-science", "heavy-landing" },
                Parts = new string[0],
                Modules = new[] { "sample-return-capsule" },
                Description = "Sample return capsules and landed experiments yield the highest-value science returns in the solar system.",
            },
            new TechNode {
                Id = "deep-space-net", Title = "Deep Space Network", Cost = 250, Prereqs = new[] { "deep-space-science", "advanced-probes" },
                Parts = new string[0],
                Modules = new[] { "comm-dsn-dish" },
                Description = "The giant DSN dish can phone home from the outer planets — no data left behind.",
            },
            new TechNode {
                Id = "colonization", Title = "Colonization", Cost = 180, Prereqs = new[] { "sustainability", "docking" },
                Parts = new[] { "inflatable-habitat", "main-hub" },
                Modules = new[] { "habitation-ring", "greenhouse-dome" },
                Description = "Habitats, hubs, and greenhouse domes turn a landed crewed vessel into a self-sufficient surface colony.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 5 — grand finale (400–600 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "heavy-crew", Title = "Heavy Crew", Cost = 400, Prereqs = new[] { "crew-systems", "ultra-heavy" },
                Parts = new[] { "pod-mk3" },
                Modules = new string[0],
                Description = "The Mk3 pod carries five crew — a flying command center for the most ambitious missions.",
            },
            new TechNode {
                Id = "near-future", Title = "Near Future Propulsion", Cost = 450, Prereqs = new[] { "nuclear-power", "megarocketry" },
                Parts = new[] { "nuclear-lightbulb", "pit-thruster" },
                Modules = new string[0],
                Description = "Advanced nuclear propulsion: the closed-cycle gas-core lightbulb and pulsed inductive thruster push Isp into four digits.",
            },
            new TechNode {
                Id = "grand-finale", Title = "Grand Finale", Cost = 500, Prereqs = new[] { "ion-prop", "space-stations", "deep-space-net" },
                Parts = new string[0],
                Modules = new[] { "signal-booster" },
                Description = "The pinnacle of aerospace research. Extends comms to the farthest reaches of the solar system.",
            },

            // ════════════════════════════════════════════════════════════════
            // TIER 6 — interstellar (500–850 science)
            // ════════════════════════════════════════════════════════════════
            new TechNode {
                Id = "fusion-propulsion", Title = "Fusion Propulsion", Cost = 500, Prereqs = new[] { "near-future", "ion-prop" },
                Parts = new[] { "fusion-torch", "vasimr", "deuterium-tank" },
                Modules = new[] { "fusion-reactor" },
                Description = "Deuterium fusion torches and a reactor for power between the stars. The first drives with enough dV to leave the Sun's grip and reach another star.",
            },
            new TechNode {
                Id = "interstellar-logistics", Title = "Interstellar Logistics", Cost = 650, Prereqs = new[] { "fusion-propulsion", "nuclear-power", "sustainability" },
                Parts = new string[0],
                Modules = new[] { "closed-loop-ls", "radiation-vault", "xl-consumables-pod" },
                Description = "Closed-loop recyclers, deep-space radiation vaults, and bulk consumables keep a crew alive through the years-long crossing between stars.",
            },
            new TechNode {
                Id = "starflight-systems", Title = "Starflight Systems", Cost = 850, Prereqs = new[] { "fusion-propulsion", "deep-space-net" },
                Parts = new[] { "antimatter-drive", "antimatter-pod" },
                Modules = new[] { "interstellar-beacon" },
                Description = "Antimatter propulsion and galaxy-spanning relays - the capstone of starflight, cutting the journey to another sun from a lifetime to a voyage.",
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

        /// <summary>Migrate an old save's unlocked-node ids to the current tree. The combined
        /// "ultra-heavy" node was split into "ultra-heavy" + "imperator-heavy"; a save that earned the
        /// old node (which granted both sets of parts) keeps both, so the Imperator SRB / T16000 stay
        /// unlocked. Idempotent and harmless for sandbox/new saves.</summary>
        public static void MigrateSave(GameState gs)
        {
            if (gs?.UnlockedTech == null) return;
            if (gs.UnlockedTech.Contains("ultra-heavy") && !gs.UnlockedTech.Contains("imperator-heavy"))
                gs.UnlockedTech.Add("imperator-heavy");
        }

        /// <summary>The node a part id belongs to (defaults to the always-unlocked start node).</summary>
        public static string TechForPart(string partId)
        {
            foreach (var n in Nodes)
                foreach (var p in n.Parts) if (p == partId) return n.Id;
            return "start";
        }

        public static string TechForModule(string moduleId)
        {
            foreach (var n in Nodes)
                foreach (var m in n.Modules) if (m == moduleId) return n.Id;
            return "start";
        }

        public static bool PartAvailable(GameState gs, string partId) => Available(gs, TechForPart(partId));

        public static bool ModuleAvailable(GameState gs, string moduleId) => Available(gs, TechForModule(moduleId));

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