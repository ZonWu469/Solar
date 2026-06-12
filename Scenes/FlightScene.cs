using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Solar.Core;
using Solar.Physics;
using Solar.Rendering;
using Solar.UI;
using Solar.Vessels;

namespace Solar.Scenes
{
    /// <summary>
    /// Flight: numeric integration under thrust/atmosphere, analytic on-rails coasting with
    /// exact-time SOI handoffs, time warp, debris, and a KSP-style toggleable map view.
    /// </summary>
    public sealed class FlightScene : Scene
    {
        private const double SubstepDt = 1.0 / 120;
        private const int MaxDebris = 20;
        private const float HandleDist = 52f;   // px from node to a delta-v handle
        private const float HandleHit = 12f;    // px hit radius for handles
        private const float NodeHit = 12f;      // px hit radius for the node body

        private Vessel _vessel;
        private string _shipName = "Ship";
        private readonly ShipState _resume;   // non-null when resuming a saved ship
        private readonly List<Vessel> _debris = new();
        private readonly List<TrackedShip> _others = new();  // other saved ships, co-orbiting passively

        /// <summary>A named, passive co-orbiting ship (drawn + targetable, never simulated under physics).</summary>
        private sealed class TrackedShip { public string Name; public Vessel V; }
        private readonly Camera2D _cam = new();
        private readonly DustField _dust = new();

        private bool _map;
        private double _flightZoom = 0.35;   // m per pixel
        private double _mapZoom;
        private int _focus;                  // 0 = vessel, otherwise body index + 1

        // ----- target / rendezvous -----
        private CelestialBody _targetBody;   // one of _targetBody / _targetVessel is non-null when targeting
        private Vessel _targetVessel;
        private string _targetName;
        private bool _showTargetWindow;
        private bool _showCrew;              // in-flight crew roster / transfer panel
        private bool _caValid;               // closest-approach solution for this frame
        private double _caUT, _caSep, _caRelSpeed;
        private List<ProximityPoint> _prox = new();  // geometric orbit-curve proximities (throttled)
        private double _proxTimer;

        // ----- attitude hold (SAS) -----
        private enum SasMode { Off, Prograde, Retrograde, Target, AntiTarget, RelRetro }
        private SasMode _sas = SasMode.Off;

        private Prediction _pred;
        private double _predTimer;

        private readonly List<Maneuver> _nodes = new();   // chained planned burns (KSP-style), sorted by UT
        private int _dragHandle = -1;       // -1 none; 0/1 prograde +/-; 2/3 radial out/in; 4 = move node
        private int _dragNode = -1;         // index into _nodes being dragged
        private int _hoverHandle = -1;
        private int _hoverNode = -1;        // node under the cursor (handle or body)
        private int _hoverX = -1;           // node whose X delete button is hovered
        private double _dragStartProj, _dragStartValue;
        private double _burnSpent;          // delta-v consumed against the current (nearest future) node
        private double _burnTargetUT = double.NaN;  // UT of the node _burnSpent is keyed to
        private double? _warpTo;            // target UT for an active "warp to maneuver"
        private string _endMessage;
        private string _toast;               // transient milestone notification
        private double _toastT;              // seconds remaining on the toast
        private double _anim;
        private Vec2d _lastCamRel;           // camera position relative to body (kept after a crash)
        private CelestialBody _lastBody;
        private bool _showExitDialog;        // pause / exit-confirmation modal (freezes the sim)
        private bool _exitToTitle;           // deferred: leave to the Title screen on the next Update
        private bool _exitToHub;             // deferred: return to the Space Center hub on the next Update
        private bool _cancelMission;         // deferred: scrap a pre-launch ship and reopen the editor

        public FlightScene(GameContext ctx) : base(ctx) { }

        /// <summary>Resume a previously saved ship (from the tracking station) rather than a fresh launch.</summary>
        public FlightScene(GameContext ctx, ShipState resume) : base(ctx) { _resume = resume; }

        public override void Enter()
        {
            Ctx.Clock.DropToRealtime();
            _endMessage = null;

            if (_resume != null)
            {
                _vessel = _resume.ToVessel(Ctx.Universe, Ctx.State.Roster);
                _shipName = _resume.Name;
                LoadNodes(_resume);
                _burnSpent = 0;
                var b = _vessel.Body ?? Ctx.Universe["Earth"];
                _vessel.Body = b;
                if (_vessel.OnRails) _vessel.UpdateFromRails(Ctx.Clock.UT);
                _lastBody = b;
                _lastCamRel = _vessel.Position;
                _mapZoom = b.SoiRadius * 2.4 / Math.Min(Ctx.W, Ctx.H);
                if (!_vessel.Landed && !_vessel.Destroyed) RefreshPrediction(Ctx.Clock.UT);
                SpawnOthers();
                RestoreTarget(_resume.TargetName);
                return;
            }

            var earth = Ctx.Universe["Earth"];
            _vessel = Ctx.Design.Instantiate(Ctx.State.Roster);
            // a duplicate of an existing ship's name gets a progressive suffix so both persist
            _shipName = Ctx.State.UniqueShipName(Ctx.Design.Name);
            _vessel.Body = earth;
            _vessel.Position = new Vec2d(0, earth.Radius);
            _vessel.Velocity = Vec2d.Zero;
            _vessel.Heading = Math.PI / 2;
            _vessel.Landed = true;
            _vessel.OnRails = false;
            _lastBody = earth;
            _mapZoom = earth.SoiRadius * 2.4 / Math.Min(Ctx.W, Ctx.H);
            SpawnOthers();
        }

        /// <summary>Load every other saved ship as a passive, always-on-rails co-orbiting target.</summary>
        private void SpawnOthers()
        {
            _others.Clear();
            foreach (var s in Ctx.State.Ships)
            {
                if (s.Name == _shipName || s.Destroyed) continue;
                var v = s.ToVessel(Ctx.Universe, Ctx.State.Roster);
                if (v.Body == null) continue;
                if (!v.Landed && !v.OnRails) v.GoOnRails(Ctx.Clock.UT);
                _others.Add(new TrackedShip { Name = s.Name, V = v });
            }
        }

        /// <summary>Snapshot the active ship into the savegame (dropping it if destroyed).</summary>
        private void PersistShip()
        {
            if (_vessel == null) return;
            Ctx.State.UT = Ctx.Clock.UT;
            Ctx.State.UpsertShip(ShipState.From(_vessel, _shipName, _nodes, _targetName));
        }

        /// <summary>Snapshot every tracked co-orbiting ship back into the savegame (so undocked modules
        /// and stations the player isn't currently flying are saved on exit too).</summary>
        private void PersistOthers()
        {
            foreach (var ts in _others)
            {
                var v = ts.V;
                if (v.Destroyed) { Ctx.State.RemoveShip(ts.Name); continue; }
                if (!v.Landed && !v.OnRails) v.GoOnRails(Ctx.Clock.UT);
                Ctx.State.UpsertShip(ShipState.From(v, ts.Name));
            }
        }

        /// <summary>Restore planned nodes from a saved ship (supports the legacy single-node field).</summary>
        private void LoadNodes(ShipState s)
        {
            _nodes.Clear();
            if (s.Nodes != null)
                foreach (var n in s.Nodes) { var m = n?.ToManeuver(); if (m != null) _nodes.Add(m); }
            else if (s.Node != null)
                _nodes.Add(s.Node.ToManeuver());
        }

        /// <summary>The nearest node that has not yet passed (the current burn target), or null.</summary>
        private Maneuver NextNode(double ut)
        {
            Maneuver best = null;
            foreach (var n in _nodes)
                if (n.UT >= ut && (best == null || n.UT < best.UT)) best = n;
            return best;
        }

        /// <summary>The final orbit a new node should be placed on: the last future node's result
        /// orbit, or the live coast orbit when there are no pending nodes.</summary>
        private OrbitalElements ChainEndOrbit(OrbitalElements live, double mu, double ut, out double fromUT)
        {
            OrbitalElements src = live; fromUT = ut;
            foreach (var n in Sorted(ut))
            {
                if (n.UT < ut) continue;
                var res = n.ResultOrbit(src, mu);
                if (!double.IsNaN(res.A)) { src = res; fromUT = n.UT; }
            }
            return src;
        }

        /// <summary>Nodes sorted by time (a fresh list; safe to iterate while mutating _nodes is avoided).</summary>
        private List<Maneuver> Sorted(double ut)
        {
            var list = new List<Maneuver>(_nodes);
            list.Sort((a, b) => a.UT.CompareTo(b.UT));
            return list;
        }

        /// <summary>Re-chain every pending node so each is planned against the previous burn's result
        /// orbit (within the current SOI). Past nodes keep their last (frozen) source.</summary>
        private void RefreshNodeChain(OrbitalElements live, double mu, double ut)
        {
            OrbitalElements src = live;
            foreach (var n in Sorted(ut))
            {
                if (n.UT < ut) continue;   // past nodes: leave frozen, they no longer alter projection
                n.Source = src; n.HasSource = true;
                var res = n.ResultOrbit(src, mu);
                if (!double.IsNaN(res.A)) src = res;
            }
        }

        public override void Update(double dt)
        {
            double realDt = Math.Min(dt, 0.1);
            _anim += realDt;
            var inp = Ctx.Input;
            var clock = Ctx.Clock;

            // ---------- input ----------
            // Escape opens a pause/exit modal; the sim is frozen while it's up (handled in Draw).
            // Scene switches are deferred out of Draw (where the buttons are evaluated) to here.
            if (_exitToTitle) { PersistShip(); PersistOthers(); Ctx.Scenes.SwitchTo(new TitleScene(Ctx)); return; }
            if (_exitToHub) { PersistShip(); PersistOthers(); Ctx.Scenes.SwitchTo(new HubScene(Ctx)); return; }
            if (_cancelMission)
            {
                Ctx.State.Ships.RemoveAll(s => s.Name == _shipName);   // discard the flight instance
                Ctx.Design = VesselDesign.FromVessel(_vessel, _shipName);
                Ctx.Scenes.SwitchTo(new EditorScene(Ctx));
                return;
            }
            if (inp.Pressed(Keys.Escape)) _showExitDialog = !_showExitDialog;
            if (_showExitDialog) return;
            if (inp.Pressed(Keys.M)) _map = !_map;
            if (inp.Pressed(Keys.Tab)) CycleTarget(inp.Down(Keys.LeftShift) || inp.Down(Keys.RightShift) ? -1 : 1);
            if (inp.Pressed(Keys.T)) _showTargetWindow = !_showTargetWindow;
            if (inp.Pressed(Keys.V)) TakeControlOfTarget();
            if (inp.Pressed(Keys.U)) Undock();
            if (inp.Pressed(Keys.C)) _showCrew = !_showCrew;
            if (_map && inp.Pressed(Keys.F)) _focus = (_focus + 1) % (Ctx.Universe.Bodies.Count + 1);

            bool maneuverWheel = false;
            UpdateManeuverInput(clock.UT, out maneuverWheel);

            if (inp.WheelDelta != 0 && !maneuverWheel)
            {
                double factor = Math.Pow(1.18, -inp.WheelDelta / 120.0);
                if (_map) _mapZoom = Math.Clamp(_mapZoom * factor, 50, 5e8);
                else _flightZoom = Math.Clamp(_flightZoom * factor, 0.02, 400);
            }
            if (inp.Pressed(Keys.OemComma)) { clock.WarpDown(); _warpTo = null; }
            if (inp.Pressed(Keys.OemPeriod)) { clock.WarpUp(); _warpTo = null; }
            if (NextNode(clock.UT) == null) _warpTo = null; // no pending node -> nothing to warp to

            bool alive = _vessel != null && !_vessel.Destroyed;
            if (alive)
            {
                if (inp.Down(Keys.LeftShift) || inp.Down(Keys.RightShift)) _vessel.Throttle = Math.Min(1, _vessel.Throttle + 0.7 * realDt);
                if (inp.Down(Keys.LeftControl) || inp.Down(Keys.RightControl)) _vessel.Throttle = Math.Max(0, _vessel.Throttle - 0.7 * realDt);
                if (inp.Pressed(Keys.Z)) _vessel.Throttle = 1;
                if (inp.Pressed(Keys.X)) _vessel.Throttle = 0;
                if (inp.Pressed(Keys.G)) ToggleSolar();
                if (inp.Pressed(Keys.H)) CycleSas();
                if (inp.Pressed(Keys.R)) _vessel.RcsEnabled = !_vessel.RcsEnabled;
                _vessel.RcsCommand = Vec2d.Zero;
                if (!_vessel.Landed)
                {
                    double turn = _vessel.TurnRate * realDt;
                    bool manual = inp.Down(Keys.A) || inp.Down(Keys.Left) || inp.Down(Keys.D) || inp.Down(Keys.Right);
                    if (inp.Down(Keys.A) || inp.Down(Keys.Left)) _vessel.Heading += turn;
                    if (inp.Down(Keys.D) || inp.Down(Keys.Right)) _vessel.Heading -= turn;
                    if (manual) _sas = SasMode.Off;          // manual input releases the hold
                    else if (_sas != SasMode.Off && SasHoldAngle(clock.UT) is double hold)
                    {
                        double diff = Kepler.WrapPi(hold - _vessel.Heading);
                        _vessel.Heading += Math.Clamp(diff, -turn, turn);
                    }
                    // RCS translation: I/K fore-aft (along Up axis), J/L left-right
                    double rx = 0, ry = 0;
                    if (inp.Down(Keys.I)) ry += 1;
                    if (inp.Down(Keys.K)) ry -= 1;
                    if (inp.Down(Keys.L)) rx += 1;
                    if (inp.Down(Keys.J)) rx -= 1;
                    _vessel.RcsCommand = new Vec2d(rx, ry);
                }
                if (inp.Pressed(Keys.Space))
                {
                    bool wasOnRails = _vessel.OnRails;
                    if (wasOnRails) _vessel.GoOffRails(clock.UT);
                    var debris = Staging.FireNext(_vessel);
                    if (debris != null)
                    {
                        _debris.Add(debris);
                        if (_debris.Count > MaxDebris) _debris.RemoveAt(0);
                    }
                    if (wasOnRails) { _vessel.GoOnRails(clock.UT); RefreshPrediction(clock.UT); }
                }
            }

            // ---------- simulation ----------
            if (alive)
            {
                var body = _vessel.Body;
                bool inAtmo = body.Atmo != null && _vessel.Altitude < body.Atmo.Top + 500;
                bool thrusting = _vessel.CurrentThrust > 0;
                bool needsPhysics = !_vessel.Landed && (thrusting || inAtmo || _vessel.RcsActive);

                clock.MaxWarpIndex = _vessel.Landed
                    ? (thrusting ? SimClock.PhysicsMaxIndex : SimClock.Levels.Length - 1)
                    : needsPhysics ? SimClock.PhysicsMaxIndex : SimClock.Levels.Length - 1;
                // a tracked ship in close proximity is simulated under physics: cap warp so it stays exact
                if (AnyOtherNear()) clock.MaxWarpIndex = Math.Min(clock.MaxWarpIndex, SimClock.PhysicsMaxIndex);

                // "warp to maneuver": force max warp while coasting toward the target, else cancel
                if (_warpTo.HasValue)
                {
                    if (_vessel.Landed || needsPhysics || _warpTo.Value <= clock.UT) _warpTo = null;
                    else clock.WarpIndex = SimClock.Levels.Length - 1;
                }

                double sdt = realDt * clock.Warp;

                if (_vessel.Landed)
                {
                    clock.UT += sdt;
                    if (thrusting)
                    {
                        _vessel.DrainFuel(sdt);
                        double g = body.Mu / (body.Radius * body.Radius);
                        if (_vessel.CurrentThrust > _vessel.TotalMass * g)
                        {
                            _vessel.Landed = false;
                            _vessel.OnRails = false;
                            _endMessage = null;
                        }
                    }
                }
                else if (needsPhysics)
                {
                    if (_vessel.OnRails) _vessel.GoOffRails(clock.UT);
                    int steps = Math.Clamp((int)Math.Ceiling(sdt / SubstepDt), 1, 64);
                    double h = sdt / steps;
                    for (int s = 0; s < steps; s++)
                    {
                        Integrator.Step(_vessel, h);
                        clock.UT += h;
                        CheckSoiOffRails(clock.UT);
                        if (CheckSurface()) break;
                    }
                    _predTimer -= realDt;
                    if (_predTimer <= 0 && !_vessel.Destroyed && !_vessel.Landed)
                    {
                        _pred = TrajectoryPredictor.Predict(_vessel.CurrentElements(clock.UT), _vessel.Body, clock.UT);
                        _predTimer = 0.2;
                    }
                }
                else // coasting in vacuum: on rails
                {
                    if (!_vessel.OnRails)
                    {
                        _vessel.GoOnRails(clock.UT);
                        RefreshPrediction(clock.UT);
                    }
                    double target = clock.UT + sdt;
                    // Stop a warp just before an upcoming maneuver node so it never sails past it.
                    // The burn is centred on the node, so the latest safe stop is when it should start.
                    double nodeStop = double.PositiveInfinity;
                    var stopNode = NextNode(clock.UT);
                    if (stopNode != null && _burnSpent == 0)
                    {
                        double bt = Staging.BurnTime(_vessel, stopNode.DeltaV);
                        nodeStop = stopNode.UT - (bt > 0 ? bt / 2 : 0);
                    }
                    if (_pred != null && _pred.Type != TransitionType.None && _pred.TransitionUT <= target
                        && _pred.TransitionUT <= nodeStop)
                    {
                        clock.UT = _pred.TransitionUT;
                        ApplyTransition();
                        clock.DropToRealtime();
                    }
                    else if (nodeStop > clock.UT && nodeStop <= target)
                    {
                        clock.UT = nodeStop;
                        _warpTo = null;
                        clock.DropToRealtime();
                    }
                    else if (_warpTo.HasValue && _warpTo.Value <= target)
                    {
                        clock.UT = _warpTo.Value;
                        _warpTo = null;
                        clock.DropToRealtime();
                    }
                    else
                    {
                        clock.UT = target;
                    }
                    if (_vessel.OnRails) _vessel.UpdateFromRails(clock.UT);
                }

                _vessel.UpdateResources(sdt, clock.UT, Ctx.Universe);
                _lastBody = _vessel.Body;
                _lastCamRel = _vessel.Position;
            }
            else
            {
                clock.MaxWarpIndex = SimClock.Levels.Length - 1;
                clock.UT += realDt * clock.Warp;
            }

            // maneuver burn tracking: count delta-v spent against the current node. Once a node's
            // burn time passes it is consumed (KSP-style) and removed -- the live predicted
            // trajectory then stands in as the achieved orbit, so nothing lingers/drifts on screen.
            _nodes.RemoveAll(n => n.HasSource && n.UT < clock.UT);

            var burnTarget = NextNode(clock.UT);
            if (burnTarget == null) { _burnSpent = 0; _burnTargetUT = double.NaN; }
            else
            {
                if (burnTarget.UT != _burnTargetUT) { _burnSpent = 0; _burnTargetUT = burnTarget.UT; }
                if (alive)
                {
                    bool burning = _vessel.Throttle > 0 && _vessel.MaxAvailableThrust > 0 && clock.UT >= burnTarget.UT - 1.0;
                    if (burning) _burnSpent += _vessel.CurrentThrust / _vessel.TotalMass * realDt * clock.Warp;
                }
            }

            UpdateDebris(realDt);
            UpdateOthers(clock.UT, realDt);
            TryDock(clock.UT);
            PruneTarget();
            UpdateClosestApproach(clock.UT);

            // progression: award science for newly reached flight milestones + transmitted experiments
            var hit = Solar.Progression.Milestones.Evaluate(_vessel, clock.UT, Ctx.State);
            if (hit != null) { _toast = $"+{hit.Reward:0} science  -  {hit.Title}"; _toastT = 5; }
            else CollectScience(clock.UT, realDt * clock.Warp);
            // crew that ran out of life support this tick (overrides other toasts: it's important)
            if (_vessel != null && _vessel.RecentDeaths.Count > 0)
            {
                _toast = $"Crew lost: {string.Join(", ", _vessel.RecentDeaths)}";
                _toastT = 6;
                _vessel.RecentDeaths.Clear();
            }
            if (_toastT > 0) _toastT -= realDt;

            UpdateCamera();
        }

        /// <summary>Within this range of the active vessel (same SOI), a tracked ship is simulated under
        /// physics so close-range relative motion is exact (rendezvous / docking); beyond it, on rails.</summary>
        private const double ProximityRange = 30_000;

        /// <summary>True when a tracked ship is close enough to the active vessel to need physics.</summary>
        private bool OtherNear(Vessel o) =>
            _vessel != null && !_vessel.Destroyed && o != null && !o.Landed && !o.Destroyed
            && o.Body == _vessel.Body && (o.Position - _vessel.Position).Length < ProximityRange;

        private bool AnyOtherNear()
        {
            foreach (var ts in _others) if (OtherNear(ts.V)) return true;
            return false;
        }

        private void UpdateOthers(double ut, double realDt)
        {
            var clock = Ctx.Clock;
            bool physWarp = clock.Warp <= SimClock.Levels[SimClock.PhysicsMaxIndex];
            double sdt = realDt * clock.Warp;
            foreach (var ts in _others)
            {
                var v = ts.V;
                if (v.Landed || v.Destroyed) continue;

                if (physWarp && OtherNear(v))
                {
                    if (v.OnRails) v.GoOffRails(ut);
                    int steps = Math.Clamp((int)Math.Ceiling(sdt / SubstepDt), 1, 32);
                    double h = sdt / steps;
                    for (int s = 0; s < steps; s++) Integrator.Step(v, h);
                }
                else
                {
                    if (!v.OnRails) v.GoOnRails(ut);
                    v.UpdateFromRails(ut);
                }
            }
        }

        /// <summary>Capture distance / closing-speed limit for an orbital dock, and the (looser) range
        /// for connecting two craft that are both parked on a surface into a compound base.</summary>
        private const double CaptureDist = 15.0;
        private const double SoftDockSpeed = 2.5;
        private const double ConnectDist = 50.0;

        /// <summary>Join the active vessel to a tracked ship into one compound vessel: an orbital dock
        /// (both coasting, within capture range, low relative speed) or a surface connection (both landed
        /// nearby, no alignment needed). Both craft must have a free docking port.</summary>
        private void TryDock(double ut)
        {
            if (_vessel == null || _vessel.Destroyed || !_vessel.HasFreeDockingPort) return;
            foreach (var ts in _others)
            {
                var o = ts.V;
                if (o.Destroyed || o.Body != _vessel.Body || !o.HasFreeDockingPort) continue;

                bool surface = _vessel.Landed && o.Landed;
                if (surface)
                {
                    if ((o.Position - _vessel.Position).Length >= ConnectDist) continue;
                }
                else
                {
                    if (_vessel.Landed || o.Landed) continue;   // can't dock a flying craft to a landed one
                    if ((o.Position - _vessel.Position).Length >= CaptureDist) continue;
                    if ((o.Velocity - _vessel.Velocity).Length >= SoftDockSpeed) continue;
                }

                var myPort = _vessel.FirstFreeDockingPort();
                var theirPort = o.FirstFreeDockingPort();
                if (_vessel.OnRails) _vessel.GoOffRails(ut);
                if (!surface)
                {
                    // conserve momentum into the merged rigid body (landed craft are already at rest)
                    double m1 = _vessel.TotalMass, m2 = o.TotalMass;
                    _vessel.Velocity = (_vessel.Velocity * m1 + o.Velocity * m2) / (m1 + m2);
                }
                _vessel.DockWith(o, myPort, theirPort);
                _others.Remove(ts);
                Ctx.State.RemoveShip(ts.Name);
                ClearTarget();
                if (!_vessel.Landed) RefreshPrediction(ut);
                if (surface)
                {
                    var ms = Solar.Progression.Milestones.Evaluate(_vessel, ut, Ctx.State);
                    _toast = ms != null ? $"+{ms.Reward:0} science  -  {ms.Title}" : $"Connected {ts.Name} to base";
                }
                else
                {
                    var ms = Solar.Progression.Milestones.Award(Ctx.State, "docking");
                    _toast = ms != null ? $"+{ms.Reward:0} science  -  {ms.Title}" : $"Docked with {ts.Name}";
                }
                _toastT = 5;
                return;
            }
        }

        /// <summary>Detach the most-recently docked module; it becomes a tracked co-orbiting ship.</summary>
        private void Undock()
        {
            if (_vessel == null || !_vessel.CanUndock) return;
            double ut = Ctx.Clock.UT;
            if (_vessel.OnRails) _vessel.GoOffRails(ut);
            var detached = _vessel.Undock();
            if (detached == null) return;
            detached.GoOnRails(ut);
            string name = UniqueShipName(_shipName + " module");
            _others.Add(new TrackedShip { Name = name, V = detached });
            Ctx.State.UpsertShip(ShipState.From(detached, name));
            if (!_vessel.Landed && !_vessel.Destroyed) RefreshPrediction(ut);
            _toast = $"Undocked {name}"; _toastT = 4;
        }

        /// <summary>A ship name not already taken by the active vessel, a tracked ship, or the savegame.</summary>
        private string UniqueShipName(string baseName)
        {
            bool Taken(string n) => n == _shipName || _others.Exists(o => o.Name == n) || Ctx.State.Ships.Exists(s => s.Name == n);
            if (!Taken(baseName)) return baseName;
            for (int i = 2; ; i++) { var n = $"{baseName} {i}"; if (!Taken(n)) return n; }
        }

        /// <summary>Drop a target that no longer exists (e.g. debris that decayed).</summary>
        private void PruneTarget()
        {
            if (_targetVessel != null && !_debris.Contains(_targetVessel) && _others.TrueForAll(o => o.V != _targetVessel))
                ClearTarget();
        }

        private void ClearTarget() { _targetBody = null; _targetVessel = null; _targetName = null; _caValid = false; _prox.Clear(); _proxTimer = 0; }

        /// <summary>Deploy all solar panels if any are stowed, otherwise retract them all.</summary>
        private void ToggleSolar()
        {
            if (_vessel == null) return;
            bool anyStowed = false;
            foreach (var p in _vessel.Parts)
                foreach (var m in p.Modules)
                    if (m.Def.Kind == Parts.ModuleKind.SolarPanel && !m.Active) anyStowed = true;
            foreach (var p in _vessel.Parts)
                foreach (var m in p.Modules)
                    if (m.Def.Kind == Parts.ModuleKind.SolarPanel) m.Active = anyStowed;
        }

        private void UpdateClosestApproach(double ut)
        {
            _caValid = false;
            if (!HasTarget || _vessel == null || _vessel.Destroyed || _vessel.Landed) { _prox.Clear(); return; }
            if (!YourPlannedOrbit(ut, out var you, out var youPrimary)) { _prox.Clear(); return; }
            if (!TargetOrbitBody(ut, out var tEl, out var tPrimary)) { _prox.Clear(); return; }

            double window = you.Hyperbolic || double.IsNaN(you.Period) ? 6 * 3600.0 : you.Period;
            Func<double, Vec2d> tpos = t => tPrimary.AbsolutePositionAt(t) + Kepler.StateAtTime(tEl, t).pos;
            _caValid = Rendezvous.ClosestApproach(you, youPrimary, tpos, ut, window, out _caUT, out _caSep, out _caRelSpeed);

            // geometric orbit-curve proximities (intersections + closest points) -- recompute every ~8 frames
            if (--_proxTimer <= 0)
            {
                _proxTimer = 8;
                _prox = Rendezvous.OrbitProximity(you, youPrimary.AbsolutePositionAt(ut), tEl, tPrimary.AbsolutePositionAt(ut));
            }
        }

        private void RefreshPrediction(double ut)
        {
            _pred = _vessel.OnRails
                ? TrajectoryPredictor.Predict(_vessel.Orbit, _vessel.Body, ut)
                : TrajectoryPredictor.Predict(_vessel.CurrentElements(ut), _vessel.Body, ut);
        }

        private void ApplyTransition()
        {
            double t = _pred.TransitionUT;
            var v = _vessel;
            v.UpdateFromRails(t);
            switch (_pred.Type)
            {
                case TransitionType.Escape:
                    v.Position += v.Body.LocalPositionAt(t);
                    v.Velocity += v.Body.LocalVelocityAt(t);
                    v.Body = v.Body.Parent;
                    v.GoOnRails(t);
                    break;
                case TransitionType.Encounter:
                    v.Position -= _pred.NextBody.LocalPositionAt(t);
                    v.Velocity -= _pred.NextBody.LocalVelocityAt(t);
                    v.Body = _pred.NextBody;
                    v.GoOnRails(t);
                    break;
                case TransitionType.AtmoEntry:
                    v.OnRails = false; // physics takes over next frame
                    break;
            }
            // pending nodes were planned around the old primary and can't be re-based; reached nodes
            // carry an absolute snapshot, so keep them visible across the handoff.
            if (_pred.Type != TransitionType.AtmoEntry) { _nodes.RemoveAll(n => !n.Reached); _burnSpent = 0; }
            RefreshPrediction(t);
        }

        private void CheckSoiOffRails(double ut)
        {
            var v = _vessel;
            if (v.Body.Parent != null && v.Position.Length > v.Body.SoiRadius)
            {
                v.Position += v.Body.LocalPositionAt(ut);
                v.Velocity += v.Body.LocalVelocityAt(ut);
                v.Body = v.Body.Parent;
                Ctx.Clock.DropToRealtime();
                _nodes.RemoveAll(n => !n.Reached); _burnSpent = 0;
                return;
            }
            foreach (var c in v.Body.Children)
            {
                if (double.IsInfinity(c.SoiRadius)) continue;
                if ((v.Position - c.LocalPositionAt(ut)).Length < c.SoiRadius)
                {
                    v.Position -= c.LocalPositionAt(ut);
                    v.Velocity -= c.LocalVelocityAt(ut);
                    v.Body = c;
                    Ctx.Clock.DropToRealtime();
                    _nodes.RemoveAll(n => !n.Reached); _burnSpent = 0;
                    return;
                }
            }
        }

        private bool CheckSurface()
        {
            var v = _vessel;
            if (v.Position.Length > v.Body.Radius) return false;
            double speed = v.Velocity.Length;
            v.Position = v.Position.Normalized() * v.Body.Radius;
            if (speed <= v.SafeLandingSpeed)
            {
                v.Velocity = Vec2d.Zero;
                v.Landed = true;
                v.Throttle = 0;
                v.Heading = v.Position.Angle();
                _endMessage = $"Landed safely on {v.Body.Name}!   [Esc] to base";
            }
            else
            {
                v.Destroyed = true;
                _endMessage = $"CRASHED into {v.Body.Name} at {speed:0} m/s   [Esc] to base";
            }
            return true;
        }

        private void UpdateDebris(double realDt)
        {
            var clock = Ctx.Clock;
            for (int i = _debris.Count - 1; i >= 0; i--)
            {
                var d = _debris[i];
                if (d.OnRails)
                {
                    d.UpdateFromRails(clock.UT);
                    if (d.Position.Length < d.Body.Radius + 100) _debris.RemoveAt(i);
                    continue;
                }

                bool inAtmo = d.Body.Atmo != null && d.Altitude < d.Body.Atmo.Top;
                if (clock.Warp > SimClock.Levels[SimClock.PhysicsMaxIndex] ||
                    (_vessel != null && (d.AbsolutePosition(clock.UT) - _vessel.AbsolutePosition(clock.UT)).Length > 30_000))
                {
                    if (inAtmo) { _debris.RemoveAt(i); continue; }
                    d.GoOnRails(clock.UT);
                    continue;
                }

                double sdt = realDt * clock.Warp;
                int steps = Math.Clamp((int)Math.Ceiling(sdt / SubstepDt), 1, 32);
                double h = sdt / steps;
                for (int s = 0; s < steps; s++) Integrator.Step(d, h);
                if (d.Position.Length <= d.Body.Radius) _debris.RemoveAt(i);
            }
        }

        private void UpdateCamera()
        {
            _cam.SetViewport(Ctx.W, Ctx.H);
            double ut = Ctx.Clock.UT;
            bool alive = _vessel != null && !_vessel.Destroyed;
            Vec2d vesselPos = alive ? _vessel.AbsolutePosition(ut) : _lastBody.AbsolutePositionAt(ut) + _lastCamRel;

            if (_map)
            {
                _cam.Center = _focus == 0 ? vesselPos : Ctx.Universe.Bodies[_focus - 1].AbsolutePositionAt(ut);
                _cam.MetersPerPixel = _mapZoom;
            }
            else
            {
                _cam.Center = vesselPos;
                _cam.MetersPerPixel = _flightZoom;
            }
        }

        // =====================================================================  targeting

        private bool HasTarget => _targetBody != null || _targetVessel != null;

        /// <summary>All targetable objects in cycle order: bodies, then debris, then other ships.</summary>
        private List<(string name, CelestialBody body, Vessel vessel)> TargetItems()
        {
            var list = new List<(string, CelestialBody, Vessel)>();
            foreach (var b in Ctx.Universe.Bodies) list.Add((b.Name, b, null));
            for (int i = 0; i < _debris.Count; i++) list.Add(($"Debris {i + 1}", null, _debris[i]));
            foreach (var ts in _others) list.Add((ts.Name, null, ts.V));
            return list;
        }

        private void SetTarget(CelestialBody b, Vessel v, string name)
        {
            _targetBody = b; _targetVessel = v; _targetName = name; _caValid = false; _prox.Clear(); _proxTimer = 0;
        }

        /// <summary>Re-select a saved target by name on resume: a body first, then a co-orbiting ship.</summary>
        private void RestoreTarget(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var body = Ctx.Universe[name];
            if (body != null) { SetTarget(body, null, name); return; }
            foreach (var ts in _others)
                if (ts.Name == name) { SetTarget(null, ts.V, name); return; }
        }

        /// <summary>Take control of the currently-targeted co-orbiting ship: persist the active vessel,
        /// demote it into the tracked list in the target's slot, and promote the target to the active
        /// vessel. Lets the player fly any of their craft (needed for station / colony assembly).</summary>
        private void TakeControlOfTarget()
        {
            if (_targetVessel == null) return;
            TrackedShip sel = null;
            foreach (var ts in _others) if (ts.V == _targetVessel) { sel = ts; break; }
            if (sel == null) return;   // only co-orbiting ships are controllable (not bodies/debris)

            double ut = Ctx.Clock.UT;
            PersistShip();                          // save the ship we are leaving
            var prevName = _shipName;
            var prevVessel = _vessel;

            // promote the target to the controlled vessel
            _vessel = sel.V;
            _shipName = sel.Name;
            _nodes.Clear(); _burnSpent = 0; _burnTargetUT = double.NaN;
            _sas = SasMode.Off;
            _vessel.RcsCommand = Vec2d.Zero;
            if (_vessel.OnRails) _vessel.UpdateFromRails(ut);
            _lastBody = _vessel.Body;
            _lastCamRel = _vessel.Position;
            if (!_vessel.Landed && !_vessel.Destroyed) RefreshPrediction(ut);

            // the ship we left becomes the tracked co-orbiter in the freed slot
            sel.Name = prevName;
            sel.V = prevVessel;
            if (prevVessel.Body != null && !prevVessel.Landed && !prevVessel.OnRails) prevVessel.GoOnRails(ut);

            ClearTarget();
            _toast = $"Now controlling {_shipName}"; _toastT = 4;
        }

        /// <summary>Advance the target through none -> each item -> none.</summary>
        private void CycleTarget(int dir)
        {
            var items = TargetItems();
            int cur = -1;
            for (int i = 0; i < items.Count; i++)
                if (items[i].body == _targetBody && items[i].vessel == _targetVessel && HasTarget) { cur = i; break; }
            int state = cur < 0 ? 0 : cur + 1;                     // 0 = none, 1..n = items
            state = (state + dir + items.Count + 1) % (items.Count + 1);
            if (state == 0) ClearTarget();
            else { var it = items[state - 1]; SetTarget(it.body, it.vessel, it.name); }
        }

        private Vec2d TargetPos(double ut) =>
            _targetBody != null ? _targetBody.AbsolutePositionAt(ut) : _targetVessel.AbsolutePosition(ut);

        private Vec2d TargetVel(double ut) =>
            _targetBody != null ? _targetBody.AbsoluteVelocityAt(ut) : _targetVessel.AbsoluteVelocity(ut);

        /// <summary>Advance the attitude-hold mode, skipping target-relative modes when no target is set.</summary>
        private void CycleSas()
        {
            do { _sas = (SasMode)(((int)_sas + 1) % 6); }
            while (_sas != SasMode.Off && SasHoldAngle(Ctx.Clock.UT) == null);
        }

        /// <summary>World angle the current SAS mode points at, or null if unavailable this frame.</summary>
        private double? SasHoldAngle(double ut)
        {
            if (_vessel == null || _vessel.Destroyed) return null;
            switch (_sas)
            {
                case SasMode.Prograde: return _vessel.Velocity.Length > 0.1 ? _vessel.Velocity.Angle() : (double?)null;
                case SasMode.Retrograde: return _vessel.Velocity.Length > 0.1 ? (-_vessel.Velocity).Angle() : (double?)null;
                case SasMode.Target: return HasTarget ? (TargetPos(ut) - _vessel.AbsolutePosition(ut)).Angle() : (double?)null;
                case SasMode.AntiTarget: return HasTarget ? (_vessel.AbsolutePosition(ut) - TargetPos(ut)).Angle() : (double?)null;
                case SasMode.RelRetro:
                    if (!HasTarget) return null;
                    Vec2d rel = _vessel.AbsoluteVelocity(ut) - TargetVel(ut);
                    return rel.Length > 0.1 ? (-rel).Angle() : (double?)null;
                default: return null;
            }
        }

        private string SasLabel() => _sas switch
        {
            SasMode.Prograde => "PRO", SasMode.Retrograde => "RETRO",
            SasMode.Target => "TGT", SasMode.AntiTarget => "ANTI-TGT",
            SasMode.RelRetro => "KILL REL", _ => "",
        };

        /// <summary>Bottom-left readout telling the player what the fitted instruments can collect here and
        /// whether an antenna will transmit it — so the science economy is legible in flight.</summary>
        private void DrawScienceStatus(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            var v = _vessel;
            if (v == null || v.Destroyed || v.Body == null) return;
            bool hasSci = false, antenna = false, anyNew = false;
            string sit = SciSituation(v);
            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                {
                    if (m.Def.Kind == Parts.ModuleKind.Science)
                    {
                        hasSci = true;
                        if (m.Active && !Ctx.State.ScienceCollected.Contains($"{v.Body.Name}|{sit}|{m.Def.Name}")) anyNew = true;
                    }
                    if (m.Def.Kind == Parts.ModuleKind.Antenna && m.Active) antenna = true;
                }

            var f = Ctx.Font;
            double signal = v.SignalStrength(ut, Ctx.Universe, OtherVessels());
            var r = new Rectangle(10, Ctx.H - 168, 256, 68);
            UiDraw.Panel(pb, r);
            // running total is always shown (milestones pay out even with no instruments)
            sb.DrawString(f, $"SCIENCE: {Ctx.State.Science:0}", new Vector2(r.X + 10, r.Y + 8), new Color(150, 230, 150));
            // signal strength governs transmission speed
            string sigTxt = !antenna ? "Signal: no antenna"
                          : signal <= 0 ? "Signal: none (out of range)"
                          : $"Signal: {signal * 100:0}%";
            Color sigC = !antenna ? new Color(255, 190, 90)
                       : signal <= 0 ? new Color(255, 120, 90)
                       : signal < 0.5 ? new Color(255, 210, 120) : new Color(150, 220, 150);
            sb.DrawString(f, sigTxt, new Vector2(r.X + 10, r.Y + 26), sigC);
            string l2; Color c2;
            if (v.PendingScience > 0)
            {
                l2 = signal > 0 ? $"transmitting... {v.PendingScience:0} data left"
                                : $"{v.PendingScience:0} data queued (no signal)";
                c2 = signal > 0 ? new Color(150, 230, 150) : new Color(255, 190, 90);
            }
            else if (!hasSci) { l2 = "fit an instrument + antenna, or hit milestones"; c2 = UiDraw.TextDim; }
            else if (anyNew) { l2 = $"{v.Body.Name} {sit}: new reading"; c2 = new Color(150, 230, 150); }
            else { l2 = $"{v.Body.Name} {sit}: nothing new here"; c2 = UiDraw.TextDim; }
            sb.DrawString(f, l2, new Vector2(r.X + 10, r.Y + 46), c2);
        }

        /// <summary>Seconds until a resource pool empties at the given draw, or +inf if it isn't draining.</summary>
        private static double TimeLeft(double amount, double ratePerSec)
            => ratePerSec > 0 ? amount / ratePerSec : double.PositiveInfinity;

        private static string SciSituation(Vessel v) =>
            v.Landed ? "landed" : v.Altitude < 250_000 ? "low orbit" : "high orbit";

        private static double SciBodyFactor(string body) => body switch
        {
            "Earth" => 1.0, "Moon" => 2.0, "Sun" => 2.5, _ => 3.0,
        };

        /// <summary>Science point/s transmitted at full signal; weak signal scales this down (so distant
        /// vessels transmit slowly) and zero signal stops transmission entirely.</summary>
        private const double TransmitBaseRate = 8.0;

        private IEnumerable<Vessel> OtherVessels()
        {
            foreach (var o in _others) if (o?.V != null) yield return o.V;
        }

        /// <summary>Record new experiments into the vessel's transmit buffer (needs an active instrument +
        /// antenna), then drain that buffer into the science total at a rate scaled by signal strength —
        /// so transmission speed degrades with distance from the nearest antenna / home station.</summary>
        private void CollectScience(double ut, double dt)
        {
            var v = _vessel;
            if (v == null || v.Destroyed || v.Body == null) return;
            bool antenna = false, anyScience = false;
            foreach (var p in v.AllParts())
                foreach (var m in p.Modules)
                {
                    if (m.Def.Kind == Parts.ModuleKind.Antenna && m.Active) antenna = true;
                    if (m.Def.Kind == Parts.ModuleKind.Science && m.Active) anyScience = true;
                }

            var gs = Ctx.State;
            // queue the next unrecorded experiment for transmission
            if (antenna && anyScience)
            {
                string sit = SciSituation(v);
                double baseVal = v.Landed ? 15 : v.Altitude < 250_000 ? 8 : 10;
                bool queued = false;
                foreach (var p in v.AllParts())
                {
                    if (queued) break;
                    foreach (var m in p.Modules)
                    {
                        if (m.Def.Kind != Parts.ModuleKind.Science || !m.Active) continue;
                        string key = $"{v.Body.Name}|{sit}|{m.Def.Name}";
                        if (gs.ScienceCollected.Contains(key)) continue;
                        gs.ScienceCollected.Add(key);
                        v.PendingScience += Math.Round(baseVal * SciBodyFactor(v.Body.Name));
                        _toast = $"recorded {m.Def.Name}: {v.Body.Name} {sit}";
                        _toastT = 5;
                        queued = true;
                        break;   // one new experiment per frame
                    }
                }
            }

            // transmit the buffer; rate scales with signal strength, so distance slows it down
            if (v.PendingScience > 0 && dt > 0)
            {
                double signal = v.SignalStrength(ut, Ctx.Universe, OtherVessels());
                if (signal > 0)
                {
                    double xfer = Math.Min(v.PendingScience, TransmitBaseRate * signal * dt);
                    v.PendingScience -= xfer;
                    gs.Science += xfer;
                }
            }
        }

        /// <summary>Build the target navball cues (markers + readout) for the HUD.</summary>
        private NavMarkers BuildNavMarkers(double ut)
        {
            var nav = new NavMarkers { SasMode = (int)_sas, SasLabel = SasLabel(), Target = double.NaN, RelPro = double.NaN };
            if (HasTarget && _vessel != null && !_vessel.Destroyed && !_vessel.Landed)
            {
                nav.Active = true;
                Vec2d sep = TargetPos(ut) - _vessel.AbsolutePosition(ut);
                nav.Target = sep.Angle();
                Vec2d rel = _vessel.AbsoluteVelocity(ut) - TargetVel(ut);
                if (rel.Length > 0.1) nav.RelPro = rel.Angle();
                double closing = sep.Length > 1 ? -rel.Dot(sep.Normalized()) : 0;  // + = closing distance
                nav.Readout = $"TGT {UiDraw.Dist(sep.Length)}  {UiDraw.Speed(closing)}";
            }
            return nav;
        }

        /// <summary>Target's conic + the body it orbits (for prediction). False if the target has no orbit.</summary>
        private bool TargetOrbitBody(double ut, out OrbitalElements el, out CelestialBody primary)
        {
            el = default; primary = null;
            if (_targetBody != null)
            {
                if (_targetBody.Parent == null) return false; // the Sun has no orbit
                el = _targetBody.Orbit; primary = _targetBody.Parent; return true;
            }
            if (_targetVessel != null)
            {
                el = _targetVessel.CurrentElements(ut); primary = _targetVessel.Body;
                return !double.IsNaN(el.A);
            }
            return false;
        }

        /// <summary>Your final planned conic (after all pending nodes) and the body you orbit.</summary>
        private bool YourPlannedOrbit(double ut, out OrbitalElements el, out CelestialBody primary)
        {
            el = default;
            if (!LiveOrbit(ut, out var live, out primary, out _)) return false;
            var src = live;
            foreach (var n in Sorted(ut))
                if (n.UT >= ut && n.HasSource)
                {
                    var res = n.ResultOrbit(n.Source, primary.Mu);
                    if (!double.IsNaN(res.A)) src = res;
                }
            el = src;
            return true;
        }

        // =====================================================================  maneuver planning

        /// <summary>The vessel's live coast orbit + primary (the trajectory currently drawn).</summary>
        private bool LiveOrbit(double ut, out OrbitalElements orbit, out CelestialBody body, out Vec2d primaryAbs)
        {
            orbit = default; primaryAbs = default; body = null;
            if (_vessel == null || _vessel.Destroyed || _vessel.Landed) return false;
            orbit = _vessel.CurrentElements(ut);
            if (double.IsNaN(orbit.A) || double.IsNaN(orbit.E)) return false;
            body = _vessel.Body;
            primaryAbs = body.AbsolutePositionAt(ut);
            return true;
        }

        /// <summary>Orbit a given node is *projected* against: its (chained) source snapshot when
        /// available, else the live orbit.</summary>
        private bool ManeuverContext(Maneuver node, double ut, out OrbitalElements orbit, out CelestialBody body, out Vec2d primaryAbs)
        {
            if (!LiveOrbit(ut, out orbit, out body, out primaryAbs)) return false;
            if (node != null && node.HasSource) orbit = node.Source;
            return true;
        }

        /// <summary>Screen position of a node and the (unit) screen-space prograde/radial-out axes.</summary>
        private bool ManeuverGeometry(Maneuver node, double ut, out Vector2 nodeScreen, out Vector2 proDir, out Vector2 radDir)
        {
            nodeScreen = default; proDir = default; radDir = default;
            if (node == null || !ManeuverContext(node, ut, out var orbit, out _, out var primaryAbs)) return false;
            var (rN, vN) = Kepler.StateAtTime(orbit, node.UT);
            nodeScreen = _cam.WorldToScreen(primaryAbs + rN);
            Vec2d pro = vN.Normalized();
            Vec2d radOut = pro.Perp();
            if (radOut.Dot(rN) < 0) radOut = -radOut;
            proDir = new Vector2((float)pro.X, -(float)pro.Y);      // world->screen flips Y
            radDir = new Vector2((float)radOut.X, -(float)radOut.Y);
            if (proDir.Length() > 1e-4f) proDir.Normalize();
            if (radDir.Length() > 1e-4f) radDir.Normalize();
            return true;
        }

        /// <summary>Nearest true anomaly on the orbit to the mouse, if within the pick threshold.</summary>
        private bool PickOrbitNu(in OrbitalElements el, Vec2d primaryAbs, Vector2 mouse, out double nuBest)
        {
            nuBest = 0; double best = double.MaxValue;
            const int n = 360;
            bool closed = !el.Hyperbolic;
            double nuMax = Math.PI;
            if (el.Hyperbolic)
            {
                double rLim = double.IsInfinity(_vessel.Body.SoiRadius) ? el.Periapsis * 50 : _vessel.Body.SoiRadius;
                double cnu = (el.SemiLatus / rLim - 1) / el.E;
                nuMax = cnu <= -1 ? Math.PI - 1e-3 : cnu >= 1 ? 0.1 : Math.Acos(cnu);
            }
            for (int s = 0; s <= n; s++)
            {
                double nu = closed ? -Math.PI + 2 * Math.PI * s / n : -nuMax + 2 * nuMax * s / n;
                double r = el.SemiLatus / (1 + el.E * Math.Cos(nu));
                if (r <= 0) continue;
                Vector2 sp = _cam.WorldToScreen(primaryAbs + Vec2d.FromAngle(el.ArgPe + el.Dir * nu) * r);
                float d = Vector2.Distance(sp, mouse);
                if (d < best) { best = d; nuBest = nu; }
            }
            return best <= 10f;
        }

        // X delete button placement relative to a node marker
        private const float XOffX = 14f, XOffY = -14f, XHit = 9f;
        private static Vector2 XButtonPos(Vector2 nodeScreen) => nodeScreen + new Vector2(XOffX, XOffY);

        private void UpdateManeuverInput(double ut, out bool wheelConsumed)
        {
            wheelConsumed = false;
            if (!_map || _cam.ScreenW == 0) { _dragHandle = -1; _dragNode = -1; _hoverHandle = -1; _hoverNode = -1; _hoverX = -1; return; }
            var inp = Ctx.Input;
            if (!LiveOrbit(ut, out var live, out var body, out var primaryAbs))
            {
                if (_vessel == null || _vessel.Destroyed || _vessel.Landed) { _nodes.Clear(); _burnSpent = 0; }
                _dragHandle = -1; _dragNode = -1; _hoverHandle = -1; _hoverNode = -1; _hoverX = -1;
                return;
            }

            // re-chain pending nodes against the live orbit while coasting; freeze during a burn
            bool thrusting = _vessel.CurrentThrust > 0;
            if (!thrusting) RefreshNodeChain(live, body.Mu, ut);

            Vector2 mouse = inp.MousePos;

            // [Del] removes the active/next node
            if (_nodes.Count > 0 && inp.Pressed(Keys.Delete))
            {
                var del = _nodes[Math.Max(0, _hoverNode)];
                if (_hoverNode < 0) del = NextNode(ut) ?? _nodes[0];
                _nodes.Remove(del); _burnSpent = 0; _dragHandle = -1; _dragNode = -1;
            }

            // hover scan across all nodes (handles, body, X)
            _hoverHandle = -1; _hoverNode = -1; _hoverX = -1;
            for (int n = 0; n < _nodes.Count && _hoverHandle < 0 && _hoverX < 0; n++)
            {
                if (!ManeuverGeometry(_nodes[n], ut, out var ns, out var pd, out var rd)) continue;
                if (Vector2.Distance(mouse, XButtonPos(ns)) <= XHit) { _hoverX = n; break; }
                var h = new[] { ns + pd * HandleDist, ns - pd * HandleDist, ns + rd * HandleDist, ns - rd * HandleDist };
                for (int k = 0; k < 4; k++)
                    if (Vector2.Distance(mouse, h[k]) <= HandleHit) { _hoverHandle = k; _hoverNode = n; break; }
                if (_hoverHandle < 0 && Vector2.Distance(mouse, ns) <= NodeHit) _hoverNode = n;
            }

            // scroll-wheel fine-tune when hovering a handle (takes priority over zoom)
            if (_hoverHandle >= 0 && _hoverNode >= 0 && inp.WheelDelta != 0)
            {
                double step = (inp.Down(Keys.LeftShift) || inp.Down(Keys.RightShift)) ? 0.5 : 5.0;
                AdjustComponent(_nodes[_hoverNode], _hoverHandle, Math.Sign(inp.WheelDelta) * step);
                wheelConsumed = true;
            }

            // keyboard fine-tune targets the next node
            var kbNode = NextNode(ut);
            if (kbNode != null)
            {
                double ks = (inp.Down(Keys.LeftShift) || inp.Down(Keys.RightShift)) ? 0.5 : 5.0;
                if (inp.Pressed(Keys.OemCloseBrackets)) kbNode.Prograde += ks;
                if (inp.Pressed(Keys.OemOpenBrackets)) kbNode.Prograde -= ks;
                if (inp.Pressed(Keys.OemQuotes)) kbNode.Radial += ks;
                if (inp.Pressed(Keys.OemSemicolon)) kbNode.Radial -= ks;
            }

            // begin interaction on click
            if (inp.LeftClick)
            {
                if (_hoverX >= 0)
                {
                    _nodes.RemoveAt(_hoverX); _burnSpent = 0; _dragHandle = -1; _dragNode = -1;
                }
                else if (_hoverHandle >= 0 && _hoverNode >= 0)
                {
                    _dragHandle = _hoverHandle; _dragNode = _hoverNode;
                    ManeuverGeometry(_nodes[_dragNode], ut, out var ns, out var pd, out var rd);
                    Vector2 axis = _dragHandle < 2 ? pd : rd;
                    _dragStartProj = Vector2.Dot(mouse - ns, axis);
                    _dragStartValue = _dragHandle < 2 ? _nodes[_dragNode].Prograde : _nodes[_dragNode].Radial;
                }
                else if (_hoverNode >= 0)
                {
                    _dragHandle = 4; _dragNode = _hoverNode; // move node along its orbit
                }
                else if (!_showTargetWindow)
                {
                    // place a new node on the end of the chain (live orbit if none pending)
                    var endOrbit = ChainEndOrbit(live, body.Mu, ut, out double fromUT);
                    if (PickOrbitNu(endOrbit, primaryAbs, mouse, out double nu))
                    {
                        _nodes.Add(new Maneuver { UT = Kepler.TimeAtTrueAnomaly(endOrbit, nu, fromUT), Source = endOrbit, HasSource = true });
                        _burnSpent = 0; _dragHandle = -1; _dragNode = -1;
                    }
                }
            }

            // continue drag of the active node
            if (inp.LeftDown && _dragHandle >= 0 && _dragNode >= 0 && _dragNode < _nodes.Count)
            {
                var node = _nodes[_dragNode];
                var src = node.HasSource ? node.Source : live;
                if (_dragHandle == 4)
                {
                    if (PickOrbitNu(src, primaryAbs, mouse, out double nu))
                        node.UT = Kepler.TimeAtTrueAnomaly(src, nu, ut);
                }
                else if (ManeuverGeometry(node, ut, out var ns, out var pd, out var rd))
                {
                    Vector2 axis = _dragHandle < 2 ? pd : rd;
                    double proj = Vector2.Dot(mouse - ns, axis);
                    double sens = Math.Max(1.0, Kepler.StateAtTime(src, node.UT).vel.Length * 0.01);
                    double val = _dragStartValue + (proj - _dragStartProj) * sens;
                    if (_dragHandle < 2) node.Prograde = val; else node.Radial = val;
                }
            }
            if (!inp.LeftDown) { _dragHandle = -1; _dragNode = -1; }
        }

        /// <summary>World angle of the next node's burn-delta vector, or NaN if there is none.</summary>
        private double BurnDirAngle(double ut)
        {
            var node = NextNode(ut);
            if (node == null || !ManeuverContext(node, ut, out var orbit, out _, out _)) return double.NaN;
            var (rN, vN) = Kepler.StateAtTime(orbit, node.UT);
            Vec2d bd = node.BurnDelta(rN, vN);
            return bd.Length > 1e-6 ? bd.Angle() : double.NaN;
        }

        private static void AdjustComponent(Maneuver node, int handle, double d)
        {
            switch (handle)
            {
                case 0: node.Prograde += d; break;
                case 1: node.Prograde -= d; break;
                case 2: node.Radial += d; break;
                case 3: node.Radial -= d; break;
            }
        }

        // =====================================================================  draw

        public override void Draw()
        {
            var pb = Ctx.Pb; var sb = Ctx.Sb;
            double ut = Ctx.Clock.UT;
            pb.Begin();
            sb.Begin();

            Ctx.Stars.Draw(pb, Ctx.W, Ctx.H, _cam.Center);

            if (_map) DrawMap(pb, sb, ut);
            else DrawWorld(pb, ut);

            var hud = Hud.Draw(Ctx, _vessel, _pred, _map, FocusName(), NextNode(ut), BurnDirAngle(ut), _burnSpent, _nodes.Count, BuildNavMarkers(ut));
            if (hud.WarpToUT.HasValue) _warpTo = hud.WarpToUT;

            DrawTargetPanel(pb, sb, ut);
            DrawTargetWindow(pb, sb);
            DrawScienceStatus(pb, sb, ut);
            DrawCrewPanel(pb, sb);
            DrawCancelMission(pb, sb);

            if (_toastT > 0 && _toast != null)
            {
                var f = Ctx.FontBig;
                var sz = f.MeasureString(_toast);
                float alpha = (float)Math.Clamp(_toastT, 0, 1);
                var pos = new Vector2(Ctx.W / 2 - sz.X / 2, 70);
                pb.FillRect((int)pos.X - 14, (int)pos.Y - 6, (int)sz.X + 28, (int)sz.Y + 12, new Color(20, 30, 22, (int)(200 * alpha)));
                sb.DrawString(f, _toast, pos, new Color(150, 230, 150) * alpha);
            }

            DrawExitDialog(pb, sb);

            pb.End();
            sb.End();

            if (_endMessage != null)
            {
                pb.Begin();
                sb.Begin();
                pb.FillRect(0, Ctx.H / 2 - 60, Ctx.W, 120, new Color(0, 0, 0, 160));
                var sz = Ctx.FontBig.MeasureString(_endMessage);
                sb.DrawString(Ctx.FontBig, _endMessage, new Vector2(Ctx.W / 2 - sz.X / 2, Ctx.H / 2 - sz.Y / 2), Color.White);
                pb.End();
                sb.End();
            }
        }

        /// <summary>The pause/exit modal: Save game / Return to game / Exit to main menu. Button clicks
        /// are evaluated here (immediate-mode); the actual scene switch is deferred to the next Update
        /// (via <see cref="_exitToTitle"/>) so it doesn't run mid-draw.</summary>
        private void DrawExitDialog(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
        {
            if (!_showExitDialog) return;
            var f = Ctx.Font;
            var inp = Ctx.Input;
            pb.FillRect(0, 0, Ctx.W, Ctx.H, new Color(0, 0, 0, 150));   // dim the world behind
            int w = 320, h = 262;
            var r = new Rectangle(Ctx.W / 2 - w / 2, Ctx.H / 2 - h / 2, w, h);
            UiDraw.Panel(pb, r);
            const string title = "PAUSED";
            var ts = f.MeasureString(title);
            sb.DrawString(f, title, new Vector2(r.Center.X - ts.X / 2, r.Y + 16), UiDraw.Accent);

            int bx = r.X + 30, bw = w - 60, bh = 38, by = r.Y + 52;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Save game", inp))
            { PersistShip(); PersistOthers(); SaveGame.Save(Ctx.State); _toast = "Game saved"; _toastT = 3; }
            by += bh + 10;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Return to game", inp))
                _showExitDialog = false;
            by += bh + 10;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Space Center", inp))
                _exitToHub = true;
            by += bh + 10;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx, by, bw, bh), "Exit to main menu", inp))
                _exitToTitle = true;
        }

        /// <summary>A "Cancel mission" button shown only while the ship is on the pad (landed and never
        /// launched): scraps the flight and reopens the design in the editor. The scene switch is
        /// deferred to the next Update via <see cref="_cancelMission"/>.</summary>
        private void DrawCancelMission(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
        {
            if (_showExitDialog) return;   // don't sit under the modal
            var v = _vessel;
            if (v == null || v.Destroyed || !v.Landed || v.EnginesIgnited) return;
            var rect = new Rectangle(Ctx.W / 2 - 90, 8, 180, 28);
            if (UiDraw.Button(pb, sb, Ctx.Font, rect, "Cancel mission", Ctx.Input)) _cancelMission = true;
        }

        /// <summary>In-flight crew panel: lists crew per crewable axial part, with Up/Dn buttons to
        /// transfer one crew member to the adjacent crewable part when it has a free seat.</summary>
        private void DrawCrewPanel(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
        {
            if (!_showCrew) return;
            var v = _vessel;
            var f = Ctx.Font;
            var inp = Ctx.Input;
            var seated = new List<Solar.Parts.Part>();
            if (v != null && !v.Destroyed) foreach (var p in v.Parts) if (p.SeatCount > 0) seated.Add(p);

            int crew = v?.CrewCount ?? 0;
            int wWin = 280, rows = 0;
            foreach (var p in seated) rows += 1 + p.Crew.Count;
            var r = new Rectangle(Ctx.W - wWin - 10, 120, wWin, 78 + Math.Max(1, rows) * 20);
            UiDraw.Panel(pb, r);
            float y = r.Y + 8;
            sb.DrawString(f, "CREW  [C] close", new Vector2(r.X + 10, y), UiDraw.Accent); y += 22;

            // life-support wellbeing summary (which resource is the limiter, and time left)
            if (v != null && crew > 0)
            {
                bool ok = v.LifeSupportOk;
                string ls;
                if (!ok) ls = "Life support: CRITICAL";
                else
                {
                    // the resource that will run out first, by time-to-empty per current crew draw
                    double tO = TimeLeft(v.Oxygen, crew * Vessel.OxygenPerCrew);
                    double tW = TimeLeft(v.Water, crew * Vessel.WaterPerCrew);
                    double tF = TimeLeft(v.Food, crew * Vessel.FoodPerCrew);
                    double min = Math.Min(tO, Math.Min(tW, tF));
                    string which = min == tO ? "Oxygen" : min == tW ? "Water" : "Food";
                    ls = double.IsPositiveInfinity(min) ? "Life support: OK" : $"{which}: {UiDraw.Time(min)} left";
                }
                sb.DrawString(f, ls, new Vector2(r.X + 10, y), ok ? new Color(150, 220, 150) : new Color(255, 100, 90));
            }
            else sb.DrawString(f, "No crew aboard", new Vector2(r.X + 10, y), UiDraw.TextDim);
            y += 22;

            if (seated.Count == 0)
            { sb.DrawString(f, "No crew-capable parts", new Vector2(r.X + 10, y), UiDraw.TextDim); return; }

            Color crewCol = (v != null && !v.LifeSupportOk) ? new Color(255, 100, 90) : Color.White;
            Solar.Parts.Part moveFrom = null, moveTo = null;
            for (int k = 0; k < seated.Count; k++)
            {
                var p = seated[k];
                sb.DrawString(f, $"{p.Def.Name}  ({p.Crew.Count}/{p.SeatCount})", new Vector2(r.X + 10, y), crewCol);
                if (p.Crew.Count > 0)
                {
                    if (k > 0 && seated[k - 1].Crew.Count < seated[k - 1].SeatCount
                        && UiDraw.Button(pb, sb, f, new Rectangle(r.Right - 72, (int)y - 2, 30, 20), "Up", inp))
                    { moveFrom = p; moveTo = seated[k - 1]; }
                    if (k < seated.Count - 1 && seated[k + 1].Crew.Count < seated[k + 1].SeatCount
                        && UiDraw.Button(pb, sb, f, new Rectangle(r.Right - 38, (int)y - 2, 30, 20), "Dn", inp))
                    { moveFrom = p; moveTo = seated[k + 1]; }
                }
                y += 20;
                foreach (var c in p.Crew)
                { sb.DrawString(f, $"  - {c.Name} ({c.Role})", new Vector2(r.X + 10, y), UiDraw.TextDim); y += 20; }
            }
            if (moveFrom != null && moveTo != null) v.TransferCrew(moveFrom, moveTo);
        }

        private string FocusName() =>
            _focus == 0 ? "Vessel" : Ctx.Universe.Bodies[_focus - 1].Name;

        /// <summary>Compact target readout (below the left HUD panel): name, distance, rendezvous.</summary>
        private void DrawTargetPanel(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            if (!HasTarget) return;
            var f = Ctx.Font;
            var r = new Rectangle(10, 286, 256, 188);
            UiDraw.Panel(pb, r);
            float y = r.Y + 8;
            sb.DrawString(f, "TARGET  [Tab] cycle  [T] list", new Vector2(r.X + 10, y), UiDraw.Accent); y += 20;
            void Row(string label, string value, Color? c = null)
            {
                sb.DrawString(f, label, new Vector2(20, y), UiDraw.TextDim);
                sb.DrawString(f, value, new Vector2(120, y), c ?? Color.White);
                y += 18;
            }
            Row("Name", _targetName ?? "-", TargetColor);
            bool alive = _vessel != null && !_vessel.Destroyed;
            if (alive)
            {
                Vec2d sep = TargetPos(ut) - _vessel.AbsolutePosition(ut);
                Row("Distance", UiDraw.Dist(sep.Length));
                Vec2d rel = _vessel.AbsoluteVelocity(ut) - TargetVel(ut);
                bool closing = rel.Dot(sep) > 0;   // velocity points toward the target
                Row("Rel. vel.", UiDraw.Speed(rel.Length), closing ? new Color(150, 220, 150) : new Color(255, 170, 90));
                Row("", closing ? "closing" : "opening", closing ? new Color(150, 220, 150) : new Color(255, 170, 90));
            }
            // docking cue: when both craft have a free port and share an SOI, show capture readiness
            // (orbital dock) or surface-connect readiness (both landed)
            if (alive && _targetVessel != null
                && _vessel.HasFreeDockingPort && _targetVessel.HasFreeDockingPort
                && _targetVessel.Body == _vessel.Body)
            {
                double d = (_targetVessel.Position - _vessel.Position).Length;
                var green = new Color(150, 230, 150); var blue = new Color(120, 190, 230);
                if (_vessel.Landed && _targetVessel.Landed)
                {
                    bool ready = d < ConnectDist;
                    Row("Connect", ready ? "connecting..." : $"approach < {ConnectDist:0} m", ready ? green : blue);
                }
                else if (!_vessel.Landed && !_targetVessel.Landed)
                {
                    double rs = (_targetVessel.Velocity - _vessel.Velocity).Length;
                    bool ready = d < CaptureDist && rs < SoftDockSpeed;
                    string txt = ready ? "capturing..."
                               : d >= CaptureDist ? $"close to {CaptureDist:0} m"
                               : $"slow below {SoftDockSpeed:0.0} m/s";
                    Row("Dock", txt, ready ? green : blue);
                }
            }
            if (_caValid)
            {
                Row("Close App.", UiDraw.Dist(_caSep), new Color(150, 220, 150));
                Row("  in", UiDraw.Time(_caUT - ut));
                Row("  rel. vel.", UiDraw.Speed(_caRelSpeed));
            }
            if (_prox.Count > 0)
            {
                bool meet = false; double nearest = double.MaxValue;
                foreach (var p in _prox) { if (p.Intersect) meet = true; if (p.Sep < nearest) nearest = p.Sep; }
                if (meet) Row("Orbits", "intersect", new Color(255, 190, 70));
                else Row("Orbit gap", UiDraw.Dist(nearest), new Color(140, 230, 160));
            }
        }

        /// <summary>Togglable list window for picking a target by clicking.</summary>
        private void DrawTargetWindow(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb)
        {
            if (!_showTargetWindow) return;
            var f = Ctx.Font; var inp = Ctx.Input;
            var items = TargetItems();
            const int rowH = 20;
            int maxRows = Math.Max(6, (Ctx.H - 160) / rowH);
            int rows = Math.Min(items.Count + 1, maxRows);   // +1 for the "None" row
            int wWin = 230, hWin = rows * rowH + 34;
            var r = new Rectangle(Ctx.W / 2 - wWin / 2, 70, wWin, hWin);
            UiDraw.Panel(pb, r);
            sb.DrawString(f, "SELECT TARGET  [T] close", new Vector2(r.X + 8, r.Y + 6), UiDraw.Accent);

            int y = r.Y + 28;
            void RowBox(bool selected, string label, Action onClick)
            {
                var rr = new Rectangle(r.X + 6, y, wWin - 12, rowH - 1);
                bool hover = rr.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);
                if (selected) pb.FillRect(rr, new Color(60, 95, 140, 200));
                else if (hover) pb.FillRect(rr, new Color(45, 70, 105, 200));
                sb.DrawString(f, label, new Vector2(rr.X + 6, rr.Y + 2), selected ? Color.White : UiDraw.TextDim);
                if (hover && inp.LeftClick) onClick();
                y += rowH;
            }

            RowBox(!HasTarget, "(none)", ClearTarget);
            for (int i = 0; i < items.Count && i < rows - 1; i++)
            {
                var it = items[i];
                bool sel = it.body == _targetBody && it.vessel == _targetVessel && HasTarget;
                RowBox(sel, it.name, () => SetTarget(it.body, it.vessel, it.name));
            }
        }

        private void DrawWorld(PrimitiveBatch pb, double ut)
        {
            foreach (var b in Ctx.Universe.Bodies)
                PlanetRenderer.Draw(pb, _cam, b, ut, false, Ctx.Sb, Ctx.Textures.Body(b.TextureId));

            if (_vessel != null && !_vessel.Destroyed && !_vessel.Landed)
                _dust.Draw(pb, _cam, _vessel.Velocity);

            foreach (var d in _debris)
                VesselRenderer.Draw(pb, _cam, d, ut, _anim, tex: Ctx.Textures);

            foreach (var ts in _others)
                VesselRenderer.Draw(pb, _cam, ts.V, ut, _anim, tex: Ctx.Textures);

            if (_vessel != null && !_vessel.Destroyed)
                VesselRenderer.Draw(pb, _cam, _vessel, ut, _anim, tex: Ctx.Textures);

            if (HasTarget && _vessel != null && !_vessel.Destroyed)
                DrawTargetIndicator(pb, ut);
        }

        /// <summary>Flight-view aid: a magenta reticle on the target when it is on screen, or an
        /// arrow pinned to the screen edge pointing toward it when off screen, with a distance label.</summary>
        private void DrawTargetIndicator(PrimitiveBatch pb, double ut)
        {
            var sb = Ctx.Sb;
            var tScreen = _cam.WorldToScreenD(TargetPos(ut));
            double dist = (TargetPos(ut) - _vessel.AbsolutePosition(ut)).Length;
            const float margin = 36f;
            bool onScreen = tScreen.X >= margin && tScreen.X <= Ctx.W - margin &&
                            tScreen.Y >= margin && tScreen.Y <= Ctx.H - margin;
            if (onScreen)
            {
                var p = new Vector2((float)tScreen.X, (float)tScreen.Y);
                pb.CircleOutline(p, 11, 1.6f, TargetColor);
                pb.Line(p + new Vector2(-16, 0), p + new Vector2(-11, 0), 1.5f, TargetColor);
                pb.Line(p + new Vector2(11, 0), p + new Vector2(16, 0), 1.5f, TargetColor);
                pb.Line(p + new Vector2(0, -16), p + new Vector2(0, -11), 1.5f, TargetColor);
                pb.Line(p + new Vector2(0, 11), p + new Vector2(0, 16), 1.5f, TargetColor);
                sb.DrawString(Ctx.Font, $"{_targetName}  {UiDraw.Dist(dist)}", p + new Vector2(14, 10), TargetColor);
            }
            else
            {
                // clamp the direction to the screen-edge margin box and draw an arrow toward it
                var center = new Vector2(Ctx.W / 2f, Ctx.H / 2f);
                var dir = new Vector2((float)tScreen.X - center.X, (float)tScreen.Y - center.Y);
                if (dir.LengthSquared() < 1e-3f) dir = new Vector2(0, -1);
                dir.Normalize();
                float halfW = Ctx.W / 2f - margin, halfH = Ctx.H / 2f - margin;
                float scale = Math.Min(halfW / Math.Max(1e-3f, Math.Abs(dir.X)), halfH / Math.Max(1e-3f, Math.Abs(dir.Y)));
                var edge = center + dir * scale;
                var perp = new Vector2(-dir.Y, dir.X);
                pb.Line(edge - dir * 10 + perp * 7, edge, 2f, TargetColor);
                pb.Line(edge - dir * 10 - perp * 7, edge, 2f, TargetColor);
                sb.DrawString(Ctx.Font, $"{_targetName}  {UiDraw.Dist(dist)}", edge - dir * 18 + new Vector2(6, 6), TargetColor);
            }
        }

        private void DrawMap(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            var u = Ctx.Universe;

            // body orbits
            foreach (var b in u.Bodies)
            {
                if (b.Parent == null) continue;
                Vec2d parentAbs = b.Parent.AbsolutePositionAt(ut);
                OrbitRenderer.DrawConic(pb, _cam, b.Orbit, parentAbs, b.BodyColor * 0.4f, 1.2f);
            }

            // SOI circles
            foreach (var b in u.Bodies)
            {
                if (double.IsInfinity(b.SoiRadius)) continue;
                double soiPx = b.SoiRadius / _cam.MetersPerPixel;
                if (soiPx < 12 || soiPx > 40000) continue;
                var s = _cam.WorldToScreenD(b.AbsolutePositionAt(ut));
                if (!_cam.OnScreen(s, soiPx + 50)) continue;
                pb.DashedCircleOutline(new Vector2((float)s.X, (float)s.Y), (float)soiPx, 1f, b.BodyColor * 0.45f);
            }

            // bodies
            foreach (var b in u.Bodies)
                PlanetRenderer.Draw(pb, _cam, b, ut, true, Ctx.Sb, Ctx.Textures.Body(b.TextureId));

            // body labels
            foreach (var b in u.Bodies)
            {
                var s = _cam.WorldToScreenD(b.AbsolutePositionAt(ut));
                if (!_cam.OnScreen(s, 0)) continue;
                double orbitPx = b.Parent == null ? double.MaxValue : b.Orbit.A / _cam.MetersPerPixel;
                if (orbitPx < 40) continue; // too cluttered at this zoom
                sb.DrawString(Ctx.Font, b.Name, new Vector2((float)s.X + 8, (float)s.Y - 18), b.BodyColor * 0.9f);
            }

            // vessel trajectory + markers
            bool alive = _vessel != null && !_vessel.Destroyed;
            if (alive && !_vessel.Landed && _pred != null)
            {
                var el = _pred.Orbit;
                Vec2d primaryAbs = _pred.Body.AbsolutePositionAt(ut);
                var trajColor = new Color(110, 220, 255);

                if (_pred.Type == TransitionType.None)
                {
                    OrbitRenderer.DrawConicGlow(pb, _cam, el, primaryAbs, trajColor, _pred.Body.SoiRadius);
                    DrawApPeMarkers(pb, sb, el, primaryAbs, _pred.Body.Radius, PeColor, ApColor);
                }
                else
                {
                    OrbitRenderer.DrawTrajectory(pb, _cam, el, primaryAbs, ut, _pred.TransitionUT, trajColor, 1.6f);
                    // transition marker
                    Vec2d mPos = primaryAbs + Kepler.StateAtTime(el, _pred.TransitionUT).pos;
                    var ms = _cam.WorldToScreen(mPos);
                    pb.CircleOutline(ms, 6, 1.5f, new Color(255, 200, 110));
                    if (!el.Hyperbolic && _pred.TransitionUT - ut > el.Period * 0.4)
                        DrawApPeMarkers(pb, sb, el, primaryAbs, _pred.Body.Radius, PeColor, ApColor);

                    // post-transition conic (ghost) around the next body at transition time
                    if (_pred.NextBody != null)
                    {
                        Vec2d nbAbs = _pred.NextBody.AbsolutePositionAt(_pred.TransitionUT);
                        var c2 = new Color(255, 170, 90);
                        OrbitRenderer.DrawConic(pb, _cam, _pred.NextOrbit, nbAbs, c2, 1.4f, _pred.NextBody.SoiRadius);
                        if (_pred.Type == TransitionType.Encounter)
                        {
                            // ghost of the body at encounter time
                            var gs = _cam.WorldToScreenD(nbAbs);
                            if (_cam.OnScreen(gs, 100))
                                pb.CircleOutline(new Vector2((float)gs.X, (float)gs.Y),
                                    (float)Math.Max(4, _pred.NextBody.Radius / _cam.MetersPerPixel), 1.2f, c2 * 0.8f);
                        }
                    }
                }
            }

            if (HasTarget) DrawTarget(pb, sb, ut);
            if (alive && _nodes.Count > 0) DrawManeuver(pb, sb, ut);

            foreach (var d in _debris)
                VesselRenderer.Draw(pb, _cam, d, ut, _anim, forceIcon: true);

            foreach (var ts in _others)
                VesselRenderer.Draw(pb, _cam, ts.V, ut, _anim, forceIcon: true);

            if (alive)
                VesselRenderer.Draw(pb, _cam, _vessel, ut, _anim, forceIcon: true);
        }

        private static readonly Color TargetColor = new Color(235, 130, 235);

        /// <summary>Highlights the target's orbit, marks its current position, and (when a rendezvous
        /// solution exists) draws closest-approach markers on both orbits with a separation label.</summary>
        private void DrawTarget(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            // target body/vessel orbit
            if (TargetOrbitBody(ut, out var tEl, out var tPrimary))
                OrbitRenderer.DrawConic(pb, _cam, tEl, tPrimary.AbsolutePositionAt(ut), TargetColor * 0.8f, 1.4f);

            // current target position marker
            var tNow = _cam.WorldToScreenD(TargetPos(ut));
            if (_cam.OnScreen(tNow, 30))
            {
                var p = new Vector2((float)tNow.X, (float)tNow.Y);
                pb.CircleOutline(p, 9, 1.6f, TargetColor);
                pb.Line(p + new Vector2(-12, 0), p + new Vector2(12, 0), 1f, TargetColor * 0.7f);
                pb.Line(p + new Vector2(0, -12), p + new Vector2(0, 12), 1f, TargetColor * 0.7f);
                sb.DrawString(Ctx.Font, _targetName ?? "Target", p + new Vector2(12, 8), TargetColor);
            }

            // closest-approach markers
            if (_caValid && YourPlannedOrbit(ut, out var you, out var youPrimary))
            {
                Vec2d youCa = youPrimary.AbsolutePositionAt(_caUT) + Kepler.StateAtTime(you, _caUT).pos;
                Vec2d tgtCa = TargetCaPos(_caUT);
                var ys = _cam.WorldToScreen(youCa);
                var tsScreen = _cam.WorldToScreen(tgtCa);
                pb.Line(ys, tsScreen, 1f, new Color(180, 180, 200, 160));
                pb.FillCircle(ys, 4, new Color(120, 220, 255));
                pb.CircleOutline(tsScreen, 5, 1.5f, TargetColor);
                var mid = (ys + tsScreen) / 2;
                sb.DrawString(Ctx.Font, UiDraw.Dist(_caSep), mid + new Vector2(6, -6), new Color(200, 210, 230));
            }

            // geometric orbit-curve proximities: intersections ("Meet") + up-to-2 closest points
            var meet = new Color(255, 190, 70);
            var close = new Color(140, 230, 160);
            foreach (var p in _prox)
            {
                var ys = _cam.WorldToScreenD(p.YouPos);
                if (!_cam.OnScreen(ys, 40)) continue;
                var yp = new Vector2((float)ys.X, (float)ys.Y);
                if (p.Intersect)
                {
                    pb.CircleOutline(yp, 6, 1.8f, meet);
                    pb.Line(yp + new Vector2(-4, -4), yp + new Vector2(4, 4), 1.4f, meet);
                    pb.Line(yp + new Vector2(-4, 4), yp + new Vector2(4, -4), 1.4f, meet);
                    sb.DrawString(Ctx.Font, "Meet", yp + new Vector2(8, -8), meet);
                }
                else
                {
                    var ts2 = _cam.WorldToScreen(p.TgtPos);
                    pb.Line(yp, ts2, 1f, close * 0.6f);
                    pb.CircleOutline(yp, 5, 1.6f, close);
                    pb.FillCircle(ts2, 3, close * 0.85f);
                    sb.DrawString(Ctx.Font, UiDraw.Dist(p.Sep), yp + new Vector2(8, -8), close);
                }
            }
        }

        /// <summary>Target's absolute position at an arbitrary UT, propagated along its conic.</summary>
        private Vec2d TargetCaPos(double ut)
        {
            if (TargetOrbitBody(ut, out var tEl, out var tPrimary))
                return tPrimary.AbsolutePositionAt(ut) + Kepler.StateAtTime(tEl, ut).pos;
            return TargetPos(ut);
        }

        private void DrawApPeMarkers(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                                     in OrbitalElements el, Vec2d primaryAbs, double bodyRadius, Color peCol, Color apCol)
        {
            var peW = OrbitRenderer.PeriapsisPoint(el, primaryAbs);
            var peS = _cam.WorldToScreenD(peW);
            if (_cam.OnScreen(peS, 50))
            {
                var p = new Vector2((float)peS.X, (float)peS.Y);
                pb.FillCircle(p, 4, peCol);
                sb.DrawString(Ctx.Font, $"Pe {UiDraw.Dist(el.Periapsis - bodyRadius)}", p + new Vector2(8, -8), peCol);
            }
            if (!el.Hyperbolic)
            {
                var apW = OrbitRenderer.ApoapsisPoint(el, primaryAbs);
                var apS = _cam.WorldToScreenD(apW);
                if (_cam.OnScreen(apS, 50))
                {
                    var p = new Vector2((float)apS.X, (float)apS.Y);
                    pb.FillCircle(p, 4, apCol);
                    sb.DrawString(Ctx.Font, $"Ap {UiDraw.Dist(el.Apoapsis - bodyRadius)}", p + new Vector2(8, -8), apCol);
                }
            }
        }

        private static readonly Color PeColor = new Color(120, 220, 255);
        private static readonly Color ApColor = new Color(170, 150, 255);

        /// <summary>Draws every planned node: chained orbit previews, Ap/Pe + encounter for the
        /// final patch, plus each node's marker, delta-v handles and X delete button. Nodes are
        /// consumed the moment their burn time passes, so only pending future nodes are ever drawn.</summary>
        private void DrawManeuver(PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb, double ut)
        {
            if (!LiveOrbit(ut, out _, out var body, out var primaryAbs)) return;
            var orange = new Color(255, 170, 90);

            var sorted = Sorted(ut);
            // the last node owns the final planned orbit (Ap/Pe + encounter shown there)
            Maneuver last = null;
            foreach (var n in sorted) if (n.UT >= ut) last = n;

            foreach (var node in sorted)
            {
                if (!node.HasSource) continue;

                // planned-orbit preview
                var planned = node.ResultOrbit(node.Source, body.Mu);
                if (double.IsNaN(planned.A)) continue;
                if (node == last)
                {
                    OrbitRenderer.DrawConicGlow(pb, _cam, planned, primaryAbs, orange, body.SoiRadius);
                    DrawApPeMarkers(pb, sb, planned, primaryAbs, body.Radius, orange, new Color(255, 210, 140));
                    var pp = TrajectoryPredictor.Predict(planned, body, node.UT);
                    if (pp.Type != TransitionType.None)
                    {
                        Vec2d mPos = primaryAbs + Kepler.StateAtTime(planned, pp.TransitionUT).pos;
                        pb.CircleOutline(_cam.WorldToScreen(mPos), 6, 1.5f, orange);
                        if (pp.NextBody != null)
                        {
                            Vec2d nbAbs = pp.NextBody.AbsolutePositionAt(pp.TransitionUT);
                            OrbitRenderer.DrawConic(pb, _cam, pp.NextOrbit, nbAbs, orange * 0.85f, 1.4f, pp.NextBody.SoiRadius);
                        }
                    }
                }
                else
                {
                    OrbitRenderer.DrawConic(pb, _cam, planned, primaryAbs, orange * 0.7f, 1.3f, body.SoiRadius);
                }

                if (!ManeuverGeometry(node, ut, out var nodeScreen, out var proDir, out var radDir)) continue;

                // burn-direction arrow + delta-v handles
                var (rN, vN) = Kepler.StateAtTime(node.Source, node.UT);
                Vec2d bd = node.BurnDelta(rN, vN);
                if (bd.Length > 1e-6)
                {
                    Vec2d u = bd.Normalized();
                    var dir = new Vector2((float)u.X, -(float)u.Y);
                    float len = (float)Math.Min(64, 18 + node.DeltaV * 0.05);
                    pb.Line(nodeScreen, nodeScreen + dir * len, 2f, PeColor);
                }

                Vector2[] hp = { nodeScreen + proDir * HandleDist, nodeScreen - proDir * HandleDist,
                                 nodeScreen + radDir * HandleDist, nodeScreen - radDir * HandleDist };
                Color[] hc = { new Color(120, 255, 140), new Color(255, 120, 110),
                               new Color(120, 210, 255), new Color(255, 200, 120) };
                int ni = _nodes.IndexOf(node);
                for (int k = 0; k < 4; k++)
                {
                    pb.Line(nodeScreen, hp[k], 1f, hc[k] * 0.5f);
                    float r = (_dragNode == ni && _dragHandle == k) || (_hoverNode == ni && _hoverHandle == k) ? 7f : 5f;
                    pb.FillCircle(hp[k], r, hc[k]);
                }

                // node marker
                pb.CircleOutline(nodeScreen, 8, 2f, Color.White);
                pb.FillCircle(nodeScreen, 3, PeColor);

                // X delete button
                var xp = XButtonPos(nodeScreen);
                bool xHot = _hoverX == _nodes.IndexOf(node);
                Color xc = xHot ? new Color(255, 120, 110) : new Color(220, 200, 200);
                pb.FillCircle(xp, 7, new Color(40, 20, 24, 220));
                pb.CircleOutline(xp, 7, 1.5f, xc);
                pb.Line(xp + new Vector2(-3, -3), xp + new Vector2(3, 3), 1.6f, xc);
                pb.Line(xp + new Vector2(-3, 3), xp + new Vector2(3, -3), 1.6f, xc);
            }
        }
    }
}
