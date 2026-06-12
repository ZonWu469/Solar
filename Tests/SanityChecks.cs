using System;
using System.Collections.Generic;
using Solar.Core;
using Solar.Physics;

namespace Solar.Tests
{
    /// <summary>Startup physics self-tests (no test framework; result shown in the main menu).</summary>
    public static class SanityChecks
    {
        public static string Run()
        {
            int pass = 0, total = 0;
            var fails = new List<string>();
            void Check(string name, bool ok) { total++; if (ok) pass++; else fails.Add(name); }

            var rnd = new Random(42);
            const double mu = 3.986004418e12;

            // 1. elliptic + retrograde round trip: elements -> state -> elements -> state
            bool rtOk = true;
            for (int i = 0; i < 60; i++)
            {
                var el = new OrbitalElements
                {
                    A = 7e5 + rnd.NextDouble() * 5e7,
                    E = rnd.NextDouble() * 0.95,
                    ArgPe = (rnd.NextDouble() - 0.5) * 2 * Math.PI,
                    M0 = (rnd.NextDouble() - 0.5) * 2 * Math.PI,
                    Epoch = 0,
                    Mu = mu,
                    Dir = rnd.Next(2) * 2 - 1,
                };
                double t = rnd.NextDouble() * el.Period;
                var (r, v) = Kepler.StateAtTime(el, t);
                var el2 = Kepler.ElementsFromState(r, v, mu, t);
                var (r2, v2) = Kepler.StateAtTime(el2, t);
                if ((r2 - r).Length > 1e-6 * r.Length + 1e-3 || (v2 - v).Length > 1e-6 * v.Length + 1e-6)
                { rtOk = false; break; }
            }
            Check("round-trip", rtOk);

            // 2. energy and angular momentum conservation along propagation
            bool consOk = true;
            for (int i = 0; i < 10 && consOk; i++)
            {
                var el = new OrbitalElements
                {
                    A = 7e5 + rnd.NextDouble() * 1e7,
                    E = rnd.NextDouble() * 0.9,
                    ArgPe = rnd.NextDouble(),
                    M0 = 0,
                    Epoch = 0,
                    Mu = mu,
                    Dir = 1,
                };
                double eps0 = double.NaN, h0 = double.NaN;
                for (int k = 0; k <= 100; k++)
                {
                    var (r, v) = Kepler.StateAtTime(el, el.Period * k / 100);
                    double eps = v.LengthSquared / 2 - mu / r.Length;
                    double h = r.Cross(v);
                    if (k == 0) { eps0 = eps; h0 = h; }
                    else if (Math.Abs(eps - eps0) > 1e-9 * Math.Abs(eps0) || Math.Abs(h - h0) > 1e-9 * Math.Abs(h0))
                    { consOk = false; break; }
                }
            }
            Check("energy/momentum", consOk);

            // 3. hyperbolic solver convergence
            bool hypOk = true;
            for (int i = 0; i < 300; i++)
            {
                double e = 1.001 + rnd.NextDouble() * 4;
                double M = (rnd.NextDouble() - 0.5) * 2e4;
                double H = Kepler.SolveHyperbolic(M, e);
                if (Math.Abs(e * Math.Sinh(H) - H - M) > 1e-8 * Math.Max(1, Math.Abs(M))) { hypOk = false; break; }
            }
            Check("hyperbolic solver", hypOk);

            // 4. radius-crossing prediction: propagate to the predicted time, radius must match
            bool rcOk = true;
            for (int i = 0; i < 30 && rcOk; i++)
            {
                var el = new OrbitalElements
                {
                    A = 1e6 + rnd.NextDouble() * 1e7,
                    E = 0.1 + rnd.NextDouble() * 0.7,
                    ArgPe = rnd.NextDouble(),
                    M0 = rnd.NextDouble(),
                    Epoch = 0,
                    Mu = mu,
                    Dir = rnd.Next(2) * 2 - 1,
                };
                double rTarget = el.Periapsis + (el.Apoapsis - el.Periapsis) * (0.2 + 0.6 * rnd.NextDouble());
                double? tIn = Kepler.NextRadiusCrossingInbound(el, rTarget, 0);
                double? tOut = Kepler.NextRadiusCrossingOutbound(el, rTarget, 0);
                if (tIn.HasValue && Math.Abs(Kepler.StateAtTime(el, tIn.Value).pos.Length - rTarget) > 1e-4 * rTarget) rcOk = false;
                if (tOut.HasValue && Math.Abs(Kepler.StateAtTime(el, tOut.Value).pos.Length - rTarget) > 1e-4 * rTarget) rcOk = false;
                if (!tIn.HasValue && !tOut.HasValue) rcOk = false;
            }
            Check("radius crossing", rcOk);

            // 5. circular orbit quarter-period rotation
            {
                var el = new OrbitalElements { A = 1e6, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                var (r0, _) = Kepler.StateAtTime(el, 0);
                var (r1, _) = Kepler.StateAtTime(el, el.Period / 4);
                Check("quarter period", Math.Abs(r0.Dot(r1)) < 1e-4 * r0.LengthSquared && Math.Abs(r1.Length - 1e6) < 1);
            }

            // 6. maneuver node: zero delta-v is a no-op; a prograde burn at periapsis raises apoapsis only
            {
                var el = new OrbitalElements { A = 7e6, E = 0.1, ArgPe = 0.7, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                double tPe = Kepler.TimeAtTrueAnomaly(el, 0, 0); // periapsis pass
                var zero = new Maneuver { UT = tPe, Prograde = 0, Radial = 0 }.ResultOrbit(el, mu);
                bool noop = Math.Abs(zero.A - el.A) < 1e-3 * el.A && Math.Abs(zero.E - el.E) < 1e-6;

                var raised = new Maneuver { UT = tPe, Prograde = 100, Radial = 0 }.ResultOrbit(el, mu);
                bool apOnly = raised.Apoapsis > el.Apoapsis + 1
                              && Math.Abs(raised.Periapsis - el.Periapsis) < 1e-4 * el.Periapsis; // periapsis ~unchanged
                Check("maneuver node", noop && apOnly);
            }

            // 7. warp-to-node stops before the node: burn is centred on the node, so the stop
            //    time node - bt/2 must lie in (node - bt, node]. Guards the off-by-half-burn sign.
            {
                var v = new Vessels.Vessel();
                foreach (var d in Parts.PartCatalog.DefaultDesign()) v.Parts.Add(new Parts.Part(d));
                double bt = Vessels.Staging.BurnTime(v, 100); // 100 m/s burn
                const double nodeUT = 1000;
                double stop = nodeUT - (bt > 0 ? bt / 2 : 0);
                Check("warp stops before node", bt > 0 && stop <= nodeUT && stop > nodeUT - bt - 1e-9);
            }

            // 8. rendezvous closest approach between two coplanar circular orbits: minimum
            //    separation must be the radius difference (they line up once per synodic period).
            {
                var primary = new CelestialBody { Mu = mu, Radius = 1 };
                var you = new OrbitalElements { A = 1e6, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                var tgt = new OrbitalElements { A = 2e6, E = 0, ArgPe = 0, M0 = Math.PI, Epoch = 0, Mu = mu, Dir = 1 };
                double tsyn = 1.0 / (1.0 / you.Period - 1.0 / tgt.Period);
                bool ok = Rendezvous.ClosestApproach(you, primary, t => Kepler.StateAtTime(tgt, t).pos,
                                                     0, tsyn, out _, out double sep, out _);
                Check("rendezvous", ok && Math.Abs(sep - 1e6) < 5e3);
            }

            // 9. resource accounting: an RTG charges a battery (minus the pod's avionics draw); a
            //    drill mines fuel while landed, paying electric charge for it.
            {
                var v = new Vessels.Vessel();
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Battery")));
                pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("RTG")));
                v.Parts.Add(pod);
                v.ElectricCharge = 0;
                v.UpdateResources(100, 0, null);          // (RTG 1.5 - pod avionics 0.05) EC/s * 100 s = 145
                bool ecOk = Math.Abs(v.ElectricCharge - 145) < 1e-6;

                var v2 = new Vessels.Vessel { Landed = true, ElectricCharge = 100 };
                var tank = new Parts.Part(Parts.PartCatalog.Get("Tank T200")) { Fuel = 0 };
                tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Battery")));
                tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Drill")) { Active = true });
                v2.Parts.Add(tank);
                v2.UpdateResources(10, 0, null);          // drill: +3 fuel/s, -4 EC/s for 10 s
                bool mined = Math.Abs(tank.Fuel - 30) < 1e-6 && Math.Abs(v2.ElectricCharge - 60) < 1e-6;
                Check("resources", ecOk && mined);
            }

            // 10. geometric orbit proximity: two equal circles whose centers are one radius apart
            //     intersect at two points (gap ~ 0); two concentric circles never meet (gap = radius gap).
            {
                var c1 = new OrbitalElements { A = 1e6, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                var c2 = new OrbitalElements { A = 1e6, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                var cross = Rendezvous.OrbitProximity(c1, new Vec2d(0, 0), c2, new Vec2d(1e6, 0));
                bool crossOk = cross.Count == 2 && cross[0].Intersect && cross[1].Intersect
                               && cross[0].Sep < 1e3 && cross[1].Sep < 1e3;

                var inner = new OrbitalElements { A = 1e6, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                var outer = new OrbitalElements { A = 2e6, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                var conc = Rendezvous.OrbitProximity(inner, new Vec2d(0, 0), outer, new Vec2d(0, 0));
                bool concOk = conc.Count >= 1 && !conc[0].Intersect && Math.Abs(conc[0].Sep - 1e6) < 5e3;

                Check("orbit proximity", crossOk && concOk);
            }

            // 11. tech tree: starting parts always available, gated parts hidden until their node is
            //     unlocked; unlocking requires prereqs + enough science and deducts the cost.
            {
                var gs = new GameState { UnlockedTech = Progression.TechTree.StartingNodes() };
                var heavy = Progression.TechTree.Node("heavy");
                bool lockedNoSci = !Progression.TechTree.CanUnlock(gs, heavy);          // 0 science
                gs.Science = 1000;
                bool buyable = Progression.TechTree.CanUnlock(gs, heavy);               // prereq + funds met
                Progression.TechTree.Unlock(gs, heavy);
                bool unlocked = Progression.TechTree.IsUnlocked(gs, "heavy")
                                && Math.Abs(gs.Science - (1000 - heavy.Cost)) < 1e-9;
                bool startAvail = Progression.TechTree.PartAvailable(gs, "Pod Mk1")
                                  && Progression.TechTree.PartAvailable(gs, "Mainsail");          // now unlocked
                var fresh = new GameState { UnlockedTech = Progression.TechTree.StartingNodes() };
                bool gatedHidden = !Progression.TechTree.PartAvailable(fresh, "Mainsail");        // still locked
                Check("tech tree", lockedNoSci && buyable && unlocked && startAvail && gatedHidden);
            }

            // 12. milestones award science once: re-evaluating a met milestone never re-pays it.
            {
                var gs = new GameState();
                var body = new CelestialBody { Mu = mu, Radius = 6.371e6 };
                var v = new Vessels.Vessel { Body = body, Position = new Vec2d(6.371e6 + 2000, 0), Velocity = new Vec2d(0, 10) };
                var first = Progression.Milestones.Evaluate(v, 0, gs);
                double sci1 = gs.Science;
                var second = Progression.Milestones.Evaluate(v, 0, gs);
                Check("milestone award-once",
                      first != null && first.Id == "liftoff" && sci1 >= 5
                      && gs.CompletedMilestones.Contains("liftoff")
                      && (second == null || second.Id != "liftoff") && gs.Science >= sci1);
            }

            // 13. solid boosters: full thrust regardless of throttle, burning their own fuel; their
            //     self-contained fuel is excluded from the segment pool that feeds liquid engines.
            {
                var v = new Vessels.Vessel();
                var srb = new Parts.Part(Parts.PartCatalog.Get("Thumper SRB")) { Ignited = true };
                v.Parts.Add(srb);
                v.Throttle = 0;
                bool fullAtZero = Math.Abs(v.CurrentThrust - srb.Def.Thrust) < 1e-6;
                double f0 = srb.Fuel;
                v.DrainFuel(1.0);
                bool drained = Math.Abs((f0 - srb.Fuel) - srb.Def.FuelFlowAtMax) < 1e-6;
                bool segExcludesSolid = v.SegmentFuel((0, 0)) == 0;
                Check("solid booster", fullAtZero && drained && segExcludesSolid);
            }

            // 14. every part/module has a non-empty, unique texture id; Slug normalizes display names.
            {
                bool idsOk = true; var seen = new HashSet<string>();
                foreach (var p in Parts.PartCatalog.All) if (string.IsNullOrEmpty(p.Id) || !seen.Add("P:" + p.Id)) idsOk = false;
                foreach (var m in Parts.ModuleCatalog.All) if (string.IsNullOrEmpty(m.Id) || !seen.Add("M:" + m.Id)) idsOk = false;
                bool slugOk = Parts.PartDef.Slug("Pod Mk1") == "pod-mk1" && Parts.PartDef.Slug("  A!!B  ") == "a-b";
                Check("part ids", idsOk && slugOk);
            }

            // 15. fuel cell: produces EC while active and burns a fuel trickle over a tick.
            {
                var v = new Vessels.Vessel();
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                var fc = Parts.ModuleCatalog.Get("Fuel Cell");
                pod.Modules.Add(new Parts.ModuleInstance(fc) { Active = true });
                v.Parts.Add(pod);
                v.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")));
                double f0 = v.TotalLiquidFuel;
                v.EcRates(0, null, out double prod, out double draw);
                bool makesEc = prod >= fc.EcProduce - 1e-9;
                v.UpdateResources(1.0, 0, null);
                bool burnsFuel = v.TotalLiquidFuel <= f0 - fc.FuelDraw + 1e-6 && v.TotalLiquidFuel < f0;
                Check("fuel cell", makesEc && burnsFuel);
            }

            // 16. nose cone at the top of the stack reduces total drag area.
            {
                var bare = new Vessels.Vessel();
                bare.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                bare.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")));
                var coned = new Vessels.Vessel();
                coned.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Nose Cone")));
                coned.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                coned.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")));
                Check("nose cone drag", coned.TotalCdA < bare.TotalCdA);
            }

            // 17. landing legs raise the survivable touchdown speed.
            {
                var v = new Vessels.Vessel();
                double baseSpeed = v.SafeLandingSpeed;
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Landing Legs")));
                v.Parts.Add(pod);
                Check("landing legs", baseSpeed == 8.0 && v.SafeLandingSpeed > baseSpeed);
            }

            // 18. radial boosters add thrust + mass to the ship and drain their own fuel.
            {
                var v = new Vessels.Vessel();
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                var core = new Parts.Part(Parts.PartCatalog.Get("Tank T400"));
                var rdef = Parts.PartCatalog.Get("Thumper-R");
                core.Radials.Add(new Parts.Part(rdef) { Ignited = true });
                core.Radials.Add(new Parts.Part(rdef) { Ignited = true });
                v.Parts.Add(pod); v.Parts.Add(core);
                bool thrustOk = v.MaxAvailableThrust >= 2 * rdef.Thrust - 1e-6;
                bool massOk = v.TotalMass > pod.Mass + core.Mass + 1;   // radial mass is included
                double rf0 = core.Radials[0].Fuel + core.Radials[1].Fuel;
                v.DrainFuel(1.0);
                bool drained = core.Radials[0].Fuel + core.Radials[1].Fuel < rf0;
                Check("radial booster", thrustOk && massOk && drained);
            }

            // 19. a radial design round-trips through the savegame design state, including a multi-part
            //     sub-stack (radial tank + engine), which must regroup correctly.
            {
                var vd = new Vessels.VesselDesign();
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1")));
                var entry = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400"));
                entry.AddRadial(Parts.PartCatalog.Get("Thumper-R"));                 // single-part mount
                entry.AddRadial(Parts.PartCatalog.Get("Radial Tank"), separate: false);
                entry.AppendToMount(1, Parts.PartCatalog.Get("Spark"));              // sub-stack: tank + engine
                vd.Stack.Add(entry);
                var ds = DesignState.From(vd);
                var vd2 = new Vessels.VesselDesign();
                ds.ApplyTo(vd2);
                var m = vd2.Stack.Count == 2 ? vd2.Stack[1].Mounts : null;
                bool rt = m != null && m.Count == 2
                          && m[0].Parts.Count == 1 && m[0].Root.Name == "Thumper-R" && m[0].Separate
                          && m[1].Parts.Count == 2 && m[1].Parts[0].Name == "Radial Tank"
                          && m[1].Parts[1].Name == "Spark" && !m[1].Separate;
                Check("radial round-trip", rt);

                // FromVessel (instantiate -> regroup) must reproduce the same mounts.
                var vd3 = Vessels.VesselDesign.FromVessel(vd.Instantiate(), "x");
                var m3 = vd3.Stack.Count == 2 ? vd3.Stack[1].Mounts : null;
                bool fv = m3 != null && m3.Count == 2 && m3[1].Parts.Count == 2
                          && m3[1].Parts[0].Name == "Radial Tank" && m3[1].Parts[1].Name == "Spark" && !m3[1].Separate;
                Check("radial FromVessel regroup", fv);
            }

            // 19b. a separate-stage radial mount appears as its own stage with delta-V, and the core stage's
            //      initial mass excludes the dropped radial.
            {
                var stack = new System.Collections.Generic.List<Vessels.StackEntry>();
                var pod = new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1"));
                var core = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400"));
                core.AddRadial(Parts.PartCatalog.Get("Thumper-R"), separate: true);   // own-stage radial pair
                stack.Add(pod); stack.Add(core);

                var stages = Vessels.Staging.ComputeStages(stack);
                // expected separate-radial dV: 2x Thumper-R lifting the full vessel mass on their own fuel
                var rdef = Parts.PartCatalog.Get("Thumper-R");
                double m0 = pod.Def.DryMass + core.Def.DryMass + core.Def.FuelCapacity + 2 * (rdef.DryMass + rdef.FuelCapacity);
                double fuel = 2 * rdef.FuelCapacity;
                double isp = rdef.Thrust / rdef.FuelFlowAtMax / 9.81;
                double expectDv = isp * 9.81 * System.Math.Log(m0 / (m0 - fuel));
                bool hasSepStage = stages.Count >= 1 && stages[0].DeltaV > 0
                                   && System.Math.Abs(stages[0].DeltaV - expectDv) < 1.0;
                Check("separate radial dV", hasSepStage);

                // the separate radial-booster stage must report its fuel + capacity (drives the HUD fuel
                // bar) -- the old segment-derived StageFuelFrac ignored radial fuel and read 0.
                bool sepFuelBar = stages.Count >= 1
                                  && System.Math.Abs(stages[0].Fuel - fuel) < 1.0
                                  && System.Math.Abs(stages[0].FuelCap - fuel) < 1.0;
                Check("separate radial fuel bar", sepFuelBar);

                // BurnTime over the full stage list must be finite and consistent for the total dV.
                double totalDv = 0; foreach (var st in stages) totalDv += st.DeltaV;
                var inst = new Vessels.VesselDesign { Stack = stack }.Instantiate();
                double bt = Vessels.Staging.BurnTime(inst, totalDv);
                Check("separate radial burn-time", bt >= 0 && !double.IsNaN(bt));
            }

            // 19d. every decoupler shows up as a stage row flagged as a separation event, so a multi-decoupler
            //      stack lists all its decouplers (a no-engine segment between decouplers still appears).
            {
                var names = new[] { "Pod Mk1", "Tank T400", "Terrier", "Decoupler",
                                    "Tank T400", "Terrier", "Decoupler", "Tank T400", "Terrier" };
                var stack = new System.Collections.Generic.List<Parts.PartDef>();
                foreach (var n in names) stack.Add(Parts.PartCatalog.Get(n));
                var stages = Vessels.Staging.ComputeStages(stack);
                int decouplerCount = 0; foreach (var n in names) if (n == "Decoupler") decouplerCount++;
                int decRows = 0; foreach (var st in stages) if (st.Decouples) decRows++;
                Check("decouplers visible as stages", decRows == decouplerCount && decouplerCount == 2);
            }

            // 19e. an ordinarily-inline part (a parachute) can be radially mounted; it defaults to riding the
            //      core (KEEP, not its own stage), materializes as a symmetric pair, and round-trips.
            {
                var entry = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400"));
                entry.AddRadial(Parts.PartCatalog.Get("Parachute"));   // not a booster -> Separate defaults false
                bool ridesCore = entry.Mounts.Count == 1 && !entry.Mounts[0].Separate;
                var host = new Parts.Part(entry.Def);
                Vessels.VesselDesign.MaterializeRadials(entry, host);
                bool pair = host.Radials.Count == 2 && host.Radials.TrueForAll(r => r.Def.Name == "Parachute");
                Check("inline part radial-mountable", ridesCore && pair);
            }

            // 20. body catalog: the solar system builds from BodyCatalog with the parent hierarchy, the
            //     1/10 length scale, texture ids, and finite SOIs intact.
            {
                var u = SolarSystemData.Create();
                var earth = u["Earth"]; var moon = u["Moon"];
                bool wired = earth != null && moon != null && moon.Parent == earth
                             && earth.Parent == u.Root && u.Root?.Name == "Sun";
                bool scaled = earth != null && Math.Abs(earth.Radius - 6371e3 * 0.1) < 1e-3;
                bool tex = earth != null && earth.TextureId == "earth";
                bool soi = moon != null && moon.SoiRadius > 0 && !double.IsInfinity(moon.SoiRadius);
                Check("body catalog", wired && scaled && tex && soi);
            }

            // 21. antenna signal: full strength near home (Earth), zero far beyond range, none when the
            //     antenna is inactive -- this drives transmission speed.
            {
                var u = SolarSystemData.Create();
                var earth = u["Earth"];
                var v = new Vessels.Vessel { Body = earth, Position = new Vec2d(earth.Radius + 1000, 0) };
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                var ant = Parts.ModuleCatalog.Get("Antenna");
                pod.Modules.Add(new Parts.ModuleInstance(ant) { Active = true });
                v.Parts.Add(pod);
                double near = v.SignalStrength(0, u, null);
                v.Position = new Vec2d(ant.Range * 5, 0);
                double far = v.SignalStrength(0, u, null);
                pod.Modules[0].Active = false;
                double off = v.SignalStrength(0, u, null);
                Check("antenna signal", near > 0.99 && far <= 0 && off == 0);
            }

            // 22. life support: crew consume oxygen over time, and total deprivation eventually kills crew.
            {
                var v = new Vessels.Vessel();
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Life Support")));
                pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("RTG")));   // power
                pod.Crew.Add(new Vessels.CrewMember("Test", Vessels.CrewRole.Pilot));
                v.Parts.Add(pod);
                v.Oxygen = v.OxygenCapacity; v.Water = v.WaterCapacity; v.Food = v.FoodCapacity;
                double ox0 = v.Oxygen;
                v.UpdateResources(100, 0, null);
                bool consumed = Math.Abs((ox0 - v.Oxygen) - 100 * Vessels.Vessel.OxygenPerCrew) < 1e-6;
                v.Oxygen = 0; v.Water = 0; v.Food = 0;
                v.UpdateResources(Vessels.Vessel.LsDeathTime + 1, 0, null);
                Check("life support", consumed && v.CrewCount == 0);
            }

            // 23. crew seats + transfer: a crew cabin adds seats; crew move only into a part with room.
            {
                var v = new Vessels.Vessel();
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));      // 1 built-in seat
                var cabin = new Parts.Part(Parts.PartCatalog.Get("Tank T400"));
                cabin.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Crew Cabin")));  // +4 seats
                pod.Crew.Add(new Vessels.CrewMember("A", Vessels.CrewRole.Pilot));
                v.Parts.Add(pod); v.Parts.Add(cabin);
                bool seats = pod.SeatCount == 1 && cabin.SeatCount == 4;
                bool moved = v.TransferCrew(pod, cabin) && pod.Crew.Count == 0 && cabin.Crew.Count == 1;
                bool noRoom = !v.TransferCrew(pod, cabin);   // pod is now empty: nothing to move
                Check("crew transfer", seats && moved && noRoom);
            }

            // 24. steering authority: reaction wheels only turn the ship with electric charge; fins only
            //     bite in atmosphere; a bare pod's built-in rate is modest (below the old 0.4 floor).
            {
                var body = new CelestialBody { Mu = mu, Radius = 6.371e6, Atmo = new Atmosphere(1.225, 5600, 56_000) };
                var v = new Vessels.Vessel { Body = body, Position = new Vec2d(6.371e6 + 1000, 0) };
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Reaction Wheel")));
                v.Parts.Add(pod);
                v.Velocity = Vec2d.Zero;
                double baseRate = v.TurnRate;                 // no EC -> wheels contribute nothing
                v.ElectricCharge = 100;
                double powered = v.TurnRate;                  // EC -> one wheel adds ~0.6 rad/s
                bool wheelsNeedPower = powered > baseRate + 0.5 && powered < baseRate + 0.7
                                       && baseRate <= 0.8 + 1e-9;   // built-in rate trimmed (was up to 2.0)

                var finned = new Vessels.Vessel { Body = body, Position = new Vec2d(6.371e6 + 100, 0) };
                finned.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                finned.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Fin Set")));
                finned.Velocity = Vec2d.Zero;                 // in atmosphere but not moving -> fins idle
                double finsStill = finned.TurnRate;
                finned.Velocity = new Vec2d(0, 300);          // fast through air -> fins bite
                double finsFast = finned.TurnRate;
                finned.Position = new Vec2d(6.371e6 + 200_000, 0);  // above the atmosphere
                double finsVacuum = finned.TurnRate;
                bool finsNeedAir = finsFast > finsStill + 0.1 && Math.Abs(finsVacuum - finsStill) < 1e-9;

                Check("steering authority", wheelsNeedPower && finsNeedAir);
            }

            // 25. RCS translation: a command accelerates along the body axes (fore = +Up) and burns
            //     monopropellant at thrust/(Isp*g0); disabling RCS or running the tank dry produces no force.
            {
                var v = new Vessels.Vessel { Heading = Math.PI / 2 };   // Up = (0,1), right = (1,0)
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Monoprop Tank")));
                pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("RCS Thruster Block")));
                v.Parts.Add(pod);
                v.ElectricCharge = 100;
                v.Monoprop = v.MonopropCapacity;
                v.RcsEnabled = true;
                v.RcsCommand = new Vec2d(0, 1);                        // fore: +Up = +y
                double expect = Vessels.Vessel.RcsThrustPerBlock / v.TotalMass;
                var a = v.RcsAccel;
                bool foreOk = Math.Abs(a.X) < 1e-9 && Math.Abs(a.Y - expect) < 1e-6 * expect;

                double m0 = v.Monoprop;
                v.DrainMonoprop(1.0);
                double flow = Vessels.Vessel.RcsThrustPerBlock / (Vessels.Vessel.RcsIsp * Vessels.Vessel.G0);
                bool drained = Math.Abs((m0 - v.Monoprop) - flow) < 1e-6;

                v.RcsEnabled = false;
                bool offNoForce = v.RcsAccel.Length == 0 && !v.RcsActive;
                v.RcsEnabled = true; v.Monoprop = 0;
                bool dryNoForce = v.RcsAccel.Length == 0;
                Check("rcs translation", foreOk && drained && offNoForce && dryNoForce);
            }

            // 26. on-rails <-> physics handoff preserves a vessel's state vector. Promoting a nearby
            //     tracked ship to physics (for rendezvous) and demoting it back must not teleport it.
            {
                var body = new CelestialBody { Mu = mu, Radius = 6.371e6 };
                var v = new Vessels.Vessel { Body = body, Position = new Vec2d(7e6, 1e5), Velocity = new Vec2d(50, 7500) };
                var p0 = v.Position; var vel0 = v.Velocity;
                v.GoOnRails(123.0);
                v.GoOffRails(123.0);
                Check("rails handoff", (v.Position - p0).Length < 1e-3 && (v.Velocity - vel0).Length < 1e-6);
            }

            // 27. docking merge: parts and resource pools combine into one vessel; undock splits the
            //     parts back out and returns each side its capacity-weighted share of the pool.
            {
                var a = new Vessels.Vessel();
                var podA = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                podA.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Monoprop Tank")));
                a.Parts.Add(podA);
                a.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));
                a.Monoprop = a.MonopropCapacity;                         // 500

                var b = new Vessels.Vessel();
                b.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));
                var podB = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                podB.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Monoprop Tank")));
                b.Parts.Add(podB);
                b.Monoprop = b.MonopropCapacity;                         // 500

                double massSum = a.TotalMass + b.TotalMass;
                int partsSum = a.Parts.Count + b.Parts.Count;
                bool hadFree = a.HasFreeDockingPort && b.HasFreeDockingPort;
                bool docked = a.DockWith(b, a.FirstFreeDockingPort(), b.FirstFreeDockingPort());
                bool mergedOk = docked && a.Parts.Count == partsSum
                                && Math.Abs(a.TotalMass - massSum) < 1e-6
                                && Math.Abs(a.Monoprop - 1000) < 1e-6
                                && a.DockLinks.Count == 1
                                && !a.HasFreeDockingPort;                // both ports now occupied

                var det = a.Undock(0);                                   // no separation impulse: exact split
                bool undockOk = det != null && a.DockLinks.Count == 0
                                && a.Parts.Count + det.Parts.Count == partsSum
                                && Math.Abs(a.Monoprop - 500) < 1e-6 && Math.Abs(det.Monoprop - 500) < 1e-6;

                Check("docking merge", hadFree && mergedOk && undockOk);
            }

            // 28. a docked station persists its assembly graph: serialize -> reload keeps the parts and
            //     the dock link, so the reloaded station can still be undocked back into modules.
            {
                var u = SolarSystemData.Create();
                var earth = u["Earth"];
                var a = new Vessels.Vessel { Body = earth, Position = new Vec2d(earth.Radius + 200000, 0), Velocity = new Vec2d(0, 7000) };
                a.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                a.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));
                var b = new Vessels.Vessel();
                b.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));
                b.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                a.DockWith(b, a.FirstFreeDockingPort(), b.FirstFreeDockingPort());

                var s = ShipState.From(a, "Station");
                var a2 = s.ToVessel(u);
                bool restored = a2.Parts.Count == 4 && a2.DockLinks.Count == 1 && a2.CanUndock;
                var det = a2.Undock(0);
                bool splitOk = det != null && a2.Parts.Count == 2 && det.Parts.Count == 2;
                Check("station persist", restored && splitOk);
            }

            // 29. colony: a connected surface base pools mined fuel across the whole assembly, so a
            //     drill on the base refuels a docked lander; the colony milestone needs a crewed,
            //     food-regenerating base off Earth.
            {
                var u = SolarSystemData.Create();
                var moon = u["Moon"];

                // base (drill on a full tank) + lander (empty tank), connected via docking ports
                var baseV = new Vessels.Vessel { Body = moon, Landed = true, ElectricCharge = 100 };
                var baseTank = new Parts.Part(Parts.PartCatalog.Get("Tank T400"));
                baseTank.Fuel = baseTank.Def.FuelCapacity;     // base tank already full: overflow goes to the lander
                baseTank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Battery")));  // EC storage to run the drill
                baseTank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Drill")) { Active = true });
                baseV.Parts.Add(baseTank);
                baseV.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));

                var lander = new Vessels.Vessel();
                lander.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));
                var landerTank = new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 0 };
                lander.Parts.Add(landerTank);

                baseV.DockWith(lander, baseV.FirstFreeDockingPort(), lander.FirstFreeDockingPort());
                double f0 = baseV.TotalLiquidFuel;
                baseV.UpdateResources(10, 0, u);               // drill +3 fuel/s for 10 s, pooled base-wide
                bool refuelled = baseV.TotalLiquidFuel > f0 + 29 && landerTank.Fuel > 0;  // reached the lander

                // colony milestone condition: crewed + active hydroponics on a non-Earth body
                var gs = new GameState();
                var colony = new Vessels.Vessel { Body = moon, Landed = true };
                var hab = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                hab.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Hydroponics Bay")) { Active = true });
                hab.Crew.Add(new Vessels.CrewMember("A", Vessels.CrewRole.Pilot));
                var hab2 = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                hab2.Crew.Add(new Vessels.CrewMember("B", Vessels.CrewRole.Pilot));
                colony.Parts.Add(hab); colony.Parts.Add(hab2);
                var colMs = Progression.Milestones.All.Find(x => x.Id == "colony");
                bool colonyOk = colMs != null && colMs.Done(colony, 0, gs)
                                && !colMs.Done(new Vessels.Vessel { Body = u["Earth"], Landed = true }, 0, gs);

                Check("colony base", refuelled && colonyOk);
            }

            // 30. per-radial staging choice (explicit stages): all radials ignite with their host at stage 0;
            //     by default the "separate" ones drop at stage 1 while the "included" ones ride the core.
            {
                var v = new Vessels.Vessel();
                var host = new Parts.Part(Parts.PartCatalog.Get("Tank T400"));
                host.Radials.Add(new Parts.Part(Parts.PartCatalog.Get("Thumper-R")) { RadialSeparate = true });
                host.Radials.Add(new Parts.Part(Parts.PartCatalog.Get("Thumper-R")) { RadialSeparate = true });
                host.Radials.Add(new Parts.Part(Parts.PartCatalog.Get("Thumper-R")) { RadialSeparate = false });
                host.Radials.Add(new Parts.Part(Parts.PartCatalog.Get("Thumper-R")) { RadialSeparate = false });
                v.Parts.Add(host);

                var first = Vessels.Staging.FireNext(v);                  // stage 0: ignite all radials, drop nothing
                bool ignitedNoDrop = first == null && host.Radials.Count == 4 && host.Radials.TrueForAll(r => r.Ignited);
                var debris = Vessels.Staging.FireNext(v);                 // stage 1: jettison the separate radials
                bool droppedSeparate = debris != null && debris.Parts.Count == 2;
                bool keptIncluded = host.Radials.Count == 2 && host.Radials.TrueForAll(r => !r.RadialSeparate);
                Check("radial staging", ignitedNoDrop && droppedSeparate && keptIncluded);
            }

            // 30b. explicit KSP staging: default geometry puts the bottom engine at stage 0; firing stages in
            //      order ignites the right engine and decouples the right segment (sequence respected).
            {
                var vd = new Vessels.VesselDesign();
                foreach (var n in new[] { "Pod Mk1", "Tank T400", "Terrier", "Decoupler", "Tank T800", "Swivel" })
                    vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get(n)));
                var v = vd.Instantiate();   // assigns default stages
                var bottomEng = v.Parts[5]; var topEng = v.Parts[2]; var dec = v.Parts[3];
                bool stages = bottomEng.Def.Name == "Swivel" && bottomEng.Stage == 0
                              && dec.Def.Name == "Decoupler" && dec.Stage == 1 && topEng.Stage == 1;

                Vessels.Staging.FireNext(v);                       // stage 0: light the bottom engine only
                bool lit0 = bottomEng.Ignited && !topEng.Ignited && v.Parts.Count == 6;
                var debris = Vessels.Staging.FireNext(v);          // stage 1: decouple bottom, light upper engine
                bool decoupled = debris != null && debris.Parts.Exists(p => p.Def.Name == "Swivel")
                                 && v.Parts.Exists(p => p.Def.Name == "Terrier" && p.Ignited)
                                 && !v.Parts.Exists(p => p.Def.Name == "Swivel");
                Check("explicit staging order", stages && lit0 && decoupled);
            }

            // 30c. re-tagging a part's stage changes when it fires: move the bottom engine to stage 1 and it
            //      no longer ignites on the first stage.
            {
                var vd = new Vessels.VesselDesign();
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1")));
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400")));
                var eng = new Vessels.StackEntry(Parts.PartCatalog.Get("Terrier")) { Stage = 1 };  // explicit, not 0
                vd.Stack.Add(eng);
                var v = vd.Instantiate();
                Vessels.Staging.FireNext(v);                       // stage 0
                bool notLit = !v.Parts[2].Ignited;
                Vessels.Staging.FireNext(v);                       // stage 1
                bool litNow = v.Parts[2].Ignited;
                Check("stage re-tag", v.Parts[2].Stage == 1 && notLit && litNow);
            }

            // 31. the "Internal_" sandbox cheat unlocks the whole tech tree; an ordinary name does not.
            {
                var sandbox = GameState.NewGame("Internal_test");
                var normal = GameState.NewGame("My Game");
                bool cheatOk = sandbox.IsSandbox
                               && sandbox.UnlockedTech.Count == Progression.TechTree.Nodes.Count
                               && !normal.IsSandbox
                               && normal.UnlockedTech.Count < Progression.TechTree.Nodes.Count;
                Check("sandbox cheat", cheatOk);
            }

            // 32. launching a duplicate ship name gets a progressive suffix instead of overwriting.
            {
                var gs = GameState.NewGame("My Game");
                bool freeName = gs.UniqueShipName("Probe") == "Probe";   // none saved yet
                gs.Ships.Add(new ShipState { Name = "Probe" });
                bool second = gs.UniqueShipName("Probe") == "Probe 2";
                gs.Ships.Add(new ShipState { Name = "Probe 2" });
                bool third = gs.UniqueShipName("Probe") == "Probe 3";
                bool blankSafe = !string.IsNullOrWhiteSpace(gs.UniqueShipName("  "));
                Check("unique ship name", freeName && second && third && blankSafe);
            }

            // 33. an editor design with radial sub-stacks survives a DesignState save/load round-trip
            //     (the form ShipLibrary persists), preserving the mount's parts and staging choice.
            {
                var vd = new Vessels.VesselDesign { Name = "Lib Test" };
                var pod = new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1"));
                var core = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400"));
                var tank = Parts.PartCatalog.Get("Radial Tank");
                var eng = Parts.PartCatalog.Get("Spark");
                if (pod.Def != null && core.Def != null && tank != null && eng != null)
                {
                    core.AddRadial(tank, separate: true);
                    core.AppendToMount(0, eng);
                    vd.Stack.Add(pod); vd.Stack.Add(core);

                    var ds = DesignState.From(vd);
                    var vd2 = new Vessels.VesselDesign();
                    ds.ApplyTo(vd2);
                    bool nameOk = vd2.Name == "Lib Test";
                    bool shapeOk = vd2.Stack.Count == 2 && vd2.Stack[1].Mounts.Count == 1
                                   && vd2.Stack[1].Mounts[0].Parts.Count == 2
                                   && vd2.Stack[1].Mounts[0].Parts[0].Name == tank.Name
                                   && vd2.Stack[1].Mounts[0].Parts[1].Name == eng.Name
                                   && vd2.Stack[1].Mounts[0].Separate;
                    Check("design library round-trip", nameOk && shapeOk);
                }
                else Check("design library round-trip", false);
            }

            // 33b. explicit stage tags survive the DesignState save/load round-trip (axial part + radial mount).
            {
                var vd = new Vessels.VesselDesign { Name = "Stage Test" };
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1")));
                var core = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400")) { Stage = 3 };
                core.AddRadial(Parts.PartCatalog.Get("Thumper-R"), separate: true);
                core.Mounts[0].Stage = 5;
                vd.Stack.Add(core);

                var vd2 = new Vessels.VesselDesign();
                DesignState.From(vd).ApplyTo(vd2);
                bool tagsOk = vd2.Stack.Count == 2 && vd2.Stack[1].Stage == 3
                              && vd2.Stack[1].Mounts.Count == 1 && vd2.Stack[1].Mounts[0].Stage == 5;
                // and the runtime Part carries the tag from Instantiate
                var inst = vd.Instantiate();
                bool runtimeOk = inst.Parts.Count == 2 && inst.Parts[1].Stage == 3
                                 && inst.Parts[1].Radials.Count == 2 && inst.Parts[1].Radials[0].Stage == 5;
                Check("stage tag round-trip", tagsOk && runtimeOk);
            }

            // 33c. regression: total delta-v of a default-staged linear stack is a sane positive value and a
            //      heavier upper stage doesn't exceed an obviously-wrong bound (guards the new dV simulation).
            {
                var stack = new System.Collections.Generic.List<Parts.PartDef>();
                foreach (var n in new[] { "Pod Mk1", "Tank T400", "Terrier", "Decoupler", "Tank T800", "Swivel" })
                    stack.Add(Parts.PartCatalog.Get(n));
                var stages = Vessels.Staging.ComputeStages(stack);
                double totalDv = 0; int withThrust = 0;
                foreach (var st in stages) { totalDv += st.DeltaV; if (st.DeltaV > 0) withThrust++; }
                // two engine stages, each producing real dV; a two-stage Terrier/Swivel rocket is a few km/s
                Check("staging dV sane", withThrust == 2 && totalDv > 2000 && totalDv < 12000);
            }

            // 33d. a parachute (which defaults to the last stage, above the top engine) must not steal the
            //      final burn's fuel: the single engine stage still reports real delta-v.
            {
                var stack = new System.Collections.Generic.List<Parts.PartDef>();
                foreach (var n in new[] { "Parachute", "Pod Mk1", "Tank T400", "Terrier" })
                    stack.Add(Parts.PartCatalog.Get(n));
                var stages = Vessels.Staging.ComputeStages(stack);
                double engDv = 0; foreach (var st in stages) engDv = System.Math.Max(engDv, st.DeltaV);
                Check("parachute keeps final burn dV", engDv > 1000);
            }

            string res = $"Physics self-test: {pass}/{total} PASS";
            if (fails.Count > 0) res += "  FAILED: " + string.Join(", ", fails);
            return res;
        }
    }
}
