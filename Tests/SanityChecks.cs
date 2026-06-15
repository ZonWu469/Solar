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
            // The catalogs are the sole source of truth (Content/parts.json + modules.json). --selftest
            // returns before SolarGame initializes, so load them here or every part/module test sees an
            // empty catalog. Idempotent re-load at normal startup.
            Parts.PartCatalog.Load();
            Parts.ModuleCatalog.Load();

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

            // 6b. finite-burn projection: a very short, high-thrust burn converges to the impulsive
            //     maneuver result, and a sustained prograde burn raises apoapsis while retrograde lowers it.
            {
                var el = new OrbitalElements { A = 7e6, E = 0.1, ArgPe = 0.7, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                double tPe = Kepler.TimeAtTrueAnomaly(el, 0, 0);
                var (r, v) = Kepler.StateAtTime(el, tPe);
                const double dv = 50;
                Vec2d proDir = v.Normalized();
                // high thrust + negligible mass flow -> effectively impulsive
                var fin = BurnProjector.Project(r, v, mu, tPe, 5e6, 1e-3, 1000, 1e9, dv, (rr, vv) => proDir, out _);
                var imp = new Maneuver { UT = tPe, Prograde = dv, Radial = 0 }.ResultOrbit(el, mu);
                bool converge = Math.Abs(fin.Apoapsis - imp.Apoapsis) < 0.005 * imp.Apoapsis;

                // a sustained finite burn (real mass loss) still moves apoapsis the right way
                var up = BurnProjector.Project(r, v, mu, tPe, 3e4, 10, 1000, 1e9, 100, (rr, vv) => vv.Normalized(), out _);
                var down = BurnProjector.Project(r, v, mu, tPe, 3e4, 10, 1000, 1e9, 100, (rr, vv) => (-vv).Normalized(), out _);
                bool dirOk = up.Apoapsis > el.Apoapsis + 1 && down.Apoapsis < el.Apoapsis - 1;

                Check("finite burn projection", converge && dirOk);
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
                bool startAvail = Progression.TechTree.PartAvailable(gs, "pod-mk1")
                                  && Progression.TechTree.PartAvailable(gs, "mainsail");          // now unlocked
                var fresh = new GameState { UnlockedTech = Progression.TechTree.StartingNodes() };
                bool gatedHidden = !Progression.TechTree.PartAvailable(fresh, "mainsail");        // still locked
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

            // 17. a bare hull survives only a gentle touchdown; landing legs (module) and landing-gear
            //     parts both raise the survivable speed, the heavier gear (authored ImpactTolerance) more.
            //     The verdict has a small tolerance, so an impact at exactly the rating still lands.
            {
                var v = new Vessels.Vessel();
                double baseSpeed = v.SafeLandingSpeed;
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Landing Legs")));
                v.Parts.Add(pod);
                bool legsHelp = baseSpeed == Vessels.Vessel.BareLandingSpeed && v.SafeLandingSpeed > baseSpeed;

                var geared = new Vessels.Vessel();
                geared.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                geared.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Landing Gear")));
                var heavyGeared = new Vessels.Vessel();
                heavyGeared.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                heavyGeared.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Heavy Landing Gear")));
                bool gearHelps = geared.SafeLandingSpeed > baseSpeed
                                 && heavyGeared.SafeLandingSpeed > geared.SafeLandingSpeed
                                 && geared.SafeLandingSpeed >= 20.0;   // a legged lander clears a real touchdown

                // verdict: a touch at the rating (or a hair under) lands; clearly above crashes.
                var bare = new Vessels.Vessel();
                bare.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                bool verdict = bare.SurvivesTouchdown(bare.SafeLandingSpeed)
                               && bare.SurvivesTouchdown(bare.SafeLandingSpeed - 0.5)
                               && !bare.SurvivesTouchdown(bare.SafeLandingSpeed + 1.0);

                Check("landing tolerance", legsHelp && gearHelps && verdict);
            }

            // 17b. parachutes carry per-part deployed drag (authored DeployedCdA): a deployed chute raises
            //      TotalCdA, and a Large Parachute adds more than a Drogue Chute.
            {
                Vessels.Vessel Chute(string name, bool deploy)
                {
                    var vv = new Vessels.Vessel();
                    vv.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                    var c = new Parts.Part(Parts.PartCatalog.Get(name)) { Deployed = deploy };
                    vv.Parts.Add(c);
                    return vv;
                }
                double drogue = Chute("Drogue Chute", true).TotalCdA;
                double large = Chute("Large Parachute", true).TotalCdA;
                double largeStowed = Chute("Large Parachute", false).TotalCdA;
                Check("parachute drag", large > drogue && large > largeStowed);
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

            // 19c. a radial engine ignites at its own fire stage, independent of the host axial part, so a
            //      radial booster and an axial engine can be put in different stages (and in either order).
            {
                // forward: radial fires S0, axial engine fires S1
                var v = new Vessels.Vessel();
                v.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var core = new Parts.Part(Parts.PartCatalog.Get("Tank T400"));
                var axial = new Parts.Part(Parts.PartCatalog.Get("Terrier")) { Stage = 1 };
                var rad = new Parts.Part(Parts.PartCatalog.Get("Thumper-R")) { RadialSeparate = false, FireStage = 0 };
                core.Radials.Add(rad);
                v.Parts.Add(core); v.Parts.Add(axial);
                Vessels.Staging.FireNext(v);                       // stage 0
                bool fwd0 = rad.Ignited && !axial.Ignited;
                Vessels.Staging.FireNext(v);                       // stage 1
                bool fwd1 = axial.Ignited;

                // reverse: axial fires S0, radial fires S1
                var v2 = new Vessels.Vessel();
                v2.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var core2 = new Parts.Part(Parts.PartCatalog.Get("Tank T400"));
                var axial2 = new Parts.Part(Parts.PartCatalog.Get("Terrier")) { Stage = 0 };
                var rad2 = new Parts.Part(Parts.PartCatalog.Get("Thumper-R")) { RadialSeparate = false, FireStage = 1 };
                core2.Radials.Add(rad2);
                v2.Parts.Add(core2); v2.Parts.Add(axial2);
                Vessels.Staging.FireNext(v2);                      // stage 0
                bool rev0 = axial2.Ignited && !rad2.Ignited;
                Vessels.Staging.FireNext(v2);                      // stage 1
                bool rev1 = rad2.Ignited;

                Check("radial fire order", fwd0 && fwd1 && rev0 && rev1);
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

            // 24. attitude authority (torque/inertia model): reaction wheels add control torque (and raise
            //     the rate cap) only with electric charge; fins only bite in atmosphere; the command pod's
            //     authored ControlAuthority guarantees the same minimum angular accel at any mass, and a
            //     fixed-torque reaction wheel boosts the lighter craft more.
            {
                var body = new CelestialBody { Mu = mu, Radius = 6.371e6, Atmo = new Atmosphere(1.225, 5600, 56_000) };
                var v = new Vessels.Vessel { Body = body, Position = new Vec2d(6.371e6 + 1000, 0) };
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Reaction Wheel")));
                v.Parts.Add(pod);
                v.Velocity = Vec2d.Zero;
                double baseTorque = v.ControlTorque, baseCap = v.MaxTurnRate;   // no EC -> wheels idle
                v.ElectricCharge = 100;
                double poweredTorque = v.ControlTorque, poweredCap = v.MaxTurnRate;
                bool wheelsNeedPower = poweredTorque > baseTorque + 40000 && poweredTorque < baseTorque + 50000
                                       && poweredCap > baseCap + 0.1 && poweredCap <= 1.6 + 1e-9;

                var finned = new Vessels.Vessel { Body = body, Position = new Vec2d(6.371e6 + 100, 0) };
                finned.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                finned.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Fin Set")));
                finned.Velocity = Vec2d.Zero;                 // in atmosphere but not moving -> fins idle
                double finsStill = finned.ControlTorque;
                finned.Velocity = new Vec2d(0, 300);          // fast through air -> fins bite
                double finsFast = finned.ControlTorque;
                finned.Position = new Vec2d(6.371e6 + 200_000, 0);  // above the atmosphere
                double finsVacuum = finned.ControlTorque;
                bool finsNeedAir = finsFast > finsStill + 100 && Math.Abs(finsVacuum - finsStill) < 1e-9;

                var light = new Vessels.Vessel { Body = body };
                light.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var heavy = new Vessels.Vessel { Body = body };
                heavy.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                for (int i = 0; i < 6; i++) heavy.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T800")));
                // pod-only: the guaranteed minimum angular accel is the same regardless of mass
                double accLight = light.ControlTorque / light.MomentOfInertia;
                double accHeavy = heavy.ControlTorque / heavy.MomentOfInertia;
                bool minIndependentOfMass = heavy.MomentOfInertia > light.MomentOfInertia * 5
                                            && Math.Abs(accLight - accHeavy) < 1e-6;
                // ...but a fixed-torque reaction wheel raises the lighter craft's accel more
                light.ElectricCharge = 100; heavy.ElectricCharge = 100;
                light.Parts[0].Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Reaction Wheel")));
                heavy.Parts[0].Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Reaction Wheel")));
                double dLight = light.ControlTorque / light.MomentOfInertia - accLight;
                double dHeavy = heavy.ControlTorque / heavy.MomentOfInertia - accHeavy;
                bool wheelHelpsLighterMore = dLight > dHeavy && dLight > 0;

                Check("attitude authority", wheelsNeedPower && finsNeedAir && minIndependentOfMass && wheelHelpsLighterMore);
            }

            // 24b. module status gates: ModuleFunctioning mirrors the EcRates gates — an undeployed solar
            //      panel and a powerless reaction wheel read "off", an RTG is always "on", and a fuel cell
            //      with no liquid fuel reads "off" even when deployed and powered.
            {
                var v = new Vessels.Vessel { Body = new CelestialBody { Mu = mu, Radius = 6.371e6 } };
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                var solar = new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Solar Panel"));
                var rtg = new Parts.ModuleInstance(Parts.ModuleCatalog.Get("RTG"));
                var wheel = new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Reaction Wheel"));
                var cell = new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Fuel Cell"));
                pod.Modules.Add(solar); pod.Modules.Add(rtg); pod.Modules.Add(wheel); pod.Modules.Add(cell);
                v.Parts.Add(pod);

                v.ElectricCharge = 0; solar.Active = false; cell.Active = false;
                bool offState = !v.ModuleFunctioning(solar, 0, null) && v.ModuleFunctioning(rtg, 0, null)
                                && !v.ModuleFunctioning(wheel, 0, null) && !v.ModuleFunctioning(cell, 0, null);

                v.ElectricCharge = 50; solar.Active = true; cell.Active = true;   // pod carries no fuel
                bool onState = v.ModuleFunctioning(solar, 0, null) && v.ModuleFunctioning(wheel, 0, null)
                               && !v.ModuleFunctioning(cell, 0, null);            // fuel cell still off: no fuel

                Check("module status gates", offState && onState);
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

            // 25b. RCS translation authority scales with mass (heavier craft = less accel for the same
            //      command, same block), and a freshly launched design starts with every tank full
            //      INCLUDING monopropellant -- so RCS never silently does nothing for lack of fuel at launch.
            {
                Vessels.Vessel Make(bool heavy)
                {
                    var vd = new Vessels.VesselDesign();
                    var pod = new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1"));
                    pod.Modules.Add(Parts.ModuleCatalog.Get("Monoprop Tank"));
                    pod.Modules.Add(Parts.ModuleCatalog.Get("RCS Thruster Block"));
                    vd.Stack.Add(pod);
                    vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400")));   // fuel mass
                    if (heavy) vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T800")));
                    var v = vd.Instantiate();
                    v.Heading = Math.PI / 2; v.RcsEnabled = true; v.RcsCommand = new Vec2d(0, 1);
                    return v;
                }
                var light = Make(false);
                var heavy = Make(true);

                // launch fill: every part full, monoprop + EC at capacity (> 0)
                bool tanksFull = true;
                foreach (var p in light.Parts) if (Math.Abs(p.Fuel - p.Def.FuelCapacity) > 1e-6) tanksFull = false;
                bool monoFull = light.MonopropCapacity > 0 && Math.Abs(light.Monoprop - light.MonopropCapacity) < 1e-6;
                bool ecFull = light.EcCapacity > 0 && Math.Abs(light.ElectricCharge - light.EcCapacity) < 1e-6;

                // mass-dependent accel: same single block, more mass -> less acceleration, matching F/m
                bool massScaled = heavy.TotalMass > light.TotalMass
                                  && heavy.RcsAccel.Length < light.RcsAccel.Length
                                  && Math.Abs(heavy.RcsAccel.Length - light.RcsRawThrust / heavy.TotalMass) < 1e-6 * (light.RcsRawThrust / heavy.TotalMass);
                Check("rcs mass scaling + launch fill", tanksFull && monoFull && ecFull && massScaled);
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

            // 29b. offline production: a colony's drill keeps mining while unattended. AdvanceProduction over
            //      an elapsed span adds fuel at the live rate (clamped to capacity); a non-colony, a non-landed
            //      craft, and a zero/negative span are all no-ops.
            {
                var u = SolarSystemData.Create();
                var moon = u["Moon"];
                // a properly powered base: generation exceeds the drill's draw, so EC stays up and mining
                // runs the whole span (an underpowered base would correctly stall once its EC ran out).
                Vessels.Vessel MakeBase()
                {
                    var b = new Vessels.Vessel { Body = moon, Landed = true, ElectricCharge = 100 };
                    var tank = new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 0 };
                    tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Battery")));        // EC storage
                    for (int k = 0; k < 4; k++)                                                            // 4x RTG > drill draw
                        tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("RTG")));
                    tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Drill")) { Active = true });
                    b.Parts.Add(tank);
                    return b;
                }
                var colony = MakeBase(); colony.IsColony = true;
                double cap = colony.Parts[0].Def.FuelCapacity;
                Vessels.Colony.AdvanceProduction(colony, 100, 110, u);           // 10 s of drilling -> fuel mined
                bool produced = colony.TotalLiquidFuel > 0;
                var capped = MakeBase(); capped.IsColony = true;
                Vessels.Colony.AdvanceProduction(capped, 0, 1e9, u);             // huge span must clamp, never overflow
                bool clamped = capped.TotalLiquidFuel > 0 && capped.TotalLiquidFuel <= cap + 1e-6;

                var notColony = MakeBase();                                       // IsColony false
                Vessels.Colony.AdvanceProduction(notColony, 0, 1000, u);
                var noTime = MakeBase(); noTime.IsColony = true;
                Vessels.Colony.AdvanceProduction(noTime, 100, 100, u);            // zero span
                bool noops = notColony.TotalLiquidFuel == 0 && noTime.TotalLiquidFuel == 0;

                Check("colony production", produced && clamped && noops);
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

            // 30d. reported case: liquid engine + decoupler + axial solid booster underneath. Auto staging must
            //      put the SRB at stage 0 (fires first, lifts off) and the decoupler at stage 1; the first
            //      spacebar press lights ONLY the SRB and detaches nothing. (A decoupler can never be stage 0.)
            {
                var vd = new Vessels.VesselDesign();
                foreach (var n in new[] { "Pod Mk1", "Tank T400", "Terrier", "Decoupler", "Thumper SRB" })
                    vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get(n)));
                var v = vd.Instantiate();
                var srb = v.Parts[4]; var dec = v.Parts[3]; var eng = v.Parts[2];
                bool stages = srb.Def.Name == "Thumper SRB" && srb.Stage == 0
                              && dec.Def.Name == "Decoupler" && dec.Stage == 1 && eng.Stage == 1;

                var debris0 = Vessels.Staging.FireNext(v);         // stage 0: light the SRB only
                bool lit0 = srb.Ignited && !eng.Ignited && debris0 == null && v.Parts.Count == 5;
                bool lifts = v.CurrentThrust > v.TotalMass * Vessels.Staging.G0;   // SRB first stage TWR > 1

                var debris1 = Vessels.Staging.FireNext(v);         // stage 1: decouple SRB, light the engine
                bool decoupled = debris1 != null && debris1.Parts.Exists(p => p.Def.Name == "Thumper SRB")
                                 && !v.Parts.Exists(p => p.Def.Name == "Thumper SRB")
                                 && v.Parts.Exists(p => p.Def.Name == "Terrier" && p.Ignited);
                Check("axial SRB lower stage", stages && lit0 && lifts && decoupled);
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

            // 33e. side boosters jettison on their OWN stage, before the core decoupler: a Pod / Decoupler /
            //      Tank(+separate radial boosters) / Engine defaults to launch(0) -> drop boosters(1) ->
            //      decouple core(2). Regression for the old bug where the radial drop shared the decoupler's
            //      stage, so jettisoning the boosters also shed the core tank.
            {
                var vd = new Vessels.VesselDesign();
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1")));
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Decoupler")));
                var core = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400"));
                core.AddRadial(Parts.PartCatalog.Get("Thumper-R"), separate: true);   // strap-on pair (STG)
                vd.Stack.Add(core);
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Terrier")));
                var v = vd.Instantiate();
                var dec = v.Parts[1]; var tank = v.Parts[2]; var eng = v.Parts[3];
                bool stagesOk = tank.Stage == 0 && eng.Stage == 0
                                && tank.Radials[0].FireStage == 0 && tank.Radials[0].Stage == 1
                                && dec.Stage == 2;

                var d0 = Vessels.Staging.FireNext(v);   // stage 0: ignite engine + boosters, drop nothing
                bool s0 = d0 == null && tank.Radials.Count == 2 && eng.Ignited;
                var d1 = Vessels.Staging.FireNext(v);   // stage 1: drop ONLY the boosters
                bool s1 = d1 != null && tank.Radials.Count == 0
                          && v.Parts.Exists(p => p.Def.Name == "Tank T400")   // core tank survives
                          && d1.Parts.TrueForAll(p => p.Def.Name == "Thumper-R");
                var d2 = Vessels.Staging.FireNext(v);   // stage 2: decouple the core, leaving the pod
                bool s2 = d2 != null && d2.Parts.Exists(p => p.Def.Name == "Tank T400")
                          && v.Parts.Count == 1 && v.Parts[0].Def.Kind == Parts.PartKind.Pod;
                Check("strap-ons drop on own stage", stagesOk && s0 && s1 && s2);
            }

            // 33e-bis. regression: a strap-on booster on a core that is staged LATE must still ignite at
            //      liftoff and be jettisoned strictly AFTER it fires. Previously a separate radial's ignite
            //      stage inherited its (late) host's stage while its drop stage stayed at geometry, so the
            //      booster was jettisoned before it ever ignited ("disappears on the pad before firing").
            {
                var vd = new Vessels.VesselDesign();
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1")));
                var core = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400")) { Stage = 2 }; // core staged late
                core.AddRadial(Parts.PartCatalog.Get("Thumper-R"), separate: true);                  // strap-on, default stages
                vd.Stack.Add(core);
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Terrier")) { Stage = 2 }); // core engine fires at 2
                var v = vd.Instantiate();
                var tank = v.Parts[1];
                var srb = tank.Radials[0];
                bool ordered = srb.FireStage < srb.Stage;   // ignite strictly before drop
                bool firesEarly = srb.FireStage == 0;        // at liftoff, not inheriting the late host

                var d0 = Vessels.Staging.FireNext(v);        // stage 0: ignite the boosters, drop nothing
                bool s0 = d0 == null && tank.Radials.Count == 2 && srb.Ignited && v.SolidThrust > 0;
                Check("radial booster ignites before it drops", ordered && firesEarly && s0);
            }

            // 33e-ter. exact on-pad craft from the user's save ("Ship 1 3"): a Decoupler splits the stack,
            //      the bottom segment is Tank T800 + Fin Set + Swivel with two [Radial Decoupler, Thumper SRB]
            //      sub-stack mounts on the tank. The entire S0/liftoff stage (Swivel + boosters) must survive
            //      instantiate and FireNext#0 must keep the boosters attached. Repro for "boosters + S0 vanish".
            {
                var vd = new Vessels.VesselDesign();
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Parachute")));
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1")));
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400")));
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Terrier")));
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Decoupler")));
                var t800 = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T800"));
                for (int b = 0; b < 2; b++)   // two strap-on sub-stacks: radial decoupler + booster
                {
                    t800.AddRadial(Parts.PartCatalog.Get("Radial Decoupler"));
                    t800.AppendToMount(b, Parts.PartCatalog.Get("Thumper SRB"));
                }
                vd.Stack.Add(t800);
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Fin Set")));
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Swivel")));

                var v = vd.Instantiate();
                var tank = v.Parts[5];   // Tank T800
                int srbN = 0, decN = 0;
                Parts.Part anySrb = null;
                foreach (var r in tank.Radials)
                {
                    if (r.Def.Name == "Thumper SRB") { srbN++; anySrb = r; }
                    else if (r.Def.Name == "Radial Decoupler") decN++;
                }
                // 2 mounts x 2 sides x 2 slots = 8 radials (4 boosters + 4 radial decouplers)
                bool present = tank.Radials.Count == 8 && srbN == 4 && decN == 4;
                bool stagesOk = anySrb != null && anySrb.FireStage == 0 && anySrb.Stage == 1;

                // The HUD recomputes the stage list every frame against the LIVE parts. That must be a pure
                // read: ComputeStages must NOT strip the vessel's radials (the actual launchpad bug).
                var sts = Vessels.Staging.ComputeStages(v.Parts);
                bool notMutated = tank.Radials.Count == 8;
                var st0 = sts.Find(s => s.Number == 0);
                bool s0Exists = st0 != null && st0.Ignites.Contains("4x Thumper SRB") && st0.Ignites.Contains("Swivel");

                var d0 = Vessels.Staging.FireNext(v);   // S0: ignite Swivel + boosters, drop nothing
                bool fired = d0 == null && tank.Radials.Count == 8 && anySrb.Ignited && v.SolidThrust > 0;

                Check("on-pad booster stack keeps S0", present && stagesOk && notMutated && s0Exists && fired);
            }

            // 33f. instantiating a design materializes one runtime radial per (side x sub-stack slot) with
            //      contiguous RadialMountId/Side/Slot tags matching the design mounts, so the flight
            //      renderer (which groups by those tags) draws every radial decoupler/booster the editor
            //      showed. Mount 0 = [Radial Decoupler, Thumper-R] sub-stack; mount 1 = lone Thumper-R.
            {
                var vd = new Vessels.VesselDesign();
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1")));
                var core = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400"));
                core.AddRadial(Parts.PartCatalog.Get("Radial Decoupler"));   // mount 0: root = radial decoupler
                core.AppendToMount(0, Parts.PartCatalog.Get("Thumper-R"));   //          + booster below it
                core.AddRadial(Parts.PartCatalog.Get("Thumper-R"));          // mount 1: plain booster
                vd.Stack.Add(core);
                var rs = vd.Instantiate().Parts[1].Radials;
                // expected: mount 0 has 2 slots x 2 sides = 4, mount 1 has 1 slot x 2 sides = 2 -> 6 total
                int m0 = 0, m1 = 0; bool tagsOk = true;
                foreach (var r in rs)
                {
                    if (r.RadialMountId == 0) m0++;
                    else if (r.RadialMountId == 1) m1++;
                    else tagsOk = false;
                    if (r.RadialSide < 0 || r.RadialSide > 1 || r.RadialSlot < 0) tagsOk = false;
                }
                bool sidesOk = rs.FindAll(r => r.RadialMountId == 0 && r.RadialSide == 0).Count == 2   // both slots, one side
                               && rs.FindAll(r => r.RadialMountId == 0 && r.RadialSide == 1).Count == 2;
                Check("radial materialize tags", rs.Count == 6 && m0 == 4 && m1 == 2 && tagsOk && sidesOk);
            }

            // 33g. enriched StageStat: a Pod / Decoupler / Tank(+STG boosters) / Terrier reads as
            //      Liftoff -> Drop boosters -> Decouple, each stage naming what it ignites/drops, with the
            //      radial jettison flagged distinctly from the axial decouple (drives the KSP-style list).
            {
                var vd = new Vessels.VesselDesign();
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1")));
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Decoupler")));
                var core = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400"));
                core.AddRadial(Parts.PartCatalog.Get("Thumper-R"), separate: true);
                vd.Stack.Add(core);
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Terrier")));
                var sts = Vessels.Staging.ComputeStages(vd.Instantiate().Parts);

                bool s0 = sts.Count >= 1 && sts[0].Action == "Liftoff"
                          && sts[0].Ignites.Contains("Terrier") && sts[0].Ignites.Contains("2x Thumper-R");
                bool s1 = sts.Count >= 2 && sts[1].Action == "Drop boosters"
                          && sts[1].RadialEvent && !sts[1].AxialDecouple
                          && sts[1].Drops.Count == 1 && sts[1].Drops[0] == "2x Thumper-R";
                bool s2 = sts.Count >= 3 && sts[2].Action == "Decouple"
                          && sts[2].AxialDecouple && !sts[2].RadialEvent
                          && sts[2].Drops.Contains("Tank T400");
                Check("enriched stage labels", sts.Count == 3 && s0 && s1 && s2);
            }

            // SAS radial-hold geometry: radial-out points along the body-relative position,
            // radial-in is its exact opposite (what FlightScene.HoldAngleFor uses for those modes).
            {
                bool radOk = true;
                for (int i = 0; i < 40 && radOk; i++)
                {
                    var pos = new Vec2d((rnd.NextDouble() - 0.5) * 2e6, (rnd.NextDouble() - 0.5) * 2e6);
                    if (pos.Length < 1) continue;
                    double outAng = pos.Angle();
                    double inAng = (-pos).Angle();
                    if (Math.Abs(Kepler.WrapPi(inAng - outAng - Math.PI)) > 1e-9) radOk = false;
                }
                Check("SAS radial in/out opposed", radOk);
            }

            // 38. terrain height field: deterministic for a given seed, bounded by the amplitude, periodic in
            //     theta, and the analytic slope matches a finite-difference of the height.
            {
                double R = 6.371e5, amp = 1.2e4;
                var t1 = new Terrain(R, amp, 1234);
                var t2 = new Terrain(R, amp, 1234);
                bool deterministic = true, bounded = true, periodic = true, slopeOk = true;
                for (int i = 0; i < 200; i++)
                {
                    double a = (rnd.NextDouble() - 0.5) * 20;   // arbitrary angles incl. outside [0,2pi]
                    if (Math.Abs(t1.HeightAt(a) - t2.HeightAt(a)) > 1e-9) deterministic = false;
                    if (Math.Abs(t1.HeightAt(a)) > amp + 1e-6) bounded = false;
                    if (Math.Abs(t1.HeightAt(a) - t1.HeightAt(a + 2 * Math.PI)) > 1e-6) periodic = false;
                    double fd = (t1.HeightAt(a + 1e-3) - t1.HeightAt(a - 1e-3)) / (2 * 1e-3 * R);
                    if (Math.Abs(Math.Abs(fd) - t1.SlopeAt(a)) > 1e-9) slopeOk = false;
                }
                Check("terrain field", deterministic && bounded && periodic && slopeOk);
            }

            // 39. guaranteed flat plains: every plain centre is landable (slope below the threshold) and the
            //     flat window around it spans a usable arc, so a safe landing site always exists.
            {
                var t = new Terrain(6.371e5, 1.5e4, 99, plains: 3);
                bool plainsExist = t.PlainCenters.Count > 0;
                bool allFlat = true; bool wideEnough = true;
                foreach (double c in t.PlainCenters)
                {
                    if (t.SlopeAt(c) > Terrain.LandableSlope) allFlat = false;
                    int landable = 0;
                    for (int k = -10; k <= 10; k++)
                        if (t.SlopeAt(c + k * 0.005) <= Terrain.LandableSlope) landable++;
                    if (landable < 7) wideEnough = false;   // a contiguous flat arc, not a single point
                }
                Check("terrain plains", plainsExist && allFlat && wideEnough);
            }

            // 40. terrain wiring: orbiting bodies get relief (SurfaceRadiusAt varies, MaxRadius >= Radius)
            //     while the root star stays a smooth sphere.
            {
                var u = SolarSystemData.Create();
                var earth = u["Earth"]; var sun = u.Root;
                bool sunSmooth = sun.Terrain == null
                                 && Math.Abs(sun.SurfaceRadiusAt(1.0) - sun.Radius) < 1e-9;
                bool earthRelief = earth.Terrain != null && earth.MaxRadius > earth.Radius
                                   && Math.Abs(earth.SurfaceRadiusAt(0.3) - earth.SurfaceRadiusAt(2.1)) > 1e-3;
                Check("terrain wiring", sunSmooth && earthRelief);
            }

            // 40b. launch-pad plain: Earth always has a flat, level plain at the pad longitude so a fresh
            //      launch spawns on solid ground (not buried in a hill) at a fixed, repeatable spot.
            {
                var u = SolarSystemData.Create();
                var earth = u["Earth"];
                double a = SolarSystemData.LaunchPadAngle;
                bool flat = earth.Terrain.SlopeAt(a) <= Terrain.LandableSlope;
                bool level = Math.Abs(earth.Terrain.HeightAt(a)) < earth.Terrain.MaxAmplitude * 0.05;
                bool surfaceSane = earth.SurfaceRadiusAt(a) >= earth.Radius * 0.9
                                   && earth.SurfaceRadiusAt(a) <= earth.MaxRadius;
                Check("launch-pad plain", flat && level && surfaceSane);
            }

            // 41. patched-conic handoff + child-frame node planning (powers placing map nodes on the
            //     projected post-transition path): an orbit escaping the Moon's SOI hands off to Earth with
            //     a continuous absolute state, and a prograde burn planned on a Moon-relative orbit raises
            //     apoapsis only -- node math is frame-agnostic, so a node can live in any SOI.
            {
                var u = SolarSystemData.Create();
                var earth = u["Earth"]; var moon = u["Moon"];

                var r0 = new Vec2d(moon.Radius + 2.0e5, 0);
                double vesc = Math.Sqrt(2 * moon.Mu / r0.Length);
                var el = Kepler.ElementsFromState(r0, new Vec2d(0, vesc * 1.2), moon.Mu, 0);  // hyperbolic escape
                var pred = TrajectoryPredictor.Predict(el, moon, 0);
                bool escapes = pred.Type == TransitionType.Escape && pred.NextBody == earth
                               && !double.IsNaN(pred.NextOrbit.A);
                bool continuous = false;
                if (escapes)
                {
                    Vec2d moonRel = Kepler.StateAtTime(el, pred.TransitionUT).pos;
                    Vec2d viaMoon = moonRel + Kepler.StateAtTime(moon.Orbit, pred.TransitionUT).pos;  // Earth-relative
                    Vec2d viaEarth = Kepler.StateAtTime(pred.NextOrbit, pred.TransitionUT).pos;
                    continuous = (viaEarth - viaMoon).Length < 1e-5 * viaMoon.Length + 1.0;
                }

                var moonOrbit = new OrbitalElements { A = moon.Radius + 1e5, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = moon.Mu, Dir = 1 };
                double tPe = Kepler.TimeAtTrueAnomaly(moonOrbit, 0, 0);
                var raised = new Maneuver { UT = tPe, Prograde = 50 }.ResultOrbit(moonOrbit, moon.Mu);
                bool apRaised = raised.Apoapsis > moonOrbit.Apoapsis + 1
                                && Math.Abs(raised.Periapsis - moonOrbit.Periapsis) < 1e-4 * moonOrbit.Periapsis;

                Check("patched-conic node planning", escapes && continuous && apRaised);
            }

            // 30. science economy: an instrument's payout is data-driven (ModuleDef.ScienceValue) scaled by
            //     situation and body, so different instruments yield different points in the same place.
            {
                var sciJr = Parts.ModuleCatalog.Get("Science Jr");     // ScienceValue 12
                var goo = Parts.ModuleCatalog.Get("Mystery Goo");      // ScienceValue 8
                // Science Jr landed on Moon: round(12 * 1.5 * 2.0) = 36; Mystery Goo: round(8 * 1.5 * 2.0) = 24
                double jrPts = Solar.Scenes.FlightScene.SciPoints(sciJr, "landed", "Moon");
                double gooPts = Solar.Scenes.FlightScene.SciPoints(goo, "landed", "Moon");
                bool valuesOk = Math.Abs(jrPts - 36) < 1e-9 && Math.Abs(gooPts - 24) < 1e-9;
                // instruments are not interchangeable, and situation/body change the payout
                // (Science Jr high orbit at Earth: round(12 * 1.0 * 1.0) = 12, vs 36 landed on the Moon)
                bool distinct = jrPts != gooPts
                                && Solar.Scenes.FlightScene.SciPoints(sciJr, "high orbit", "Earth") == 12
                                && Solar.Scenes.FlightScene.SciPoints(sciJr, "high orbit", "Earth") != jrPts;
                Check("science value data-driven", valuesOk && distinct);
            }

            // 31. tech-tree coverage: every node's parts/modules resolve in the catalogs (so unlocking can't
            //     reference a phantom), and every node has an R&D layout entry (so none goes invisible).
            {
                bool catalogOk = true;
                foreach (var n in Progression.TechTree.Nodes)
                {
                    foreach (var p in n.Parts) if (Parts.PartCatalog.GetById(p) == null) catalogOk = false;
                    foreach (var m in n.Modules) if (Parts.ModuleCatalog.GetById(m) == null) catalogOk = false;
                }
                var laidOut = new HashSet<string>();
                foreach (var e in Scenes.RDScene.Layout) laidOut.Add(e.Id);
                bool layoutOk = true;
                foreach (var n in Progression.TechTree.Nodes) if (!laidOut.Contains(n.Id)) layoutOk = false;
                Check("tech tree coverage", catalogOk && layoutOk);
            }

            // 32. off-rails orbit stays in sync: while a vessel coasts/maneuvers off rails its stored
            //     conic must track the live state, so a save / GoOffRails / reload re-derives the *current*
            //     position, never an old one. Regression guard for the rendezvous "RCS nudge reverts" bug:
            //     a conic captured before the maneuver does NOT reproduce the post-maneuver state, but a
            //     freshly-synced one does (to round-trip precision).
            {
                var u = SolarSystemData.Create();
                var earth = u["Earth"];
                var v = new Vessels.Vessel { Body = earth, Position = new Vec2d(earth.Radius + 300_000, 0), Velocity = new Vec2d(0, 7600) };
                v.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));

                double ut = 1000;
                var staleOrbit = Kepler.ElementsFromState(v.Position, v.Velocity, earth.Mu, ut);  // pre-maneuver conic

                // coast 60 s, apply an "RCS" velocity nudge, then coast 60 s more so the post-maneuver
                // path diverges from the stale (pre-maneuver) conic.
                for (int i = 0; i < 60; i++) { Integrator.Step(v, 1.0); ut += 1.0; }
                v.Velocity += new Vec2d(10, -8);
                for (int i = 0; i < 60; i++) { Integrator.Step(v, 1.0); ut += 1.0; }

                // the synced (current) conic reproduces the live state; the stale one would snap it back
                var fresh = Kepler.ElementsFromState(v.Position, v.Velocity, earth.Mu, ut);
                var (rFresh, _) = Kepler.StateAtTime(fresh, ut);
                var (rStale, _) = Kepler.StateAtTime(staleOrbit, ut);
                bool syncedTracks = (rFresh - v.Position).Length < 1.0;          // round-trip is faithful (<1 m)
                bool staleWouldRevert = (rStale - v.Position).Length > 100.0;    // old conic is genuinely off (the bug)
                Check("off-rails orbit sync", syncedTracks && staleWouldRevert);
            }

            string res = $"Physics self-test: {pass}/{total} PASS";
            if (fails.Count > 0) res += "  FAILED: " + string.Join(", ", fails);
            return res;
        }
    }
}
