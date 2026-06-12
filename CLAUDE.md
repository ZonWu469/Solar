# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Solar is a 2D Kerbal-Space-Program-style rocket simulator built on **MonoGame / .NET 8** (DesktopGL). The player assembles a rocket in a VAB-style editor, launches it, flies under physics, and coasts on analytic Kepler orbits with KSP-style map view, time warp, SOI transitions, and maneuver-node planning.

## Commands

```bash
dotnet build                  # build (uses Solar.csproj / Solar.slnx)
dotnet run                    # launch the game (opens a window)
dotnet run -- --selftest      # run physics self-tests headlessly, print "N/N PASS", exit
dotnet run --no-build -- --selftest   # same, skip rebuild
```

There is **no test framework**. Physics correctness is guarded by [Tests/SanityChecks.cs](Tests/SanityChecks.cs), a hand-rolled suite (`SanityChecks.Run()` returns a pass/fail string). It runs at startup (shown bottom-left of the main menu) and via the `--selftest` flag. **Add a new `Check(...)` here when you touch orbital math**, and verify with `dotnet run -- --selftest` — this is the only automated verification available, since GUI behavior can't be checked headlessly.

## Architecture

### Scene loop
[SolarGame.cs](SolarGame.cs) builds a single [GameContext](Core/GameContext.cs) (shared services: graphics batches, fonts, `Universe`, `SimClock`, `InputState`, `VesselDesign`, `SceneManager`) and delegates Update/Draw to the current [Scene](Core/Scene.cs). Three scenes: [MainMenuScene](Scenes/MainMenuScene.cs) → [EditorScene](Scenes/EditorScene.cs) (assemble the rocket) → [FlightScene](Scenes/FlightScene.cs) (fly it). Scenes advance universal time (`Clock.UT`) themselves rather than the clock advancing globally.

### The on-rails / off-rails model (the central concept)
A vessel is in one of two states, and `FlightScene.Update` switches between them every frame:
- **Off-rails (physics):** [Integrator.cs](Physics/Integrator.cs) RK4-steps position/velocity under gravity + thrust + drag. Used while landed-and-thrusting, under thrust, or in atmosphere. Time warp is capped to `SimClock.PhysicsMaxIndex` (4×).
- **On-rails (analytic):** the vessel follows a fixed `OrbitalElements` conic; position is evaluated directly via `Kepler.StateAtTime`. Used when coasting in vacuum. Allows unlimited time warp and exact-time SOI handoffs.

`Vessel.GoOnRails`/`GoOffRails`/`UpdateFromRails` convert between a live state vector and conic elements. `CurrentElements(ut)` returns the live conic in either state — **prefer it over `Vessel.Orbit` directly.**

### Orbital mechanics (2D Kepler)
All in [Physics/](Physics/). Everything is double-precision and 2D.
- [OrbitalElements.cs](Physics/OrbitalElements.cs): the conic. Anomalies increase in the direction of motion; `Dir` (+1 CCW / −1 CW) maps orbital angle → world angle as `ArgPe + Dir*nu`. `A` is negative for hyperbolic (`E > 1`).
- [Kepler.cs](Physics/Kepler.cs): the workhorse. `StateAtTime`, `StateAtTrueAnomaly`, `ElementsFromState` (state↔elements), `TimeAtTrueAnomaly`, and `NextRadiusCrossing{Inbound,Outbound}`. **Reuse these instead of writing new orbital math** — e.g. a maneuver burn is just `ElementsFromState` applied to a perturbed velocity (see [Maneuver.cs](Physics/Maneuver.cs)).
- [TrajectoryPredictor.cs](Physics/TrajectoryPredictor.cs): given `(elements, primary, utNow)`, finds the earliest of escape / encounter / atmosphere-entry (one level deep, KSP-style) by analytic radius crossings + time-sampled encounter search. Drives both the live trajectory preview and the maneuver-node projection.
- [CelestialBody.cs](Physics/CelestialBody.cs) + [Universe.cs](Physics/Universe.cs): a parent/child body hierarchy; bodies are themselves on-rails. SOI radii via `ComputeSoiRadii()`. The system is defined in [SolarSystemData.cs](Physics/SolarSystemData.cs). Bodies do **not** rotate.

### Coordinates & rendering
- `Vessel.Position`/`Velocity` are **relative to the current SOI body's center**. `AbsolutePosition(ut)` walks up the parent chain.
- [Camera2D.cs](Rendering/Camera2D.cs) does world→screen entirely in doubles with a floating origin at `Center`, so float precision never touches planet-scale coordinates. Convert to `Vector2` only at the render boundary.
- [PrimitiveBatch.cs](Rendering/PrimitiveBatch.cs) is an immediate-mode triangle-list renderer in screen-pixel space (`FillCircle`, `Line`, `Quad`, `RingArc`, …). All shape drawing goes through it; there are no textures. Renderers ([PlanetRenderer](Rendering/PlanetRenderer.cs), [VesselRenderer](Rendering/VesselRenderer.cs), [OrbitRenderer](Rendering/OrbitRenderer.cs), [StarfieldRenderer](Rendering/StarfieldRenderer.cs), [DustField](Rendering/DustField.cs)) are static/stateless helpers that take the camera and draw.
- HUD/UI is immediate-mode too: [UI/Hud.cs](UI/Hud.cs) (flight HUD, navball, maneuver panel) and [UI/UiDraw.cs](UI/UiDraw.cs) (shared panel/button/bar helpers + number formatters `Dist`/`Speed`/`Time`/`Force`/`Accel`/`Pressure`).

### Vessel & staging
[Parts/](Parts/) defines immutable `PartDef`s ([PartCatalog](Parts/PartCatalog.cs)) and runtime `Part`s. A [Vessel](Vessel/Vessel.cs) is an ordered part stack; **decouplers are stage boundaries** — `Vessel.Segments()` splits the stack at them. [Staging.cs](Vessel/Staging.cs) both fires stages at runtime (`FireNext`) and computes per-stage Δv/TWR/burn-time via the rocket equation (`ComputeStages`, used by editor and HUD).

## Conventions / gotchas

- **Font is ASCII-only.** The bundled SpriteFont ([Content/Hud.spritefont](Content/Hud.spritefont)) covers char codes 32–126 with `*` as the default. Non-ASCII glyphs (`Δ`, `²`, `×`) render as `*` — use ASCII (`dV`, `m/s2`) in UI strings.
- **Angle/screen convention:** world angles are standard math (`atan2(y,x)`, CCW); screen Y is flipped, so a world direction `(dx,dy)` maps to screen `(dx, -dy)`. The navball and maneuver handles rely on this.
- Time warp levels and the physics cap live in [SimClock.cs](Core/SimClock.cs); `MaxWarpIndex` is re-set by the scene every frame based on whether physics is active.
- `InputState` ([Core/InputState.cs](Core/InputState.cs)) gives per-frame edge detection (`Pressed`/`Released`/`LeftClick`/`WheelDelta`); use it rather than polling MonoGame directly.
