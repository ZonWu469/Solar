using System;
using System.Collections.Generic;
using Solar.Core;
using Solar.Parts;
using Solar.Physics;

namespace Solar.Vessels
{
    /// <summary>
    /// A flying (or landed) rocket. Position is the base of the stack, relative to the
    /// current SOI body's center; Heading is the world angle of the rocket's "up" axis.
    /// </summary>
    public sealed class Vessel
    {
        public readonly List<Part> Parts = new();
        public CelestialBody Body;
        public Vec2d Position;
        public Vec2d Velocity;
        public double Heading = Math.PI / 2;
        public double AngularVelocity;   // rad/s, signed (+ CCW). Persists as momentum in vacuum; damped in atmosphere.
        public double Throttle;
        public bool OnRails;
        public OrbitalElements Orbit;
        public bool Landed;
        public bool Destroyed;
        public bool IsColony;        // a landed base the player has established (see Colony); enables offline production
        public bool EnginesIgnited;  // first stage has been fired
        public int CurrentStage;     // next stage index to fire (0 = first); advanced by Staging.FireNext
        public bool IsDebris;

        // RCS translation: per-frame body-frame command (set by the flight scene from input),
        // x = right(+)/left(-), y = fore(+, along Up)/aft(-); each component in [-1,1].
        public Vec2d RcsCommand;
        public bool RcsEnabled;

        // resources (ship-wide pools fed by slot modules)
        public double ElectricCharge;
        public double Monoprop;             // monopropellant for RCS translation
        public double PendingScience;       // queued experiment data still being transmitted (transient)
        public double Water, Oxygen, Food;  // life-support resources (consumed per crew member)
        public bool LifeSupportOk = true;   // false once crew run out of any life-support resource
        private double _lsDeprivedFor;      // seconds any LS resource has been empty while crewed
        /// <summary>Names of crew that died this tick; the flight scene drains these to raise toasts.</summary>
        public readonly List<string> RecentDeaths = new();

        // per-crew life-support consumption (units/s) and how long crew survive total deprivation
        public const double OxygenPerCrew = 0.5;
        public const double WaterPerCrew = 0.2;
        public const double FoodPerCrew = 0.1;
        public const double LsDeathTime = 21600;  // 6 h of an empty resource kills one crew member

        public const double G0 = 9.81;

        public Vec2d Up => Vec2d.FromAngle(Heading);
        public double Altitude => Position.Length - Body.SurfaceRadiusAt(Position.Angle());

        /// <summary>The axial stack plus every radially-attached part — the single iteration source for
        /// mass / drag / power / thrust aggregates. Height and stage boundaries use the axial stack only.</summary>
        public IEnumerable<Part> AllParts()
        {
            foreach (var p in Parts)
            {
                yield return p;
                foreach (var r in p.Radials) yield return r;
            }
        }

        public double TotalMass
        {
            get { double m = 0; foreach (var p in AllParts()) m += p.Mass; return Math.Max(m, 1); }
        }

        public double TotalHeight
        {
            get { double h = 0; foreach (var p in Parts) h += p.Def.Height; return h; }
        }

        public double TotalCdA
        {
            get
            {
                double c = 0;
                foreach (var p in AllParts())
                {
                    c += p.Def.CdA;
                    if (p.Def.Kind == PartKind.Parachute && p.Deployed) c += p.Def.DeployedCdA;
                }
                // a nose cone at the top of the stack streamlines the whole vehicle
                if (Parts.Count > 0 && Parts[0].Def.Kind == PartKind.Aero) c *= 0.8;
                return c;
            }
        }

        /// <summary>Count of fitted landing-leg modules; each raises the survivable touchdown speed.</summary>
        public int LandingLegs
        {
            get { int n = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.LandingLeg) n++; return n; }
        }

        /// <summary>A bare hull survives only a gentle touchdown; landing legs/gear are what make a
        /// hard landing survivable. Tuned so a pod alone clears ~6 m/s, while a properly legged lander
        /// (gear's authored <see cref="PartDef.ImpactTolerance"/> from parts.json + a margin per leg
        /// module) clears ~20-30 m/s.</summary>
        public const double BareLandingSpeed = 6.0;
        public const double LandingLegMargin = 5.0;

        /// <summary>Touchdown speed the vessel survives (see <see cref="BareLandingSpeed"/>).</summary>
        public double SafeLandingSpeed
        {
            get
            {
                double t = BareLandingSpeed + LandingLegMargin * LandingLegs;
                foreach (var p in AllParts()) t += p.Def.ImpactTolerance;
                return t;
            }
        }

        /// <summary>Whether a touchdown at <paramref name="impactSpeed"/> (m/s) is survivable. A small
        /// tolerance keeps the boundary from being brittle (an impact shown as "6" doesn't crash a craft
        /// rated for 6). Pure so it can be exercised by the self-tests.</summary>
        public bool SurvivesTouchdown(double impactSpeed) => impactSpeed <= SafeLandingSpeed + 0.05;

        public bool ChuteDeployed
        {
            get { foreach (var p in AllParts()) if (p.Def.Kind == PartKind.Parachute && p.Deployed) return true; return false; }
        }

        public bool HasFins
        {
            get { foreach (var p in Parts) if (p.Def.Kind == PartKind.Fins) return true; return false; }
        }

        /// <summary>Count of fitted reaction-wheel modules. They provide attitude authority in vacuum,
        /// but only while the vessel has electric charge (see <see cref="ControlTorque"/>).</summary>
        public int ReactionWheels
        {
            get { int n = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.ReactionWheel) n++; return n; }
        }

        /// <summary>Dynamic pressure (Pa) on the vessel right now: zero in vacuum, rising with air
        /// density and speed. Drives how much authority aerodynamic fins provide.</summary>
        public double DynamicPressure
        {
            get { double rho = Body?.Atmo?.DensityAt(Altitude) ?? 0; return 0.5 * rho * Velocity.LengthSquared; }
        }

        /// <summary>Approximate moment of inertia (kg·m²) of the stack about its center, modeled as a
        /// slender box of the total mass with the stack's height and widest part. Heavier and longer
        /// craft resist rotation more, so angular acceleration falls off with size and mass.</summary>
        public double MomentOfInertia
        {
            get
            {
                double maxW = 0;
                foreach (var p in Parts) maxW = Math.Max(maxW, p.Def.Width);
                double L = Math.Max(TotalHeight, 1), W = Math.Max(maxW, 1);
                return Math.Max(TotalMass * (L * L + W * W) / 12.0, 1);
            }
        }

        /// <summary>Best command-part steering authority (rad/s², the authored <see cref="PartDef.ControlAuthority"/>
        /// from parts.json) over all fitted pods — the minimum angular acceleration the craft can command
        /// regardless of its mass. Zero if no command part is present.</summary>
        public double PodControlAccel
        {
            get { double a = 0; foreach (var p in AllParts()) if (p.Def.Kind == PartKind.Pod && p.Def.ControlAuthority > a) a = p.Def.ControlAuthority; return a; }
        }

        /// <summary>Total attitude-control torque (N·m): a command-part minimum (its authored authority
        /// times the moment of inertia, so it holds at any mass), plus reaction wheels (only while
        /// powered), RCS blocks (gated like translation), and aerodynamic fins (atmosphere only).
        /// Angular acceleration is this divided by <see cref="MomentOfInertia"/>.</summary>
        public double ControlTorque
        {
            get
            {
                double podMin = PodControlAccel * MomentOfInertia;                        // size-independent minimum
                double wheels = ElectricCharge > 0 ? 45000.0 * ReactionWheels : 0;        // gyros, only with power
                double rcs = (RcsEnabled && Monoprop > 0 && ElectricCharge > 0) ? 8000.0 * RcsBlocks : 0;
                double fins = HasFins ? 30000.0 * Math.Clamp(DynamicPressure / 4000.0, 0, 1) : 0;
                return podMin + wheels + rcs + fins;
            }
        }

        /// <summary>Rotation-rate ceiling (rad/s). Reaction wheels let the craft spin up faster and reach
        /// a higher cap, but it stays bounded so fine attitude control is preserved.</summary>
        public double MaxTurnRate => Math.Min(0.35 + 0.22 * (ElectricCharge > 0 ? ReactionWheels : 0), 1.6);

        /// <summary>Whether the craft can run attitude hold (SAS) at all: a fitted SAS-capable command
        /// part (<see cref="PartDef.Sas"/>) or any reaction wheel. Power is checked by <see cref="SasAvailable"/>.</summary>
        public bool HasSas
        {
            get { foreach (var p in AllParts()) if (p.Def.Sas) return true; return ReactionWheels > 0; }
        }

        /// <summary>SAS can be engaged right now: it needs a SAS-capable part and electric charge,
        /// mirroring how reaction wheels in <see cref="ControlTorque"/> are gated on power.</summary>
        public bool SasAvailable => HasSas && ElectricCharge > 0;

        /// <summary>Inclusive index ranges between decouplers (decouplers belong to no segment).</summary>
        public List<(int start, int end)> Segments()
        {
            var res = new List<(int, int)>();
            int s = 0;
            for (int i = 0; i < Parts.Count; i++)
            {
                // decouplers are stage boundaries; docking ports isolate docked modules so the active
                // stage never fires or drains across a dock (undocking is a separate, explicit action).
                if (Parts[i].Def.Kind == PartKind.Decoupler)
                {
                    if (i > s) res.Add((s, i - 1));
                    s = i + 1;
                }
                else if (Parts[i].Def.Kind == PartKind.DockingPort)
                {
                    if (i > s) res.Add((s, i - 1));
                    s = i + 1;   // split here (like a gap) without making the port a stage-fire point
                }
            }
            if (s < Parts.Count) res.Add((s, Parts.Count - 1));
            return res;
        }

        /// <summary>Liquid fuel in a segment (solid boosters carry self-contained fuel that does not
        /// feed liquid engines, so it is excluded here). Radial tanks attached to parts in the segment
        /// cross-feed into the same pool.</summary>
        public double SegmentFuel((int start, int end) seg)
        {
            double f = 0;
            for (int i = seg.start; i <= seg.end; i++)
            {
                if (Parts[i].Def.Kind != PartKind.SolidBooster) f += Parts[i].Fuel;
                foreach (var r in Parts[i].Radials)
                    if (r.Def.Kind != PartKind.SolidBooster) f += r.Fuel;
            }
            return f;
        }

        /// <summary>Current thrust (N): throttleable liquid engines plus solid boosters (always full).</summary>
        public double CurrentThrust => LiquidThrust * Throttle + SolidThrust;

        /// <summary>Thrust achievable right now (liquid at full throttle + solids), for TWR/preview.</summary>
        public double MaxAvailableThrust => LiquidThrust + SolidThrust;

        /// <summary>Throttleable thrust from ignited liquid engines whose segment still has fuel
        /// (radial engines on a segment's parts draw from the same cross-fed pool).</summary>
        public double LiquidThrust
        {
            get
            {
                double t = 0;
                foreach (var seg in Segments())
                {
                    if (SegmentFuel(seg) <= 0) continue;
                    for (int i = seg.start; i <= seg.end; i++)
                    {
                        if (Parts[i].Def.Kind == PartKind.Engine && Parts[i].Ignited) t += Parts[i].Def.Thrust;
                        foreach (var r in Parts[i].Radials)
                            if (r.Def.Kind == PartKind.Engine && r.Ignited) t += r.Def.Thrust;
                    }
                }
                return t;
            }
        }

        /// <summary>Full thrust from ignited solid boosters (axial or radial) that still have fuel.</summary>
        public double SolidThrust
        {
            get
            {
                double t = 0;
                foreach (var p in AllParts())
                    if (p.Def.Kind == PartKind.SolidBooster && p.Ignited && p.Fuel > 0)
                        t += p.Def.Thrust;
                return t;
            }
        }

        /// <summary>Drain fuel for dt seconds: liquid engines scale with throttle; solids burn their
        /// own fuel at full rate once ignited (throttle-independent).</summary>
        public void DrainFuel(double dt)
        {
            // solid boosters (axial or radial): each consumes its own fuel at full flow until empty
            foreach (var p in AllParts())
                if (p.Def.Kind == PartKind.SolidBooster && p.Ignited && p.Fuel > 0)
                    p.Fuel = Math.Max(0, p.Fuel - p.Def.FuelFlowAtMax * dt);

            if (Throttle <= 0) return;
            foreach (var seg in Segments())
            {
                double flow = 0;
                for (int i = seg.start; i <= seg.end; i++)
                {
                    if (Parts[i].Def.Kind == PartKind.Engine && Parts[i].Ignited) flow += Parts[i].Def.FuelFlowAtMax;
                    foreach (var r in Parts[i].Radials)
                        if (r.Def.Kind == PartKind.Engine && r.Ignited) flow += r.Def.FuelFlowAtMax;
                }
                if (flow <= 0) continue;
                double amount = flow * Throttle * dt;
                double avail = SegmentFuel(seg);
                if (avail <= 0) continue;
                double factor = Math.Max(0, 1 - amount / avail);
                for (int i = seg.start; i <= seg.end; i++)
                {
                    if (Parts[i].Def.Kind != PartKind.SolidBooster) Parts[i].Fuel *= factor;
                    foreach (var r in Parts[i].Radials)
                        if (r.Def.Kind != PartKind.SolidBooster) r.Fuel *= factor;
                }
            }
        }

        // ----- RCS translation -----

        public const double RcsThrustPerBlock = 1000.0;  // N of translation authority per RCS block
        public const double RcsIsp = 240.0;              // s, monopropellant specific impulse

        /// <summary>Count of fitted RCS thruster-block modules.</summary>
        public int RcsBlocks
        {
            get { int n = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.RCS) n++; return n; }
        }

        /// <summary>Total RCS translation thrust (N) available right now: blocks only count while RCS is
        /// enabled, monopropellant remains, and the avionics have power.</summary>
        public double RcsThrust =>
            RcsEnabled && Monoprop > 0 && ElectricCharge > 0 ? RcsThrustPerBlock * RcsBlocks : 0;

        /// <summary>True while RCS is actively translating this frame (gates physics + warp).</summary>
        public bool RcsActive => RcsThrust > 0 && (RcsCommand.X != 0 || RcsCommand.Y != 0);

        /// <summary>World-frame RCS acceleration (m/s2) for the current command: <see cref="Up"/> is the
        /// fore axis; right is Up rotated clockwise in screen space, per the angle/screen convention.</summary>
        public Vec2d RcsAccel
        {
            get
            {
                if (!RcsActive) return Vec2d.Zero;
                Vec2d up = Up;                       // (cos H, sin H) -- fore (+y command)
                Vec2d right = new Vec2d(up.Y, -up.X); // Up rotated -90 deg -- right (+x command)
                Vec2d dir = right * RcsCommand.X + up * RcsCommand.Y;
                double dl = dir.Length;
                if (dl <= 0) return Vec2d.Zero;
                if (dl > 1) dir /= dl;               // clamp diagonal command magnitude to full authority
                return dir * (RcsThrust / TotalMass);
            }
        }

        /// <summary>Burn monopropellant for dt seconds of the current RCS command (thrust/Isp/g0 flow).</summary>
        public void DrainMonoprop(double dt)
        {
            if (!RcsActive || dt <= 0) return;
            double cmd = Math.Min(1, RcsCommand.Length);
            double flow = RcsThrust * cmd / (RcsIsp * G0);
            Monoprop = Math.Max(0, Monoprop - flow * dt);
        }

        // ----- resources -----

        /// <summary>Built-in command-pod battery and the constant avionics draw it spends to run
        /// reaction wheels / instruments, so electric charge always matters even on a bare pod.</summary>
        public const double PodEcCapacity = 100;
        public const double PodEcDraw = 0.05;

        public double EcCapacity
        {
            get
            {
                double c = 0;
                foreach (var p in AllParts())
                {
                    if (p.Def.Kind == PartKind.Pod) c += PodEcCapacity;
                    foreach (var m in p.Modules) c += m.Def.EcCapacity;
                }
                return c;
            }
        }

        /// <summary>Monopropellant storage (fed by tank modules), the propellant RCS translation burns.</summary>
        public double MonopropCapacity
        {
            get { double c = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) if (m.Def.Kind == ModuleKind.Tank) c += m.Def.FuelCapacity; return c; }
        }

        public double WaterCapacity
        {
            get { double c = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) c += m.Def.WaterCapacity; return c; }
        }

        public double OxygenCapacity
        {
            get { double c = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) c += m.Def.OxygenCapacity; return c; }
        }

        public double FoodCapacity
        {
            get { double c = 0; foreach (var p in AllParts()) foreach (var m in p.Modules) c += m.Def.FoodCapacity; return c; }
        }

        /// <summary>Any life-support storage at all (drives whether the HUD shows the LS panel).</summary>
        public double LsCapacity => WaterCapacity + OxygenCapacity + FoodCapacity;

        /// <summary>Total seats across the vessel (pods + crew cabins).</summary>
        public int SeatCount { get { int n = 0; foreach (var p in AllParts()) n += p.SeatCount; return n; } }

        /// <summary>Crew aboard right now.</summary>
        public int CrewCount { get { int n = 0; foreach (var p in AllParts()) n += p.Crew.Count; return n; } }

        public IEnumerable<CrewMember> AllCrew()
        {
            foreach (var p in AllParts()) foreach (var c in p.Crew) yield return c;
        }

        /// <summary>Total liquid (non-solid) fuel aboard, across axial and radial tanks.</summary>
        public double TotalLiquidFuel
        {
            get { double f = 0; foreach (var p in AllParts()) if (p.Def.Kind != PartKind.SolidBooster) f += p.Fuel; return f; }
        }

        /// <summary>Pull <paramref name="amount"/> kg of liquid fuel from wherever it's available
        /// (used by fuel cells, which draw from any tank, not a single stage).</summary>
        private void DrainAnyFuel(double amount)
        {
            foreach (var p in AllParts())
            {
                if (amount <= 0) break;
                if (p.Def.Kind == PartKind.SolidBooster) continue;
                double take = Math.Min(p.Fuel, amount);
                p.Fuel -= take; amount -= take;
            }
        }

        /// <summary>Spend <paramref name="amount"/> kg of liquid fuel from anywhere on the vessel as a raw
        /// material cost (colony construction). Returns false and spends nothing if the stock is short.</summary>
        public bool TrySpendFuel(double amount)
        {
            if (amount <= 0) return true;
            if (TotalLiquidFuel < amount) return false;
            DrainAnyFuel(amount);
            return true;
        }

        /// <summary>Instantaneous electric-charge production and draw (per second) given the current
        /// solar panel / drill states and sunlight. Single source of truth shared by the live tick
        /// (<see cref="UpdateResources"/>) and the HUD readout.</summary>
        public void EcRates(double ut, Universe u, out double prod, out double draw)
        {
            prod = 0; draw = 0;
            double sun = SolarFactor(ut, u);
            bool hasFuel = TotalLiquidFuel > 0;
            foreach (var p in AllParts())
            {
                if (p.Def.Kind == PartKind.Pod) draw += PodEcDraw;   // avionics
                foreach (var m in p.Modules)
                    switch (m.Def.Kind)
                    {
                        case ModuleKind.Rtg: prod += m.Def.EcProduce; break;
                        case ModuleKind.SolarPanel: if (m.Active) prod += m.Def.EcProduce * sun; break;
                        case ModuleKind.FuelCell: if (m.Active && hasFuel) prod += m.Def.EcProduce; break;
                        case ModuleKind.LifeSupport: if (!m.Def.Activatable || m.Active) draw += m.Def.EcDraw; break;
                        case ModuleKind.Harvester: if (m.Active && Landed) draw += m.Def.EcDraw; break;
                        case ModuleKind.ReactionWheel: draw += m.Def.EcDraw; break;
                        case ModuleKind.Science: if (m.Active) draw += m.Def.EcDraw; break;
                        case ModuleKind.Antenna: if (m.Active) draw += m.Def.EcDraw; break;
                        case ModuleKind.Light: if (m.Active) draw += m.Def.EcDraw; break;
                    }
            }
        }

        /// <summary>Whether a fitted module is currently doing its job, applying the same resource gates
        /// (EC / fuel / landed / deployed) that <see cref="EcRates"/> uses. Drives the flight HUD status
        /// lights, so keep the per-kind gates here in sync with EcRates. Passive parts with no on/off
        /// concept (batteries, tanks, storage, landing legs) report true ("installed/ready").</summary>
        public bool ModuleFunctioning(ModuleInstance m, double ut, Universe u)
        {
            bool ec = ElectricCharge > 0;
            switch (m.Def.Kind)
            {
                case ModuleKind.Rtg: return true;                                  // passive generator, always on
                case ModuleKind.SolarPanel: return m.Active && SolarFactor(ut, u) > 0;
                case ModuleKind.FuelCell: return m.Active && TotalLiquidFuel > 0;
                case ModuleKind.Harvester: return m.Active && Landed && ec;
                case ModuleKind.ReactionWheel: return ec;
                case ModuleKind.Science: return m.Active && ec;
                case ModuleKind.Antenna: return m.Active && ec;
                case ModuleKind.Light: return m.Active && ec;
                case ModuleKind.LifeSupport: return (!m.Def.Activatable || m.Active) && (m.Def.EcDraw <= 0 || ec);
                case ModuleKind.RCS: return RcsEnabled && Monoprop > 0 && ec;
                default: return true;                                              // Battery / Tank / Storage / LandingLeg
            }
        }

        public bool HasCrew => CrewCount > 0;

        /// <summary>Tick electric charge, life support and harvesting for dt seconds (dt includes warp).</summary>
        public void UpdateResources(double dt, double ut, Universe u)
        {
            if (dt <= 0 || Destroyed) return;
            double ecCap = EcCapacity;

            // electric charge production vs draw (shared with the HUD readout)
            EcRates(ut, u, out double prod, out double draw);

            // life-support recycler regen (water/oxygen/food per second) from active, powered recyclers
            double waterRegen = 0, oxygenRegen = 0, foodRegen = 0;
            foreach (var p in AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.LifeSupport && (!m.Def.Activatable || m.Active))
                    { waterRegen += m.Def.WaterRegen; oxygenRegen += m.Def.OxygenRegen; foodRegen += m.Def.FoodRegen; }

            // fuel cells: burn a liquid-fuel trickle while active and fuel remains (EC already counted in EcRates)
            double fcDraw = 0;
            foreach (var p in AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.FuelCell && m.Active) fcDraw += m.Def.FuelDraw;
            if (fcDraw > 0 && TotalLiquidFuel > 0) DrainAnyFuel(fcDraw * dt);

            ElectricCharge = Math.Clamp(ElectricCharge + (prod - draw) * dt, 0, Math.Max(ecCap, 0));
            bool ecOk = ElectricCharge > 0 || prod >= draw;

            // life support: each crew member consumes water/oxygen/food; recyclers regen (when powered)
            int crew = CrewCount;
            double regenGate = ecOk ? 1 : 0;   // recyclers stop without power; stored resources still drain
            Oxygen = Math.Clamp(Oxygen + (oxygenRegen * regenGate - crew * OxygenPerCrew) * dt, 0, OxygenCapacity);
            Water = Math.Clamp(Water + (waterRegen * regenGate - crew * WaterPerCrew) * dt, 0, WaterCapacity);
            Food = Math.Clamp(Food + (foodRegen * regenGate - crew * FoodPerCrew) * dt, 0, FoodCapacity);

            bool depleted = crew > 0 && (Oxygen <= 0 || Water <= 0 || Food <= 0);
            LifeSupportOk = crew == 0 || !depleted;
            if (depleted)
            {
                _lsDeprivedFor += dt;
                while (_lsDeprivedFor >= LsDeathTime && CrewCount > 0)
                {
                    _lsDeprivedFor -= LsDeathTime;
                    KillOneCrew();
                }
            }
            else _lsDeprivedFor = 0;

            // harvesting: drills refill fuel while landed + powered. A standalone craft fills its own
            // stage segment; a connected surface base (compound vessel) distributes fuel across the whole
            // assembly, so a colony's miners can refuel a docked lander from base stock.
            if (Landed && ElectricCharge > 0)
                for (int i = 0; i < Parts.Count; i++)
                    foreach (var m in Parts[i].Modules)
                        if (m.Def.Kind == ModuleKind.Harvester && m.Active && m.Def.FuelProduce > 0)
                        {
                            if (DockLinks.Count > 0) AddFuelAnywhere(m.Def.FuelProduce * dt);
                            else AddFuelToSegment(i, m.Def.FuelProduce * dt);
                        }
        }

        /// <summary>Comm signal strength in [0,1]: full within the strongest active antenna's range,
        /// falling to zero at twice that range. The link is to the home ground station (Earth) or to any
        /// other vessel carrying an active relay antenna (one hop, KSP-CommNet-lite). 0 with no antenna.</summary>
        public double SignalStrength(double ut, Universe u, IEnumerable<Vessel> others)
        {
            double best = 0;
            foreach (var p in AllParts())
                foreach (var m in p.Modules)
                    if (m.Def.Kind == ModuleKind.Antenna && m.Active && m.Def.Range > best) best = m.Def.Range;
            if (best <= 0) return 0;

            Vec2d me = AbsolutePosition(ut);
            double s = 0;

            // home ground station at Earth (effectively unlimited transmit power)
            var home = u?["Earth"];
            if (home != null)
                s = Math.Max(s, LinkStrength((me - home.AbsolutePositionAt(ut)).Length, best));

            // other vessels with an active relay antenna can rebroadcast (assumed connected onward)
            if (others != null)
                foreach (var o in others)
                {
                    if (o == null || o == this || s >= 1) continue;
                    double relay = 0;
                    foreach (var p in o.AllParts())
                        foreach (var m in p.Modules)
                            if (m.Def.Kind == ModuleKind.Antenna && m.Active && m.Def.Relay && m.Def.Range > relay) relay = m.Def.Range;
                    if (relay <= 0) continue;
                    double combined = Math.Sqrt(best * relay);   // both ends limit the link
                    s = Math.Max(s, LinkStrength((me - o.AbsolutePosition(ut)).Length, combined));
                }
            return Math.Clamp(s, 0, 1);
        }

        private static double LinkStrength(double dist, double range)
            => range <= 0 ? 0 : Math.Clamp(2.0 - dist / range, 0, 1);

        /// <summary>Sunlight intensity factor (inverse-square vs Earth's distance), clamped.</summary>
        private double SolarFactor(double ut, Universe u)
        {
            if (u?.Root == null) return 1;
            double d = (AbsolutePosition(ut) - u.Root.AbsolutePositionAt(ut)).Length;
            if (d <= 1) return 4;
            double refD = u["Earth"]?.Orbit.A ?? d;
            double f = (refD / d) * (refD / d);
            return Math.Clamp(f, 0.02, 4);
        }

        /// <summary>Add fuel to any tank on the vessel that has room (used by a connected surface base,
        /// where mined fuel pools across all docked modules).</summary>
        private void AddFuelAnywhere(double amount)
        {
            foreach (var p in AllParts())
            {
                if (amount <= 0) break;
                if (p.Def.Kind == PartKind.SolidBooster) continue;
                double room = p.Def.FuelCapacity - p.Fuel;
                if (room <= 0) continue;
                double add = Math.Min(room, amount);
                p.Fuel += add; amount -= add;
            }
        }

        /// <summary>Add fuel to the tanks in the stage segment that contains the given part index.</summary>
        private void AddFuelToSegment(int partIndex, double amount)
        {
            foreach (var seg in Segments())
            {
                if (partIndex < seg.start || partIndex > seg.end) continue;
                for (int i = seg.start; i <= seg.end && amount > 0; i++)
                {
                    double room = Parts[i].Def.FuelCapacity - Parts[i].Fuel;
                    if (room <= 0) continue;
                    double add = Math.Min(room, amount);
                    Parts[i].Fuel += add;
                    amount -= add;
                }
                return;
            }
        }

        /// <summary>Remove one living crew member (last-boarded first), mark them KIA on the shared
        /// roster instance, and record the name for the scene to toast.</summary>
        private void KillOneCrew()
        {
            foreach (var p in AllParts())
            {
                if (p.Crew.Count == 0) continue;
                var c = p.Crew[p.Crew.Count - 1];
                p.Crew.RemoveAt(p.Crew.Count - 1);
                c.Status = CrewStatus.KIA;
                RecentDeaths.Add(c.Name);
                return;
            }
        }

        /// <summary>Move a crew member to another part on this vessel that has a free seat. Returns false
        /// if the source has no crew or the destination is full.</summary>
        public bool TransferCrew(Part from, Part to)
        {
            if (from == null || to == null || from == to || from.Crew.Count == 0) return false;
            if (to.Crew.Count >= to.SeatCount) return false;
            var c = from.Crew[from.Crew.Count - 1];
            from.Crew.RemoveAt(from.Crew.Count - 1);
            to.Crew.Add(c);
            return true;
        }

        public void GoOnRails(double ut)
        {
            Orbit = Kepler.ElementsFromState(Position, Velocity, Body.Mu, ut);
            OnRails = true;
        }

        public void GoOffRails(double ut)
        {
            (Position, Velocity) = Kepler.StateAtTime(Orbit, ut);
            OnRails = false;
        }

        public void UpdateFromRails(double ut) => (Position, Velocity) = Kepler.StateAtTime(Orbit, ut);

        public Vec2d AbsolutePosition(double ut) => Body.AbsolutePositionAt(ut) + Position;

        public Vec2d AbsoluteVelocity(double ut) => Body.AbsoluteVelocityAt(ut) + Velocity;

        /// <summary>Elements for display: live elements when on rails, computed on the fly otherwise.</summary>
        public OrbitalElements CurrentElements(double ut) =>
            OnRails ? Orbit : Kepler.ElementsFromState(Position, Velocity, Body.Mu, ut);

        // ----- docking / compound assembly -----

        /// <summary>A join between two docking-port parts that were merged into this vessel. The docked
        /// module is the contiguous run of parts starting at <see cref="ModuleStart"/>, so it can be
        /// split back off on undock.</summary>
        public sealed class DockLink
        {
            public Part PortA;       // port that belongs to the part of the stack we keep
            public Part PortB;       // port on the docked-on module
            public int ModuleStart;  // index in Parts where the docked module begins
        }

        /// <summary>Joins recorded when other vessels docked onto this one (newest last).</summary>
        public readonly List<DockLink> DockLinks = new();

        private bool PortOccupied(Part port)
        {
            foreach (var dl in DockLinks) if (dl.PortA == port || dl.PortB == port) return true;
            return false;
        }

        /// <summary>Docking ports not already joined to another vessel.</summary>
        public IEnumerable<Part> FreeDockingPorts()
        {
            foreach (var p in AllParts())
                if (p.Def.Kind == PartKind.DockingPort && !PortOccupied(p)) yield return p;
        }

        public Part FirstFreeDockingPort()
        {
            foreach (var p in FreeDockingPorts()) return p;
            return null;
        }

        public bool HasFreeDockingPort => FirstFreeDockingPort() != null;

        /// <summary>Dock <paramref name="other"/> onto this vessel: append its parts as a new module,
        /// record the join, and pool the shared resources. The caller is responsible for the rigid-body
        /// snap (position/velocity). Returns false if either port is missing.</summary>
        public bool DockWith(Vessel other, Part myPort, Part theirPort)
        {
            if (other == null || myPort == null || theirPort == null) return false;
            int moduleStart = Parts.Count;
            foreach (var p in other.Parts) Parts.Add(p);
            DockLinks.Add(new DockLink { PortA = myPort, PortB = theirPort, ModuleStart = moduleStart });
            // carry over the other vessel's own joins, shifted into this stack's index space
            foreach (var dl in other.DockLinks)
                DockLinks.Add(new DockLink { PortA = dl.PortA, PortB = dl.PortB, ModuleStart = dl.ModuleStart + moduleStart });
            other.DockLinks.Clear();
            // pool shared resources (capacities recompute from the merged parts automatically)
            ElectricCharge += other.ElectricCharge;
            Monoprop += other.Monoprop;
            Oxygen += other.Oxygen; Water += other.Water; Food += other.Food;
            return true;
        }

        /// <summary>Whether the most-recently docked module can be cleanly detached (it is always the
        /// outermost leaf, so this is true whenever any join exists).</summary>
        public bool CanUndock => DockLinks.Count > 0;

        /// <summary>Detach the most-recently docked module into a new standalone vessel, splitting the
        /// pooled resources by capacity. Returns the detached vessel (already off-rails), or null.</summary>
        public Vessel Undock(double sepImpulse = 0.4)
        {
            if (DockLinks.Count == 0) return null;
            var link = DockLinks[DockLinks.Count - 1];
            DockLinks.RemoveAt(DockLinks.Count - 1);

            var detached = new Vessel
            {
                Body = Body,
                Position = Position,
                Velocity = Velocity,
                Heading = Heading,
                OnRails = false,
            };
            for (int i = Parts.Count - 1; i >= link.ModuleStart; i--)
            {
                detached.Parts.Insert(0, Parts[i]);
                Parts.RemoveAt(i);
            }

            // split pooled resources by the two halves' capacities
            SplitPool(ref ElectricCharge, detached, (v) => v.EcCapacity, x => detached.ElectricCharge = x);
            SplitPool(ref Monoprop, detached, (v) => v.MonopropCapacity, x => detached.Monoprop = x);
            SplitPool(ref Oxygen, detached, (v) => v.OxygenCapacity, x => detached.Oxygen = x);
            SplitPool(ref Water, detached, (v) => v.WaterCapacity, x => detached.Water = x);
            SplitPool(ref Food, detached, (v) => v.FoodCapacity, x => detached.Food = x);

            // small separation push along the line from this vessel's base to the detached module
            Vec2d dir = (detached.Position - Position).Length > 1e-6 ? (detached.Position - Position).Normalized() : Up;
            detached.Velocity += dir * sepImpulse;
            Velocity -= dir * sepImpulse;
            return detached;
        }

        /// <summary>Move the share of a pooled resource that belongs to <paramref name="detached"/>
        /// (proportional to its capacity) out of this vessel's pool and into it.</summary>
        private void SplitPool(ref double pool, Vessel detached, Func<Vessel, double> cap, Action<double> setOther)
        {
            double mine = cap(this), theirs = cap(detached), total = mine + theirs;
            if (total <= 0) { setOther(0); return; }
            double give = pool * (theirs / total);
            setOther(give);
            pool -= give;
        }
    }
}

