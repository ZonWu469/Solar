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

                var rock = new CelestialBody { Name = "Rock", OreRichness = 1.0, Radius = 1e6 };
                var v2 = new Vessels.Vessel { Body = rock, Landed = true, ElectricCharge = 100 };
                var tank = new Parts.Part(Parts.PartCatalog.Get("Tank T200")) { Fuel = 0 };
                tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Battery")));
                tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Drill")) { Active = true });
                v2.Parts.Add(tank);
                v2.UpdateResources(10, 0, null);          // drill: +3 ore/s (x richness 1), -4 EC/s for 10 s
                bool mined = Math.Abs(v2.Ore - 30) < 1e-6 && Math.Abs(v2.ElectricCharge - 60) < 1e-6 && tank.Fuel == 0;
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

            // 19f. a partly-filled tank (StackEntry.FuelOverride) flows into ComputeStages: half the fuel
            //      yields a strictly smaller but positive stage dV than a full tank, matching the rocket
            //      equation on the reduced fuel + mass. Guards the editor fuel-load control.
            {
                var pod = new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1"));
                var tankFull = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400"));
                var eng = new Vessels.StackEntry(Parts.PartCatalog.Get("Terrier"));
                var full = new System.Collections.Generic.List<Vessels.StackEntry> { pod, tankFull, eng };
                double dvFull = 0; foreach (var st in Vessels.Staging.ComputeStages(full)) dvFull += st.DeltaV;

                var tankHalf = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400"))
                    { FuelOverride = Parts.PartCatalog.Get("Tank T400").FuelCapacity / 2 };
                var half = new System.Collections.Generic.List<Vessels.StackEntry> { pod, tankHalf, eng };
                var halfStages = Vessels.Staging.ComputeStages(half);
                double dvHalf = 0; foreach (var st in halfStages) dvHalf += st.DeltaV;

                // expected dV for the half load: Isp*g*ln(m0/(m0-fuel)) with fuel = cap/2
                var tdef = Parts.PartCatalog.Get("Tank T400");
                var edef = Parts.PartCatalog.Get("Terrier");
                double fuel = tdef.FuelCapacity / 2;
                double m0 = pod.Def.DryMass + tdef.DryMass + fuel + edef.DryMass;
                double isp = edef.Thrust / edef.FuelFlowAtMax / 9.81;
                double expect = isp * 9.81 * System.Math.Log(m0 / (m0 - fuel));
                Check("partial tank fuel dV", dvHalf > 0 && dvHalf < dvFull
                                              && System.Math.Abs(dvHalf - expect) < 1.0);
            }

            // 19g. per-engine power limiter (in-flight): halving an engine's PowerLimit scales thrust and
            //      flow together, so the stage's dV is unchanged while its burn time doubles; switching the
            //      engine off removes its thrust so the stage produces no dV. Guards the flight power slider.
            {
                Vessels.Vessel Build(double power, bool on)
                {
                    var v = new Vessels.Vessel();
                    v.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                    v.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")));
                    v.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Terrier")) { PowerLimit = power, EngineOn = on });
                    return v;
                }
                double FullDv(Vessels.Vessel v) { double s = 0; foreach (var st in Vessels.Staging.ComputeStages(v.Parts)) s += st.DeltaV; return s; }
                double FullBt(Vessels.Vessel v) { double s = 0; foreach (var st in Vessels.Staging.ComputeStages(v.Parts)) s += st.BurnTime; return s; }

                var vFull = Build(1.0, true);
                var vHalf = Build(0.5, true);
                var vOff = Build(0.5, false);
                double dvFull = FullDv(vFull), dvHalf = FullDv(vHalf), dvOff = FullDv(vOff);
                double btFull = FullBt(vFull), btHalf = FullBt(vHalf);
                Check("engine power limiter", dvFull > 0
                    && System.Math.Abs(dvHalf - dvFull) < 0.5            // dV unchanged by the limiter
                    && System.Math.Abs(btHalf - 2 * btFull) < 0.5 * btFull   // burn time ~doubles at half power
                    && dvOff == 0);                                     // engine off -> no stage dV
            }

            // 19h. encounter detection must agree with the closest-approach search. A ship whose periapsis
            //      is exactly coincident with a child body passes deep through its SOI; even though that
            //      passage is narrow relative to the predictor's 800-sample grid, Predict must report an
            //      Encounter (guards the FindEncounter refinement). The reported closest approach is also
            //      stable regardless of when the search starts, mirroring anchoring the readout to the plan.
            {
                var parent = new CelestialBody { Mu = mu, Radius = 1 };  // SoiRadius defaults to +inf
                var child = new CelestialBody
                {
                    Mu = mu * 1e-3, Radius = 1, Parent = parent, SoiRadius = 2e5,
                    Orbit = new OrbitalElements { A = 1e7, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 },
                };
                parent.Children.Add(child);
                double tStar = child.Orbit.Period;   // child returns to (1e7, 0) here
                var ship = new OrbitalElements { A = 1e8, E = 0.9, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                ship.M0 = -2 * Math.PI * tStar / ship.Period;   // ship at periapsis (1e7, 0) exactly at tStar

                Func<double, Vec2d> tpos = t => child.AbsolutePositionAt(t);
                bool ca0 = Rendezvous.ClosestApproach(ship, parent, tpos, 0, ship.Period, out _, out double sep0, out _);
                bool ca1 = Rendezvous.ClosestApproach(ship, parent, tpos, tStar * 0.5, ship.Period, out _, out double sep1, out _);
                var pr = TrajectoryPredictor.Predict(ship, parent, 0);
                Check("encounter detection", ca0 && ca1 && sep0 < child.SoiRadius
                    && pr.Type == TransitionType.Encounter && pr.NextBody == child
                    && Math.Abs(sep0 - sep1) < 1e3);   // closest approach stable across search-start times
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
                bool docked = a.DockWith(b, a.FirstFreeDockingPort(), b.FirstFreeDockingPort(), 0, Vec2d.Zero);
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

            // 27b. dock geometry: approaching at ~90 deg snaps the docked module to a quarter turn, places
            //      it so the two ports overlap, and undock restores the module's snapped heading/pose.
            {
                var u = SolarSystemData.Create();
                var earth = u["Earth"];
                var a = new Vessels.Vessel { Body = earth, Position = new Vec2d(earth.Radius + 300000, 0), Heading = Math.PI / 2 };
                a.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));   // nose port
                a.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var b = new Vessels.Vessel { Body = earth, Position = a.Position, Heading = Math.PI / 2 + 1.55 };  // ~88.8 deg off
                b.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));
                b.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));

                var myPort = a.FirstFreeDockingPort();
                var theirPort = b.FirstFreeDockingPort();
                int q = ((int)Math.Round((b.Heading - a.Heading) / (Math.PI / 2))) & 3;
                var offset = a.PartLocalCenter(myPort) - Vessels.Vessel.RotQuarter(q, b.PartLocalCenter(theirPort));
                a.DockWith(b, myPort, theirPort, q, offset);

                double portGap = (a.PortWorldCenter(myPort, 0) - a.PortWorldCenter(theirPort, 0)).Length;
                double expectHead = a.Heading + q * (Math.PI / 2);
                var det = a.Undock(0);
                bool undockPose = det != null && det.Parts.Count == 2 && Math.Abs(det.Heading - expectHead) < 1e-6;

                bool snapOk = q == 1
                    && ((int)Math.Round(200 * Math.PI / 180 / (Math.PI / 2)) & 3) == 2
                    && ((int)Math.Round(40 * Math.PI / 180 / (Math.PI / 2)) & 3) == 0;
                Check("dock geometry", snapOk && portGap < 1e-6 && undockPose);
            }

            // 27c. undock co-location: every part of the detached module sits at exactly the world position
            //      it had while docked (no teleport). Deterministic guard for the undock pose reconstruction.
            {
                var u = SolarSystemData.Create();
                var earth = u["Earth"];
                var a = new Vessels.Vessel { Body = earth, Position = new Vec2d(earth.Radius + 400000, 0), Heading = 0.3 };
                a.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));
                a.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var b = new Vessels.Vessel { Body = earth, Position = a.Position, Heading = 0.3 + 1.55 };  // ~90 deg
                b.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));
                b.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")));

                var myPort = a.FirstFreeDockingPort();
                var theirPort = b.FirstFreeDockingPort();
                int q = ((int)Math.Round((b.Heading - a.Heading) / (Math.PI / 2))) & 3;
                var offset = a.PartLocalCenter(myPort) - Vessels.Vessel.RotQuarter(q, b.PartLocalCenter(theirPort));
                var bParts = new List<Parts.Part>(b.Parts);   // shared Part refs survive the merge
                a.DockWith(b, myPort, theirPort, q, offset);

                // compare body-relative centers (not heliocentric absolute, which carries ~1.5e11 m and
                // would swamp a 1e-6 tolerance in float noise): the Body offset cancels, the geometry doesn't
                Vec2d Rel(Vessels.Vessel v, Parts.Part p) { var c = v.PartLocalCenter(p); return v.Position + v.Right * c.X + v.Up * c.Y; }
                var docked = new List<Vec2d>();
                foreach (var p in bParts) docked.Add(Rel(a, p));

                var det = a.Undock(0);   // no separation impulse: exact co-location
                bool coLocated = det != null && det.Parts.Count == bParts.Count;
                if (coLocated)
                    for (int i = 0; i < bParts.Count; i++)
                        if ((Rel(det, bParts[i]) - docked[i]).Length > 1e-6) coLocated = false;
                Check("undock co-location", coLocated);
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
                a.DockWith(b, a.FirstFreeDockingPort(), b.FirstFreeDockingPort(), 0, Vec2d.Zero);

                var s = ShipState.From(a, "Station");
                var a2 = s.ToVessel(u);
                bool restored = a2.Parts.Count == 4 && a2.DockLinks.Count == 1 && a2.CanUndock;
                var det = a2.Undock(0);
                bool splitOk = det != null && a2.Parts.Count == 2 && det.Parts.Count == 2;
                Check("station persist", restored && splitOk);
            }

            // 29. colony: a connected surface base pools refined fuel across the whole assembly, so an
            //     ISRU converter on the base refuels a docked lander; the colony milestone needs a crewed,
            //     food-regenerating base off Earth.
            {
                var u = SolarSystemData.Create();
                var moon = u["Moon"];

                // base (drill + ISRU on a full tank, big battery) + lander (empty tank), connected via docking ports
                var baseV = new Vessels.Vessel { Body = moon, Landed = true, ElectricCharge = 50000, Ore = 200 };
                var baseTank = new Parts.Part(Parts.PartCatalog.Get("Tank T400"));
                baseTank.Fuel = baseTank.Def.FuelCapacity;     // base tank already full: overflow goes to the lander
                baseTank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Battery Z-10k")));  // EC to run the rig
                baseTank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Drill")) { Active = true });
                baseTank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("ISRU Converter")) { Active = true });
                baseV.Parts.Add(baseTank);
                baseV.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));

                var lander = new Vessels.Vessel();
                lander.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Docking Port Jr")));
                var landerTank = new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 0 };
                lander.Parts.Add(landerTank);

                baseV.DockWith(lander, baseV.FirstFreeDockingPort(), lander.FirstFreeDockingPort(), 0, Vec2d.Zero);
                double f0 = baseV.TotalLiquidFuel;
                baseV.UpdateResources(10, 0, u);               // ISRU +8 fuel/s for 10 s, pooled base-wide
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
                double cap = colony.OreCapacity;
                Vessels.Colony.AdvanceProduction(colony, 100, 110, u);           // 10 s of drilling -> ore mined
                bool produced = colony.Ore > 0;
                var capped = MakeBase(); capped.IsColony = true;
                Vessels.Colony.AdvanceProduction(capped, 0, 1e9, u);             // huge span must clamp, never overflow
                bool clamped = capped.Ore > 0 && capped.Ore <= cap + 1e-6;

                var notColony = MakeBase();                                       // IsColony false
                Vessels.Colony.AdvanceProduction(notColony, 0, 1000, u);
                var noTime = MakeBase(); noTime.IsColony = true;
                Vessels.Colony.AdvanceProduction(noTime, 100, 100, u);            // zero span
                bool noops = notColony.Ore == 0 && noTime.Ore == 0;

                // offline parity: one big catch-up step equals many small live ticks (kept under capacity)
                var oneStep = MakeBase(); oneStep.IsColony = true;
                Vessels.Colony.AdvanceProduction(oneStep, 0, 100, u);
                var manySteps = MakeBase(); manySteps.IsColony = true;
                for (int k = 0; k < 100; k++) Vessels.Colony.AdvanceProduction(manySteps, k, k + 1, u);
                bool parity = Math.Abs(oneStep.Ore - manySteps.Ore) < 1e-6 && oneStep.Ore > 0;

                Check("colony production", produced && clamped && noops && parity);
            }

            // 29c. ISRU conversion: refines stored ore into fuel at the configured ratio, bottlenecked by
            //      ore on hand and by free fuel capacity (no overflow, no negative ore). Two reactors keep
            //      EC positive so a single huge catch-up step still runs.
            {
                var isru = Parts.ModuleCatalog.Get("ISRU Converter");
                double ratio = isru.FuelProduce / isru.OreDraw;
                Vessels.Vessel MakeIsru(double ore)
                {
                    var v = new Vessels.Vessel { ElectricCharge = 50000, Ore = ore };
                    var tank = new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 0 };
                    tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Battery Z-10k")));
                    tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Nuclear Reactor")));
                    tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Nuclear Reactor")));
                    tank.Modules.Add(new Parts.ModuleInstance(isru) { Active = true });
                    v.Parts.Add(tank);
                    return v;
                }
                var a = MakeIsru(100);
                a.UpdateResources(1.0, 0, null);                         // 1 s: OreDraw ore -> OreDraw*ratio fuel
                bool converts = Math.Abs((100 - a.Ore) - isru.OreDraw) < 1e-6
                                && Math.Abs(a.TotalLiquidFuel - isru.OreDraw * ratio) < 1e-6;
                a.UpdateResources(1e6, 0, null);                         // drain the rest, never below zero
                bool oreFloor = a.Ore >= -1e-9 && a.Ore < 1e-6;

                var b = MakeIsru(1e6);
                double cap = b.Parts[0].Def.FuelCapacity;
                b.UpdateResources(1e9, 0, null);                         // tank fills; surplus ore is kept, not wasted
                bool noOverflow = b.TotalLiquidFuel <= cap + 1e-6 && b.Ore > 1e6 - cap / ratio - 1.0;
                Check("isru conversion", converts && oreFloor && noOverflow);
            }

            // 29d. ore round-trips through save/load (ShipState <-> Vessel).
            {
                var u = SolarSystemData.Create();
                var v = new Vessels.Vessel { Body = u["Moon"], Landed = true, Ore = 1234 };
                var tank = new Parts.Part(Parts.PartCatalog.Get("Tank T400"));
                tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Drill")));   // adds ore capacity
                v.Parts.Add(tank);
                var v2 = ShipState.From(v, "Miner", null, null, 0).ToVessel(u);
                Check("ore round-trip", Math.Abs(v2.Ore - 1234) < 1e-9);
            }

            // 29e. crew roles: engineers speed drilling/ISRU (capped), scientists boost science (capped),
            //      a pilot improves attitude authority.
            {
                Vessels.Vessel Drilling(int engineers)
                {
                    var rock = new CelestialBody { Name = "Rock", OreRichness = 1.0, Radius = 1e6 };
                    var v = new Vessels.Vessel { Body = rock, Landed = true, ElectricCharge = 100000 };
                    var tank = new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 0 };
                    tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Battery Z-10k")));
                    tank.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Drill")) { Active = true });
                    for (int k = 0; k < engineers; k++)
                        tank.Crew.Add(new Vessels.CrewMember("E" + k, Vessels.CrewRole.Engineer));
                    v.Parts.Add(tank);
                    return v;
                }
                var none = Drilling(0); none.UpdateResources(10, 0, null);
                var two = Drilling(2);  two.UpdateResources(10, 0, null);
                bool engBoost = none.Ore > 0 && Math.Abs(two.Ore / none.Ore - 1.5) < 1e-6;      // +25%/engineer
                bool engCap = Math.Abs(Drilling(9).CrewSkill(Vessels.CrewRole.Engineer) - 1.75) < 1e-9;  // capped at 3

                var sci = new Vessels.Vessel();
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                pod.Crew.Add(new Vessels.CrewMember("S1", Vessels.CrewRole.Scientist));
                pod.Crew.Add(new Vessels.CrewMember("S2", Vessels.CrewRole.Scientist));
                sci.Parts.Add(pod);
                bool sciMult = Math.Abs(sci.CrewSkill(Vessels.CrewRole.Scientist) - 1.4) < 1e-9;  // 1 + 0.20*2

                var unmanned = new Vessels.Vessel(); unmanned.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var piloted = new Vessels.Vessel();
                var pp = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                pp.Crew.Add(new Vessels.CrewMember("P", Vessels.CrewRole.Pilot));
                piloted.Parts.Add(pp);
                bool pilot = piloted.ControlTorque > unmanned.ControlTorque;

                Check("crew roles", engBoost && engCap && sciMult && pilot);
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

            // 30c. re-tagging a part's stage changes when it fires: two inline engines default to the same
            //      stage 0, but re-tagging the upper one to stage 1 holds it back -- the first press lights only
            //      the bottom engine, the second press lights the re-tagged one. (Staging snaps to real events,
            //      so an empty stage never costs a press.)
            {
                var vd = new Vessels.VesselDesign();
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1")));
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400")));
                var eng = new Vessels.StackEntry(Parts.PartCatalog.Get("Terrier")) { Stage = 1 };  // explicit, not 0
                vd.Stack.Add(eng);
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T800")));
                vd.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Swivel")));   // bottom engine, default stage 0
                var v = vd.Instantiate();
                Vessels.Staging.FireNext(v);                       // stage 0: bottom engine only
                bool notLit = !v.Parts[2].Ignited && v.Parts[4].Ignited;
                Vessels.Staging.FireNext(v);                       // stage 1: the re-tagged engine
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

            // 33. close-formation rails round-trip: two craft 15 m apart in a Moon orbit, put on rails then
            //     taken off at the same epoch (a save/reload handoff), keep their separation to sub-meter.
            //     Guards against a docking target drifting off when the gap is serialized and restored.
            {
                var u = SolarSystemData.Create();
                var moon = u["Moon"];
                double ut = 284160.538;                                   // a large UT like the live save
                double r = moon.Radius + 1.0e6;
                var a = new Vessels.Vessel { Body = moon, Position = new Vec2d(r, 0), Velocity = new Vec2d(-200, Math.Sqrt(moon.Mu / r) * 1.02) };
                a.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var b = new Vessels.Vessel { Body = moon, Position = a.Position + new Vec2d(3, 14.7), Velocity = a.Velocity };
                b.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                double sep0 = (a.Position - b.Position).Length;           // ~15 m
                a.GoOnRails(ut); b.GoOnRails(ut);                         // save: derive each conic
                a.GoOffRails(ut); b.GoOffRails(ut);                       // reload: evaluate at the same epoch
                double sep1 = (a.Position - b.Position).Length;
                Check("close-formation rails round-trip", Math.Abs(sep1 - sep0) < 1.0);
            }

            // 34. threats / broken-module gate: a broken module reports not-functioning and contributes
            //     nothing to EcRates; clearing the break restores both, matching the malfunction model.
            {
                var v = new Vessels.Vessel();
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                var rtg = new Parts.ModuleInstance(Parts.ModuleCatalog.Get("RTG"));
                pod.Modules.Add(rtg);
                v.Parts.Add(pod);
                v.EcRates(0, null, out double prod0, out _);
                rtg.Broken = true;
                bool brokenOff = !v.ModuleFunctioning(rtg, 0, null);
                v.EcRates(0, null, out double prodBroken, out _);
                rtg.Broken = false;
                v.EcRates(0, null, out double prod1, out _);
                Check("broken module gate", prod0 > 0 && brokenOff && prodBroken < prod0 && Math.Abs(prod1 - prod0) < 1e-9);
            }

            // 35. radiation: crew accumulate dose inside a belt at intensity*(1-shield) per second; the
            //     accumulator is warp-safe (one big step == many small ones), a shield cuts the rate,
            //     leaving a belt lets the dose decay, and a lethal dose kills crew.
            {
                var rng = new Random(1);
                var belt = new CelestialBody { Radius = 1e6, RadBeltInner = 0, RadBeltOuter = 1e9, RadBeltDose = 1.0 };
                var clear = new CelestialBody { Radius = 1e6 };
                Vessels.Vessel Crewed(CelestialBody body, string shield)
                {
                    var vv = new Vessels.Vessel { Body = body, Position = new Vec2d(body.Radius + 1e5, 0) };
                    var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                    pod.Crew.Add(new Vessels.CrewMember("R", Vessels.CrewRole.Pilot));
                    if (shield != null) pod.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get(shield)));
                    vv.Parts.Add(pod);
                    return vv;
                }
                double Dose(Vessels.Vessel vv) { foreach (var c in vv.AllCrew()) return c.RadDose; return -1; }

                var unshielded = Crewed(belt, null);
                Vessels.Threats.Tick(unshielded, 100, 0, null, rng);
                bool accrued = Math.Abs(Dose(unshielded) - 100) < 1e-6;            // 1 dose/s * 100 s

                var big = Crewed(belt, null); Vessels.Threats.Tick(big, 500, 0, null, rng);
                var small = Crewed(belt, null); for (int k = 0; k < 500; k++) Vessels.Threats.Tick(small, 1, 0, null, rng);
                bool warpSafe = Math.Abs(Dose(big) - Dose(small)) < 1e-6 && Dose(big) > 499;   // sub-lethal: crew survive

                var shielded = Crewed(belt, "Radiation Shield");
                Vessels.Threats.Tick(shielded, 100, 0, null, rng);
                bool shieldCuts = Math.Abs(Dose(shielded) - 20) < 1e-6;            // (1 - 0.8) * 100

                var decaying = Crewed(clear, null);
                foreach (var c in decaying.AllCrew()) c.RadDose = 100;
                Vessels.Threats.Tick(decaying, 100, 0, null, rng);
                bool decays = Dose(decaying) < 100 && Dose(decaying) > 0;          // clears, but slowly

                var lethal = Crewed(belt, null);
                Vessels.Threats.Tick(lethal, 2000, 0, null, rng);                  // 2000 dose >= death threshold
                bool kills = lethal.CrewCount == 0;

                Check("radiation belt dose", accrued && warpSafe && shieldCuts && decays && kills);
            }

            // 36. malfunction + repair: a functioning module worn hard enough eventually breaks; a landed
            //     craft with an engineer and power then repairs it. A huge dt drives the failure/repair
            //     probability to ~1, so the outcome is deterministic without stubbing the RNG.
            {
                var rng = new Random(7);
                var v = new Vessels.Vessel { ElectricCharge = 100 };
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                var wheel = new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Reaction Wheel"));
                pod.Modules.Add(wheel);
                v.Parts.Add(pod);
                Vessels.Threats.Tick(v, 1e12, 0, null, rng);                       // not landed: breaks, no repair
                bool broke = wheel.Broken;

                v.Landed = true;
                pod.Crew.Add(new Vessels.CrewMember("E", Vessels.CrewRole.Engineer));
                v.ElectricCharge = 100;
                Vessels.Threats.Tick(v, 1e12, 0, null, rng);                       // landed + engineer + EC: repairs
                bool repaired = !wheel.Broken && wheel.Wear <= 1e-9;
                Check("malfunction repair", broke && repaired);
            }

            // 37. illness + persistence: a fully ill engineer contributes no skill bonus; RadiationAt reads
            //     the belt dose only inside its band; broken/wear state round-trips through the savegame.
            {
                var v = new Vessels.Vessel();
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                var eng = new Vessels.CrewMember("E", Vessels.CrewRole.Engineer);
                pod.Crew.Add(eng);
                v.Parts.Add(pod);
                double healthy = v.CrewSkill(Vessels.CrewRole.Engineer);
                eng.Illness = 1.0;
                double sick = v.CrewSkill(Vessels.CrewRole.Engineer);
                bool illnessDrags = healthy > 1.0 && Math.Abs(sick - 1.0) < 1e-9;

                var b = new CelestialBody { Radius = 1e6, RadBeltInner = 1000, RadBeltOuter = 5000, RadBeltDose = 2 };
                bool band = b.RadiationAt(3000) == 2 && b.RadiationAt(500) == 0 && b.RadiationAt(6000) == 0;

                var u = SolarSystemData.Create();
                var vp = new Vessels.Vessel { Body = u["Earth"], Position = new Vec2d(u["Earth"].Radius + 1000, 0) };
                var podP = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                podP.Modules.Add(new Parts.ModuleInstance(Parts.ModuleCatalog.Get("Reaction Wheel")) { Broken = true, Wear = 0.42 });
                vp.Parts.Add(podP);
                var vp2 = ShipState.From(vp, "T").ToVessel(u);
                var m2 = vp2.Parts[0].Modules[0];
                bool persisted = m2.Broken && Math.Abs(m2.Wear - 0.42) < 1e-9;

                Check("illness + threat persistence", illnessDrags && band && persisted);
            }

            // 30. encounter band-rejection: a vessel orbit whose radius band overlaps a child's (with SOI
            //     margin) can still find the encounter; one whose band can't reach the child returns none.
            //     A Hohmann-style ellipse is timed so its apoapsis coincides with a circular moon.
            {
                var primary = new CelestialBody { Mu = mu, Radius = 1e3 };   // SoiRadius defaults to +inf: no escape
                const double Rc = 2e6, soi = 5e4;
                var child = new CelestialBody { Mu = mu * 1e-6, Radius = 1e3, SoiRadius = soi, Parent = primary,
                    Orbit = new OrbitalElements { A = Rc, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 } };
                primary.Children.Add(child);

                // ellipse: periapsis 1e6, apoapsis Rc (apoapsis at world angle pi). Time the moon to be there.
                double Rp = 1e6, A = (Rc + Rp) / 2;
                var ship = new OrbitalElements { A = A, E = (Rc - Rp) / (Rc + Rp), ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                double tApo = Kepler.TimeAtTrueAnomaly(ship, Math.PI, 0);
                child.Orbit.M0 = Math.PI - Math.Sqrt(mu / (Rc * Rc * Rc)) * tApo;   // moon at angle pi at tApo
                var hit = TrajectoryPredictor.Predict(ship, primary, 0);
                bool found = hit.Type == TransitionType.Encounter && hit.NextBody == child;

                var lowShip = new OrbitalElements { A = 1.5e6, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                var miss = TrajectoryPredictor.Predict(lowShip, primary, 0);   // apoapsis 1.5e6 < Rc - soi: rejected
                bool rejected = miss.Type != TransitionType.Encounter;
                Check("encounter band-rejection", found && rejected);
            }

            // 31. arrival timing (drives the intersection markers): the ship reaches a given true anomaly at
            //     TimeAtTrueAnomaly, and propagating to that time lands on that point of the orbit.
            {
                var el = new OrbitalElements { A = 8e6, E = 0.25, ArgPe = 1.1, M0 = 0.3, Epoch = 0, Mu = mu, Dir = 1 };
                bool ok = true;
                for (int i = 0; i < 8 && ok; i++)
                {
                    double nu = -2.5 + i * 0.6;
                    double t = Kepler.TimeAtTrueAnomaly(el, nu, 0);
                    var byTime = Kepler.StateAtTime(el, t).pos;
                    var byNu = Kepler.StateAtTrueAnomaly(el, nu).pos;
                    if ((byTime - byNu).Length > 1e-3 + 1e-6 * byNu.Length) ok = false;
                }
                Check("arrival timing", ok);
            }

            // 32. planned orbit anchored to the node's frozen source (so it holds steady during a burn):
            //     ResultOrbit(Source) is deterministic and independent of the live orbit it would drift to.
            {
                var src0 = new OrbitalElements { A = 7e6, E = 0.1, ArgPe = 0.4, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                double tp = Kepler.TimeAtTrueAnomaly(src0, 0, 0);
                var node = new Maneuver { UT = tp, Prograde = 80, Radial = 0, Source = src0, HasSource = true };
                var planned1 = node.ResultOrbit(node.Source, mu);
                var planned2 = node.ResultOrbit(node.Source, mu);
                var drifted = src0; drifted.A += 5e4;                 // a "live" orbit risen under thrust
                var fromLive = node.ResultOrbit(drifted, mu);
                bool anchored = Math.Abs(planned1.A - planned2.A) < 1e-6   // deterministic
                                && Math.Abs(planned1.A - fromLive.A) > 1.0; // and not what feeding the live orbit gives
                Check("planned orbit anchored to source", anchored);
            }

            // 33. transfer-leg closest approach (drives KSP-style node tuning to a moon inside the primary's
            //     SOI). A burn that overshoots puts the ship on an escape-class ellipse: it still crosses the
            //     moon's distance BEFORE leaving the SOI, so the useful readout is the miss to the moon
            //     measured over the pre-escape leg's window -- not the post-escape conic extrapolated to its
            //     (astronomical) apoapsis. Timed right -> an encounter; mistimed -> a sane finite miss that
            //     shrinks as the node is tuned (never the apoapsis-scale garbage the old full-period search gave).
            {
                var primary = new CelestialBody { Mu = mu, Radius = 1e3, SoiRadius = 1e8 };
                const double Rc = 2e6, soi = 5e4;
                var moon = new CelestialBody { Mu = mu * 1e-6, Radius = 1e3, SoiRadius = soi, Parent = primary,
                    Orbit = new OrbitalElements { A = Rc, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 } };
                primary.Children.Add(moon);

                // escape-class ellipse: periapsis 1e6, apoapsis 5e9 (>> SOI 1e8). It crosses r=Rc outbound.
                double Rp = 1e6, Ra = 5e9, A = (Ra + Rp) / 2, e = (Ra - Rp) / (Ra + Rp);
                var ship = new OrbitalElements { A = A, E = e, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                double nuC = Math.Acos((ship.SemiLatus / Rc - 1) / e);       // true anomaly at the moon-distance crossing
                double tC = Kepler.TimeAtTrueAnomaly(ship, nuC, 0);
                double escapeT = Kepler.NextRadiusCrossingOutbound(ship, primary.SoiRadius, 1e-6).Value;
                double n = Math.Sqrt(mu / (Rc * Rc * Rc));                    // moon mean motion (circular)
                Func<double, Vec2d> moonAbs = t => moon.AbsolutePositionAt(t);

                // timed: place the moon at the crossing point at tC -> encounter + sub-SOI closest approach.
                moon.Orbit.M0 = nuC - n * tC;
                var hit = TrajectoryPredictor.Predict(ship, primary, 0);
                Rendezvous.ClosestApproach(ship, primary, moonAbs, 0, escapeT, out _, out double sepHit, out _);
                bool timed = hit.Type == TransitionType.Encounter && hit.NextBody == moon && sepHit < soi;

                // mistimed: moon half an orbit away at the crossing -> escape, but the pre-escape leg still
                // yields a sane finite miss (well under apoapsis), the number the player tunes down.
                moon.Orbit.M0 = nuC + Math.PI - n * tC;
                var esc = TrajectoryPredictor.Predict(ship, primary, 0);
                Rendezvous.ClosestApproach(ship, primary, moonAbs, 0, escapeT, out _, out double sepMiss, out _);
                bool mistimed = esc.Type == TransitionType.Escape && sepMiss > soi && sepMiss < 1e7;

                Check("transfer-leg closest approach", timed && mistimed);
            }

            // 34. chained patched-conic prediction: after an encounter, the inside-SOI flyby leg (the conic
            //     the map draws and the player drops a capture node on) must be a valid, SOI-bounded conic,
            //     and a second Predict on it must not blow up. Guards BuildProjection's multi-leg walk.
            {
                var primary = new CelestialBody { Mu = mu, Radius = 1e3, SoiRadius = 1e8 };
                const double Rc = 2e6, soi = 5e4;
                var moon = new CelestialBody { Mu = mu * 1e-6, Radius = 1e3, SoiRadius = soi, Parent = primary,
                    Orbit = new OrbitalElements { A = Rc, E = 0, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 } };
                primary.Children.Add(moon);

                double Rp = 1e6, Ra = 5e9, A = (Ra + Rp) / 2, e = (Ra - Rp) / (Ra + Rp);
                var ship = new OrbitalElements { A = A, E = e, ArgPe = 0, M0 = 0, Epoch = 0, Mu = mu, Dir = 1 };
                double nuC = Math.Acos((ship.SemiLatus / Rc - 1) / e);
                double tC = Kepler.TimeAtTrueAnomaly(ship, nuC, 0);
                double n = Math.Sqrt(mu / (Rc * Rc * Rc));
                moon.Orbit.M0 = nuC - n * tC;                       // moon at the crossing point -> encounter

                var enc = TrajectoryPredictor.Predict(ship, primary, 0);
                var flyby = enc.NextOrbit;
                bool ok = enc.Type == TransitionType.Encounter && enc.NextBody == moon
                          && !double.IsNaN(flyby.A) && !double.IsNaN(flyby.E) && flyby.Periapsis < soi;
                var back = TrajectoryPredictor.Predict(flyby, moon, enc.TransitionUT);
                ok = ok && !double.IsNaN(back.Orbit.A)
                        && (back.Type == TransitionType.None || back.TransitionUT >= enc.TransitionUT - 1);
                Check("chained patched-conic flyby", ok);
            }

            // 35. off-axis engine thrust: ThrustDir maps the authored angle to the craft frame (0 = Up,
            //     +/-90 = right/left). An all-axial stack's ThrustVector equals CurrentThrust along Up. A
            //     radial (90-deg) engine fires on the SIGNED Q/E command (RcsCommand.X), not the throttle:
            //     +1 -> +Right, -1 -> -Right, command 0 -> no thrust; and it is excluded from the scalar
            //     CurrentThrust / MaxAvailableThrust (those stay the main-throttle figure). Guards the
            //     left/right-firing horizontal-landing thrusters.
            {
                var v = new Vessels.Vessel { Heading = Math.PI / 2 };       // Up = (0,1), Right = (1,0)
                bool dirOk = (v.ThrustDir(0) - v.Up).Length < 1e-9
                          && (v.ThrustDir(90) - v.Right).Length < 1e-9
                          && (v.ThrustDir(-90) + v.Right).Length < 1e-9;

                var ax = new Vessels.Vessel { Heading = Math.PI / 2, Throttle = 1 };
                ax.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                ax.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 400 });
                ax.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Terrier")) { Ignited = true });
                bool axOk = ax.CurrentThrust > 0 && (ax.ThrustVector - ax.Up * ax.CurrentThrust).Length < 1e-3;

                double rt = Parts.PartCatalog.Get("Lateral Thruster (Q/E)").Thrust;
                var rad = new Vessels.Vessel { Heading = Math.PI / 2, Throttle = 1 };
                rad.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                rad.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 400 });
                rad.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Lateral Thruster (Q/E)")) { Ignited = true });
                // throttle alone (no lateral command) produces no radial thrust, and the radial engine is
                // excluded from the scalar throttle thrust figures.
                bool offThrottle = rad.ThrustVector.Length < 1e-6 && rad.CurrentThrust == 0 && rad.MaxAvailableThrust == 0;
                rad.RcsCommand = new Vec2d(1, 0);
                var tvR = rad.ThrustVector;
                bool rightOk = (tvR - rad.Right * rt).Length < 1e-3 && Math.Abs(tvR.Dot(rad.Up)) < 1e-3 * tvR.Length;
                rad.RcsCommand = new Vec2d(-1, 0);
                bool leftOk = (rad.ThrustVector + rad.Right * rt).Length < 1e-3;
                bool radOk = offThrottle && rightOk && leftOk;

                Check("off-axis engine thrust", dirOk && axOk && radOk);
            }

            // 35b. single-sided lateral thrusters: a side-tagged off-axis engine pushes away from its
            //      mount side and fires only on the matching Q/E command. Left (RadialSide 1) pushes +Right
            //      on a +command; right (RadialSide 0) pushes -Right on a -command; a lone thruster cannot
            //      push the other way. Guards the per-side thrust gate.
            {
                double rt = Parts.PartCatalog.Get("Lateral Thruster (Q/E)").Thrust;
                var v = new Vessels.Vessel { Heading = Math.PI / 2 };       // Up = (0,1), Right = (1,0)
                v.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var host = new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 400 };
                host.Radials.Add(new Parts.Part(Parts.PartCatalog.Get("Lateral Thruster (Q/E)")) { Ignited = true, RadialSide = 1 }); // left
                host.Radials.Add(new Parts.Part(Parts.PartCatalog.Get("Lateral Thruster (Q/E)")) { Ignited = true, RadialSide = 0 }); // right
                v.Parts.Add(host);

                v.RcsCommand = new Vec2d(1, 0);    // command +Right -> only the left thruster fires (+Right)
                bool leftFires = (v.ThrustVector - v.Right * rt).Length < 1e-3;
                v.RcsCommand = new Vec2d(-1, 0);   // command -Right -> only the right thruster fires (-Right)
                bool rightFires = (v.ThrustVector + v.Right * rt).Length < 1e-3;

                // a lone left thruster can only push +Right; a -Right command yields no thrust
                var lone = new Vessels.Vessel { Heading = Math.PI / 2 };
                lone.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var lh = new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 400 };
                lh.Radials.Add(new Parts.Part(Parts.PartCatalog.Get("Lateral Thruster (Q/E)")) { Ignited = true, RadialSide = 1 });
                lone.Parts.Add(lh);
                lone.RcsCommand = new Vec2d(-1, 0);
                bool loneBlocked = lone.ThrustVector.Length < 1e-6;
                lone.RcsCommand = new Vec2d(1, 0);
                bool loneFires = (lone.ThrustVector - lone.Right * rt).Length < 1e-3;

                Check("single-sided thruster gate", leftFires && rightFires && loneBlocked && loneFires);
            }

            // 36. chute weathervane geometry: a top-mounted chute sits above the CoM (offset>0 -> craft
            //     points retrograde); adding a bottom chute balances it (bothEnds -> hold broadside / flat).
            //     Guards the horizontal-landing chute attitude model.
            {
                var top = new Vessels.Vessel();
                top.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Parachute")) { Deployed = true });
                top.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                top.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")));
                bool topOk = top.DeployedChuteOffset(out double offTop, out bool bothTop) && offTop > 0 && !bothTop;

                var both = new Vessels.Vessel();
                both.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Parachute")) { Deployed = true });
                both.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                both.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")));
                both.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Parachute")) { Deployed = true });
                bool bothOk = both.DeployedChuteOffset(out _, out bool bothEnds) && bothEnds;

                var none = new Vessels.Vessel();
                none.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                bool noneOk = !none.DeployedChuteOffset(out _, out _);

                Check("chute weathervane geometry", topOk && bothOk && noneOk);
            }

            // 37. radial-engine fuel draw: an off-axis engine fired on the Q/E command burns its segment's
            //     cross-fed fuel even at zero throttle; with no command and no throttle nothing drains.
            {
                var df = new Vessels.Vessel { Throttle = 0 };
                df.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var tnk = new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 400 };
                df.Parts.Add(tnk);
                df.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Lateral Thruster (Q/E)")) { Ignited = true });

                df.RcsCommand = new Vec2d(1, 0);
                double before = tnk.Fuel;
                df.DrainFuel(1.0);
                bool drained = tnk.Fuel < before - 1e-6;

                df.RcsCommand = Vec2d.Zero;
                double held = tnk.Fuel;
                df.DrainFuel(1.0);
                bool noDrain = Math.Abs(tnk.Fuel - held) < 1e-9;

                Check("radial-engine fuel draw", drained && noDrain);
            }

            // 38. RadialThrusting gate: true only with an ignited, fuelled off-axis engine AND a nonzero
            //     lateral command; an all-axial stack never reports it even while commanding Q/E.
            {
                var r = new Vessels.Vessel();
                r.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                r.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 400 });
                r.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Lateral Thruster (Q/E)")) { Ignited = true });
                bool noCmd = !r.RadialThrusting;
                r.RcsCommand = new Vec2d(1, 0);
                bool firing = r.RadialThrusting;

                var ax = new Vessels.Vessel { RcsCommand = new Vec2d(1, 0) };
                ax.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                ax.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Tank T400")) { Fuel = 400 });
                ax.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Terrier")) { Ignited = true });
                bool axialNever = !ax.RadialThrusting;

                Check("radial-thrusting gate", noCmd && firing && axialNever);
            }

            // 38c. lateral thruster launch flow: a thruster mounted through the editor path (AddRadial)
            //      defaults to welded (KEEP, not a jettisoned strap-on), ignites with its host engine's
            //      stage, adds no spurious jettison stage, and fires on the matching Q/E command after
            //      staging. Guards the design->Instantiate->FireNext wiring the checks above bypass by
            //      hand-building the vessel with Ignited/RadialSide already set.
            {
                var d = new Vessels.VesselDesign();
                d.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Pod Mk1")));
                var tankEntry = new Vessels.StackEntry(Parts.PartCatalog.Get("Tank T400"));
                tankEntry.AddRadial(Parts.PartCatalog.Get("Lateral Thruster (Q/E)"), side: 0);   // right side
                d.Stack.Add(tankEntry);
                d.Stack.Add(new Vessels.StackEntry(Parts.PartCatalog.Get("Terrier")));

                var v = d.Instantiate();
                v.Heading = Math.PI / 2; v.Throttle = 0;   // Right = (1,0); zero throttle isolates the lateral engine

                Parts.Part thr = null;
                foreach (var p in v.Parts) foreach (var rr in p.Radials) if (rr.Def.IsLateralThruster) thr = rr;
                bool welded = thr != null && !thr.RadialSeparate;          // KEEP, not strap-on
                bool offBefore = thr != null && !thr.Ignited;             // dead until its stage fires

                Vessels.Staging.FireNext(v);                              // fire stage 0 (liftoff)
                bool litWithEngine = thr != null && thr.Ignited && thr.FireStage == 0;
                bool oneStage = Vessels.Staging.MaxStage(v.Parts) == 0;   // no extra "Drop boosters" stage

                double rt = Parts.PartCatalog.Get("Lateral Thruster (Q/E)").Thrust;
                v.RcsCommand = new Vec2d(-1, 0);                          // right-mounted thruster fires on -Right (Q)
                bool fires = (v.ThrustVector + v.Right * rt).Length < 1e-3;
                v.RcsCommand = Vec2d.Zero;
                bool silent = v.ThrustVector.Length < 1e-6;

                Check("lateral thruster launch flow", welded && offBefore && litWithEngine && oneStage && fires && silent);
            }

            // 30. events-only stage list: a pod+chute+tank+engine stack lists exactly an ignition then a
            //     "Parachute" event (never a "Burn" row), and the chute stage is a deploy, not a drop.
            {
                var stack = new System.Collections.Generic.List<Parts.PartDef>
                {
                    Parts.PartCatalog.Get("Pod Mk1"), Parts.PartCatalog.Get("Parachute"),
                    Parts.PartCatalog.Get("Tank T400"), Parts.PartCatalog.Get("Terrier"),
                };
                var stages = Vessels.Staging.ComputeStages(stack);
                bool noBurn = true, hasChute = false;
                foreach (var st in stages)
                {
                    if (st.Action == "Burn") noBurn = false;
                    if (st.Action == "Parachute") { hasChute = true; if (!st.Chute || st.Decouples) hasChute = false; }
                }
                bool order = stages.Count == 2 && stages[0].Action == "Liftoff" && stages[1].Action == "Parachute";
                Check("events-only stage list", noBurn && hasChute && order);
            }

            // 31. per-stage dV is credited exactly once (no double-count): for a two-stage rocket the summed
            //     stage dV equals the lower + upper rocket-equation dV, and only the two thrust stages carry it.
            {
                var pod = Parts.PartCatalog.Get("Pod Mk1"); var t400 = Parts.PartCatalog.Get("Tank T400");
                var terrier = Parts.PartCatalog.Get("Terrier"); var dec = Parts.PartCatalog.Get("Decoupler");
                var t800 = Parts.PartCatalog.Get("Tank T800"); var swivel = Parts.PartCatalog.Get("Swivel");
                var stack = new System.Collections.Generic.List<Parts.PartDef> { pod, t400, terrier, dec, t800, swivel };

                const double g = 9.81;
                double mUpper = pod.DryMass + t400.DryMass + t400.FuelCapacity + terrier.DryMass;
                double dvUpper = terrier.Isp * g * Math.Log(mUpper / (mUpper - t400.FuelCapacity));
                double mFull = mUpper + dec.DryMass + t800.DryMass + t800.FuelCapacity + swivel.DryMass;
                double dvLower = swivel.Isp * g * Math.Log(mFull / (mFull - t800.FuelCapacity));
                double expect = dvUpper + dvLower;

                var stages = Vessels.Staging.ComputeStages(stack);
                double totalDv = 0; int dvStages = 0;
                foreach (var st in stages) { totalDv += st.DeltaV; if (st.DeltaV > 0) dvStages++; }
                Check("stage dV credited once", dvStages == 2 && Math.Abs(totalDv - expect) < 1.0);
            }

            // 32. FireNext walks the displayed events in order: ignite -> decouple+ignite -> parachute, then
            //     stops. The engine lights, the chute deploys, the spent stage is gone, CurrentStage ends past
            //     the last event, and a further press is a no-op.
            {
                var v = new Vessels.Vessel();
                var pod = new Parts.Part(Parts.PartCatalog.Get("Pod Mk1"));
                var chute = new Parts.Part(Parts.PartCatalog.Get("Parachute"));
                var t400 = new Parts.Part(Parts.PartCatalog.Get("Tank T400"));
                var terrier = new Parts.Part(Parts.PartCatalog.Get("Terrier"));
                var deco = new Parts.Part(Parts.PartCatalog.Get("Decoupler"));
                var t800 = new Parts.Part(Parts.PartCatalog.Get("Tank T800"));
                var swivel = new Parts.Part(Parts.PartCatalog.Get("Swivel"));
                foreach (var p in new[] { pod, chute, t400, terrier, deco, t800, swivel }) v.Parts.Add(p);

                Vessels.Staging.FireNext(v);                         // S0 liftoff
                bool s0 = swivel.Ignited && !terrier.Ignited && !chute.Deployed;
                Vessels.Staging.FireNext(v);                         // S1 decouple + ignite upper
                bool s1 = terrier.Ignited && !v.Parts.Contains(swivel);
                Vessels.Staging.FireNext(v);                         // S2 parachute
                bool s2 = chute.Deployed;
                int stageAfter = v.CurrentStage;
                Vessels.Staging.FireNext(v);                         // nothing left -> no-op
                Check("FireNext walks events in order", s0 && s1 && s2 && stageAfter > 2 && v.CurrentStage == stageAfter);
            }

            // 33. a radial parachute deploys (it is never jettisoned as a strap-on) and stays attached to its
            //     host, and shows up as a "Parachute" event rather than "Drop boosters".
            {
                var v = new Vessels.Vessel();
                v.Parts.Add(new Parts.Part(Parts.PartCatalog.Get("Pod Mk1")));
                var core = new Parts.Part(Parts.PartCatalog.Get("Tank T400"));
                var radChute = new Parts.Part(Parts.PartCatalog.Get("Radial Parachute"));   // RadialSeparate defaults true
                core.Radials.Add(radChute);
                v.Parts.Add(core);

                bool chuteEvent = false, noDrop = true;
                foreach (var st in Vessels.Staging.ComputeStages(v.Parts))
                {
                    if (st.Action == "Parachute" && st.Chute) chuteEvent = true;
                    if (st.Action == "Drop boosters") noDrop = false;
                }

                Vessels.Vessel debris = null;
                for (int k = 0; k < 6 && !radChute.Deployed; k++) { var d = Vessels.Staging.FireNext(v); if (d != null) debris = d; }
                bool deployedAttached = radChute.Deployed && core.Radials.Contains(radChute) && debris == null;
                Check("radial parachute deploys not drops", chuteEvent && noDrop && deployedAttached);
            }

            // 30. mission-cancelable defaults: a fresh vessel can be scrapped back to the editor (true), and
            //     has not yet left the launch site, until first touch/dock flips MissionCancelable off.
            {
                var v = new Vessels.Vessel();
                Check("mission cancelable default", v.MissionCancelable && !v.HasLeftLaunchSite);
            }

            // 31. balance.json tunables load with sane, gentler-than-legacy values (and survive an absent file
            //     via the in-code defaults). Module break base rate must be far below the old 1/(200 h).
            {
                Core.Balance.Load();
                bool positive = Core.Balance.WearPerSec > 0 && Core.Balance.BreakBaseRate > 0
                                && Core.Balance.RadDecayPerSec > 0 && Core.Balance.InfectBaseRate > 0
                                && Core.Balance.OxygenPerCrew > 0 && Core.Balance.LsDeathTime > 0;
                bool gentler = Core.Balance.BreakBaseRate < 1.0 / (500 * 3600)      // breaks rarer than every 500 h
                               && Core.Balance.InfectBaseRate < 1.0 / (4000 * 3600); // infection rarer than every 4000 h
                Check("balance tunables", positive && gentler);
            }

            // 32. radiation belt retune: Earth's belt is a thin, low-dose band (a transit hazard), not the old
            //     wide instant-kill blanket. A 400 km LEO sits safely below it; a 3000 km orbit is inside it.
            {
                Physics.BodyCatalog.Load();
                var earth = Physics.BodyCatalog.All.Find(b => b.Name == "Earth");
                bool retuned = earth != null && earth.RadBeltDose > 0 && earth.RadBeltDose <= 0.1
                               && earth.RadBeltOuterKm <= 10000 && earth.RadBeltInnerKm >= 500;
                bool leoSafe = earth != null && (400 < earth.RadBeltInnerKm || 400 > earth.RadBeltOuterKm);
                bool beltHit = earth != null && 3000 >= earth.RadBeltInnerKm && 3000 <= earth.RadBeltOuterKm;
                Check("radiation belt retune", retuned && leoSafe && beltHit);
            }

            // 33. pre-fit defaults: crewed pods ship a life-support buffer + battery; probes ship power + comms;
            //     and a part's default loadout always fits inside its slot budget.
            {
                bool DefaultsFit(string partName)
                {
                    var d = Parts.PartCatalog.Get(partName);
                    if (d == null) return false;
                    int used = 0;
                    foreach (var id in d.DefaultModules)
                    {
                        var m = Parts.ModuleCatalog.GetById(id) ?? Parts.ModuleCatalog.Get(id);
                        if (m == null) return false;
                        used += m.SlotCost;
                    }
                    return used <= d.Slots;
                }
                var pod = Parts.PartCatalog.Get("Pod Mk1");
                var probe = Parts.PartCatalog.Get("Probe Core");
                bool podOk = pod != null && pod.DefaultModules.Contains("life-support") && pod.DefaultModules.Contains("battery");
                bool probeOk = probe != null && probe.DefaultModules.Contains("battery") && probe.DefaultModules.Contains("antenna");
                bool fit = DefaultsFit("Pod Mk1") && DefaultsFit("Big Pod") && DefaultsFit("Inflatable Habitat")
                           && DefaultsFit("Probe Core");
                Check("part pre-fit defaults", podOk && probeOk && fit);
            }

            string res = $"Physics self-test: {pass}/{total} PASS";
            if (fails.Count > 0) res += "  FAILED: " + string.Join(", ", fails);
            return res;
        }
    }
}
