using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Solar.Core;
using Solar.Parts;
using Solar.Vessels;

namespace Solar.Rendering
{
    /// <summary>Draws a vessel's part stack as procedural shapes, or a triangle icon when tiny/in map view.</summary>
    public static class VesselRenderer
    {
        /// <summary>Draw the vessel. When <paramref name="pickHits"/> is supplied, each part's screen-space
        /// footprint (four corners, in draw winding) is appended to it for click hit-testing — kept in sync
        /// with rendering by construction. No footprints are recorded in the tiny/icon path.</summary>
        public static void Draw(PrimitiveBatch pb, Camera2D cam, Vessel v, double ut, double anim, bool forceIcon = false, TextureStore tex = null,
                                List<(Part part, Vector2[] quad)> pickHits = null)
        {
            Vec2d basePos = v.AbsolutePosition(ut);
            Vec2d sD = cam.WorldToScreenD(basePos);
            if (!cam.OnScreen(sD, 3000)) return;

            var baseS = new Vector2((float)sD.X, (float)sD.Y);
            float pxPerM = (float)(1.0 / cam.MetersPerPixel);
            float hPx = (float)Math.Max(v.TotalHeight, 2) * pxPerM;

            const float OpenChuteWidthM = 11f;   // deployed canopy width (m); height follows the art's aspect

            var upS = new Vector2((float)Math.Cos(v.Heading), -(float)Math.Sin(v.Heading));
            var rightS = new Vector2(-upS.Y, upS.X);

            // Local-vertical (zenith) in screen space: v.Position points from the SOI body center to the
            // vessel, so its angle is "away from the planet". Y is flipped going world->screen. A deployed
            // parachute hangs along this (opposite gravity), independent of the ship's heading.
            double zenA = v.Position.Angle();
            var vertS = new Vector2((float)Math.Cos(zenA), -(float)Math.Sin(zenA));
            var vperpS = new Vector2(-vertS.Y, vertS.X);   // across the canopy

            // Draw a deployed-parachute canopy texture hanging from the part top (anchor). The image's
            // bottom-center is the cables-attachment node, pinned at the anchor; the canopy (image top)
            // extends out along the zenith (vertS), and the art's aspect ratio is preserved.
            void DrawOpenChute(Microsoft.Xna.Framework.Graphics.Texture2D open, Vector2 anchor, Solar.Parts.PartDef cd)
            {
                float wM = cd.ParachuteWidth > 0 ? (float)cd.ParachuteWidth : OpenChuteWidthM;
                float hM = cd.ParachuteHeight > 0 ? (float)cd.ParachuteHeight : wM * (float)open.Height / open.Width;
                float halfW = (wM * 0.5f) * pxPerM, hPxLen = hM * pxPerM;
                Vector2 topL = anchor + vertS * hPxLen - vperpS * halfW;
                Vector2 topR = anchor + vertS * hPxLen + vperpS * halfW;
                Vector2 botL = anchor - vperpS * halfW;
                Vector2 botR = anchor + vperpS * halfW;
                pb.TexturedQuad(open, topL, topR, botR, botL, Color.White);   // a,b = image top; c,d = image bottom
            }

            if (forceIcon || hPx < 7)
            {
                Color ic = v.IsDebris ? new Color(140, 140, 140) : Color.White;
                Vector2 tip = baseS + upS * 7;
                pb.Tri(tip, baseS - upS * 4 + rightS * 4.5f, baseS - upS * 4 - rightS * 4.5f, ic);
                return;
            }

            bool flaming = !v.IsDebris && v.CurrentThrust > 0;   // solids flame at any throttle

            // RCS translation cue: while actively translating, the gas is expelled OPPOSITE the
            // commanded direction (Newton's third law). jx/jy is that local jet direction
            // (local +x = right, +y = nose), matching Vessel.RcsAccel's body frame.
            bool rcsFiring = !v.IsDebris && v.RcsActive;
            double rcsLen = v.RcsCommand.Length;
            float rcsMag = (float)Math.Min(1, rcsLen);
            float jx = 0, jy = 0;
            if (rcsLen > 1e-6) { jx = -(float)(v.RcsCommand.X / rcsLen); jy = -(float)(v.RcsCommand.Y / rcsLen); }

            // off-axis (radial / lateral) engines fire on the signed Q/E command; their plume exits
            // opposite the thrust direction, so it swings to the craft's other side as the player flips J<->L.
            float latCmd = !v.IsDebris ? (float)Math.Clamp(v.RcsCommand.X, -1, 1) : 0f;
            bool radialFlaming = !v.IsDebris && v.RadialThrusting;

            // Draw one sub-stack (the root stack, or a docked module) in the vessel's local frame.
            // A module's parts are rotated by `q` quarter-turns and shifted by `off` so its ports overlap.
            void DrawStack(int from, int to, int q, Vec2d off)
            {
            Vector2 P(float lx, float ly)
            {
                float mx = lx, my = ly;
                for (int t = 0; t < (q & 3); t++) { float nx = -my, ny = mx; mx = nx; my = ny; }   // Perp = +90 CCW
                mx += (float)off.X; my += (float)off.Y;
                return baseS + rightS * (mx * pxPerM) + upS * (my * pxPerM);
            }
            // Engine/booster exhaust plume, driven by the part's data-driven exhaust appearance.
            // cx = local x of the nozzle axis, baseY = nozzle exit y, halfWRef = legacy outer half-width
            // (w*0.30), len = legacy flame length (already includes throttle+flicker). Reproduces the old
            // orange plume when the part uses fallback exhaust values (scales = 1).
            void DrawPlume(float cx, float baseY, float halfWRef, float len, float throttle, PartDef d, float flick)
            {
                float ws = d.ExhaustWidthScale;
                float outerHalf = halfWRef * ws;
                float innerHalf = halfWRef * 0.533f * ws;
                float L = len * d.ExhaustLengthScale;
                // A5: throttle-reactive core hue — toward white at full throttle.
                Color core = Color.Lerp(d.ExhaustCoreColor, Color.White, 0.35f * MathHelper.Clamp(throttle, 0f, 1f));
                pb.Tri(P(cx - outerHalf, baseY + 0.05f), P(cx + outerHalf, baseY + 0.05f), P(cx, baseY - L), d.ExhaustColor);
                pb.Tri(P(cx - innerHalf, baseY + 0.05f), P(cx + innerHalf, baseY + 0.05f), P(cx, baseY - L * 0.55f), core);
                // A4: Mach diamonds on high-thrust engines — small bright lozenges along the axis, pulsed.
                if (d.Thrust >= 200000)
                {
                    float pulse = MathHelper.Clamp(0.6f + 0.4f * (flick - 1f) / 0.12f, 0.3f, 1f);
                    var dia = new Color(255, 245, 220) * pulse;
                    for (int m = 0; m < 3; m++)
                    {
                        float f = 0.22f + m * 0.18f;
                        float dy = baseY - L * f;
                        float dw = innerHalf * (0.5f - 0.12f * m);
                        float dh = L * 0.05f;
                        pb.Tri(P(cx - dw, dy), P(cx, dy - dh), P(cx, dy + dh), dia);
                        pb.Tri(P(cx + dw, dy), P(cx, dy - dh), P(cx, dy + dh), dia);
                    }
                }
            }
            // Plume along an arbitrary local direction (dx,dy unit) — used by off-axis radial engines whose
            // exhaust does not exit straight down the stack.
            void DrawDirPlume(float cx, float cy, float dx, float dy, float halfWRef, float len, PartDef d)
            {
                float ws = d.ExhaustWidthScale;
                float outerHalf = halfWRef * ws, innerHalf = halfWRef * 0.533f * ws;
                float L = len * d.ExhaustLengthScale;
                float px = -dy, py = dx;   // across the jet axis
                Color core = Color.Lerp(d.ExhaustCoreColor, Color.White, 0.30f);
                pb.Tri(P(cx + px * outerHalf, cy + py * outerHalf), P(cx - px * outerHalf, cy - py * outerHalf), P(cx + dx * L, cy + dy * L), d.ExhaustColor);
                pb.Tri(P(cx + px * innerHalf, cy + py * innerHalf), P(cx - px * innerHalf, cy - py * innerHalf), P(cx + dx * L * 0.55f, cy + dy * L * 0.55f), core);
            }
            float y = 0;
            for (int i = to - 1; i >= from; i--)
            {
                var p = v.Parts[i];
                var d = p.Def;
                float w = (float)d.Width, h = (float)d.Height;
                Color dark = PlanetRenderer.Darken(d.Tint, 0.40f);
                Color light = PlanetRenderer.Lighten(d.Tint, 0.12f);

                pickHits?.Add((p, new[] { P(-w / 2, y + h), P(w / 2, y + h), P(w / 2, y), P(-w / 2, y) }));

                var pt = tex?.Part(d.Id);
                if (pt != null)
                {
                    // textured silhouette over the part footprint (image top = nose side)
                    pb.TexturedQuad(pt, P(-w / 2, y + h), P(w / 2, y + h), P(w / 2, y), P(-w / 2, y), Color.White);
                }
                else switch (d.Kind)
                {
                    case PartKind.Pod:
                        pb.Quad(P(-w / 2, y), P(-w * 0.18f, y + h), P(w * 0.18f, y + h), P(w / 2, y),
                                dark, light, light, dark);
                        pb.FillCircle(P(0, y + h * 0.45f), w * 0.15f * pxPerM, new Color(40, 60, 100));
                        break;

                    case PartKind.Engine:
                        // mount
                        pb.Quad(P(-w * 0.28f, y + h * 0.45f), P(-w * 0.28f, y + h), P(w * 0.28f, y + h), P(w * 0.28f, y + h * 0.45f), d.Tint);
                        // nozzle (bell widens downward)
                        pb.Quad(P(-w / 2, y), P(-w * 0.18f, y + h * 0.45f), P(w * 0.18f, y + h * 0.45f), P(w / 2, y),
                                dark, light, light, dark);
                        break;

                    case PartKind.Decoupler:
                    case PartKind.RadialDecoupler:
                        pb.Quad(P(-w / 2, y), P(-w / 2, y + h), P(w / 2, y + h), P(w / 2, y), d.Tint);
                        break;

                    case PartKind.Fins:
                        pb.Quad(P(-w * 0.18f, y), P(-w * 0.18f, y + h), P(w * 0.18f, y + h), P(w * 0.18f, y),
                                dark, dark, light, light);
                        pb.Tri(P(-w * 0.18f, y + h), P(-w / 2, y), P(-w * 0.18f, y), d.Tint);
                        pb.Tri(P(w * 0.18f, y + h), P(w / 2, y), P(w * 0.18f, y), d.Tint);
                        break;

                    case PartKind.Aero: // nose cone
                        pb.Tri(P(-w / 2, y), P(w / 2, y), P(0, y + h), dark, dark, light);
                        break;

                    case PartKind.SolarShield: // stowed: a thin folded disc across the part
                        pb.Quad(P(-w * 0.5f, y + h * 0.30f), P(-w * 0.5f, y + h * 0.70f),
                                P(w * 0.5f, y + h * 0.70f), P(w * 0.5f, y + h * 0.30f), dark, dark, light, light);
                        break;

                    case PartKind.Parachute:
                        pb.Quad(P(-w / 2, y), P(-w * 0.3f, y + h), P(w * 0.3f, y + h), P(w / 2, y),
                                dark, d.Tint, d.Tint, dark);
                        break;

                    case PartKind.SolidBooster:
                        pb.Tri(P(0, y + h), P(-w / 2, y + h * 0.9f), P(w / 2, y + h * 0.9f), light);
                        pb.Quad(P(-w / 2, y), P(-w / 2, y + h * 0.9f), P(w / 2, y + h * 0.9f), P(w / 2, y),
                                dark, dark, light, light);
                        break;

                    case PartKind.DockingPort:
                        // flat collar with a recessed ring face, so it reads as a mating port end-on
                        pb.Quad(P(-w / 2, y), P(-w / 2, y + h), P(w / 2, y + h), P(w / 2, y), dark, light, light, dark);
                        pb.Quad(P(-w * 0.34f, y + h * 0.3f), P(-w * 0.34f, y + h * 0.7f), P(w * 0.34f, y + h * 0.7f), P(w * 0.34f, y + h * 0.3f),
                                d.Tint, d.Tint, d.Tint, d.Tint);
                        break;

                    case PartKind.LandingGear:
                        // landing gear is always radial; the axial case is unreachable but kept for safety.
                        // When not deployed, draw nothing — gear is completely hidden.
                        break;

                    default: // Tank
                        pb.Quad(P(-w / 2, y), P(-w / 2, y + h), P(w / 2, y + h), P(w / 2, y),
                                dark, dark, light, light);
                        break;
                }

                // dynamic add-ons that must render whether or not a texture is present
                if (d.Kind == PartKind.Engine && flaming && p.Ignited)
                {
                    float flick = 1f + 0.12f * (float)Math.Sin(anim * 37 + i * 2.1);
                    float flameLen = h * (0.9f + 2.0f * (float)v.Throttle) * flick;
                    DrawPlume(0, y, w * 0.30f, flameLen, (float)v.Throttle, d, flick);
                }
                // axial solid booster: full-thrust plume while ignited and fuelled (throttle-independent),
                // mirroring the radial-solid flame; the engine block above handles liquid engines.
                if (d.Kind == PartKind.SolidBooster && flaming && p.Ignited && p.Fuel > 0)
                {
                    float flick = 1f + 0.12f * (float)Math.Sin(anim * 37 + i * 2.1);
                    float flameLen = h * (0.9f + 2.0f * 1f) * flick;   // solids run at full
                    DrawPlume(0, y, w * 0.30f, flameLen, 1f, d, flick);
                }
                if (d.Kind == PartKind.Parachute && p.Deployed)
                {
                    var open = tex?.Part(d.Id + "-open");
                    if (open != null) DrawOpenChute(open, P(0, y + h), d);
                    else
                    {
                        float wM = d.ParachuteWidth > 0 ? (float)d.ParachuteWidth : OpenChuteWidthM;
                        float hM = d.ParachuteHeight > 0 ? (float)d.ParachuteHeight : wM;
                        var top = P(0, y + h);
                        var canopy = top + vertS * (hM * pxPerM);
                        var lTop = top + vertS * (hM * 0.85f * pxPerM) - vperpS * (wM * 0.4f * pxPerM);
                        var rTop = top + vertS * (hM * 0.85f * pxPerM) + vperpS * (wM * 0.4f * pxPerM);
                        pb.Line(P(-w * 0.4f, y + h), lTop, Math.Max(1f, 0.06f * pxPerM), new Color(180, 180, 180, 180));
                        pb.Line(P(w * 0.4f, y + h), rTop, Math.Max(1f, 0.06f * pxPerM), new Color(180, 180, 180, 180));
                        pb.FillCircle(canopy, wM * 0.5f * pxPerM, new Color(235, 130, 60), PlanetRenderer.Darken(new Color(235, 130, 60), 0.3f));
                    }
                }
                // solar shield deployed: a wide reflective plate fanned out toward the nose (the sunward
                // side), spanning well past the hull so it visibly shadows the parts below it.
                if (d.Kind == PartKind.SolarShield && p.Deployed)
                {
                    float halfW = Math.Max(w, 1.0f) * 1.7f;          // metres, half the plate's span
                    float lift = 0.30f, thick = 0.40f;               // metres toward the nose / half the plate's rendered height
                    var open = tex?.Part(d.Id + "-open");
                    var c = P(0, y + h) + vertS * (lift * pxPerM);
                    var a = c - vperpS * (halfW * pxPerM) + vertS * (thick * pxPerM);
                    var b = c + vperpS * (halfW * pxPerM) + vertS * (thick * pxPerM);
                    var bb = c + vperpS * (halfW * pxPerM) - vertS * (thick * pxPerM);
                    var aa = c - vperpS * (halfW * pxPerM) - vertS * (thick * pxPerM);
                    // struts from the hull edges to the plate
                    pb.Line(P(-w * 0.45f, y + h), c - vperpS * (halfW * 0.5f * pxPerM), Math.Max(1f, 0.05f * pxPerM), new Color(170, 170, 175, 180));
                    pb.Line(P(w * 0.45f, y + h), c + vperpS * (halfW * 0.5f * pxPerM), Math.Max(1f, 0.05f * pxPerM), new Color(170, 170, 175, 180));
                    if (open != null) pb.TexturedQuad(open, a, b, bb, aa, Color.White);
                    else
                    {
                        Color face = new Color(230, 224, 180), edge = PlanetRenderer.Darken(face, 0.4f);
                        pb.Quad(a, b, bb, aa, face, face, edge, edge);
                    }
                }
                // landing gear deploy: strut+pad extending downward from the bottom-third of the axial part
                // (unreachable in practice since landing gear is always radial; kept for completeness).
                // Skip the procedural placeholder when a texture already depicts the deployed gear.
                if (d.Kind == PartKind.LandingGear && p.Deployed && pt == null)
                {
                    float gx = 0, gy = y + h * 0.33f;
                    float strutLen = h * 0.8f, padW = w * 0.65f, padH = h * 0.15f;
                    Color strutC = new Color(180, 185, 195);
                    pb.Line(P(gx - w * 0.12f, gy), P(gx - w * 0.35f, gy - strutLen), Math.Max(1.2f, 1.8f * pxPerM), strutC);
                    pb.Line(P(gx + w * 0.12f, gy), P(gx + w * 0.35f, gy - strutLen), Math.Max(1.2f, 1.8f * pxPerM), strutC);
                    pb.Quad(P(gx - padW / 2, gy - strutLen - padH), P(gx + padW / 2, gy - strutLen - padH),
                            P(gx + padW / 2, gy - strutLen), P(gx - padW / 2, gy - strutLen),
                            d.Tint, d.Tint, dark, dark);
                }

                // RCS thruster sprays: a part carrying an RCS block puffs a pair of jets in the
                // jet direction (opposite the commanded translation), length pulsing with the command.
                if (rcsFiring && rcsMag > 1e-6f)
                {
                    bool hasRcs = false;
                    foreach (var mod in p.Modules) if (mod.Def.Kind == ModuleKind.RCS) { hasRcs = true; break; }
                    if (hasRcs)
                    {
                        float flick = 1f + 0.25f * (float)Math.Sin(anim * 41 + i * 1.7);
                        float plen = (0.35f + 0.6f * rcsMag) * Math.Max(h, w) * 0.5f * flick;
                        float perpx = -jy, perpy = jx;                  // across the jet axis
                        float side = Math.Max(w, 0.6f) * 0.45f;         // each thruster offset from center
                        float bw = 0.10f * Math.Max(w, 0.6f);
                        float cy = y + h * 0.5f;
                        for (int sgn = -1; sgn <= 1; sgn += 2)
                        {
                            float ox = perpx * side * sgn, oy = perpy * side * sgn;
                            var tip = P(ox + jx * plen, cy + oy + jy * plen);
                            pb.Tri(P(ox + perpx * bw, cy + oy + perpy * bw),
                                   P(ox - perpx * bw, cy + oy - perpy * bw), tip, new Color(150, 220, 255, 170));
                            pb.Tri(P(ox + perpx * bw * 0.5f, cy + oy + perpy * bw * 0.5f),
                                   P(ox - perpx * bw * 0.5f, cy + oy - perpy * bw * 0.5f),
                                   P(ox + jx * plen * 0.6f, cy + oy + jy * plen * 0.6f), new Color(235, 250, 255, 220));
                        }
                    }
                }

                // slot modules visible on the hull when deployed/active: drawn from the module's icon
                // texture where one exists, falling back to the simple procedural shapes otherwise
                if (!v.IsDebris && p.Modules.Count > 0)
                {
                    // a centered textured box in local coords (lx,ly), size bw x bh meters
                    void IconBox(Microsoft.Xna.Framework.Graphics.Texture2D t, float lx, float ly, float bw, float bh)
                        => pb.TexturedQuad(t, P(lx - bw / 2, ly + bh / 2), P(lx + bw / 2, ly + bh / 2),
                                              P(lx + bw / 2, ly - bh / 2), P(lx - bw / 2, ly - bh / 2), Color.White);

                    foreach (var mod in p.Modules)
                    {
                        if (!mod.Active) continue;
                        var mtex = tex?.Module(mod.Def.Id);
                        switch (mod.Def.Kind)
                        {
                            case ModuleKind.SolarPanel:
                            {
                                const float SolarLen = 1.2f, SolarHalf = 0.4f;
                                float my = y + h * 0.5f;
                                if (mtex != null)
                                {
                                    // two wings; each quad lists the hull-side vertex first, so the art's
                                    // attachment edge (texture U=0) lands at the hull and the panel extends
                                    // outward on both sides without a flip
                                    pb.TexturedQuad(mtex, P(-w / 2, my + SolarHalf), P(-w / 2 - SolarLen, my + SolarHalf), P(-w / 2 - SolarLen, my - SolarHalf), P(-w / 2, my - SolarHalf), Color.White, false);
                                    pb.TexturedQuad(mtex, P(w / 2, my + SolarHalf), P(w / 2 + SolarLen, my + SolarHalf), P(w / 2 + SolarLen, my - SolarHalf), P(w / 2, my - SolarHalf), Color.White, false);
                                }
                                else
                                {
                                    Color pc = new Color(70, 130, 235), pcd = PlanetRenderer.Darken(pc, 0.4f);
                                    pb.Quad(P(-w / 2, my - SolarHalf), P(-w / 2, my + SolarHalf), P(-w / 2 - SolarLen, my + SolarHalf), P(-w / 2 - SolarLen, my - SolarHalf), pc, pc, pcd, pcd);
                                    pb.Quad(P(w / 2, my - SolarHalf), P(w / 2, my + SolarHalf), P(w / 2 + SolarLen, my + SolarHalf), P(w / 2 + SolarLen, my - SolarHalf), pc, pc, pcd, pcd);
                                }
                                break;
                            }
                            case ModuleKind.Harvester:
                                if (mtex != null) IconBox(mtex, -w / 2, y + h * 0.25f, w * 0.5f, h * 0.4f);
                                else pb.FillCircle(P(-w / 2, y + h * 0.25f), w * 0.16f * pxPerM, new Color(255, 180, 80));
                                break;
                            case ModuleKind.IsruConverter:
                                if (mtex != null) IconBox(mtex, w / 2, y + h * 0.25f, w * 0.5f, h * 0.4f);
                                else pb.FillCircle(P(w / 2, y + h * 0.25f), w * 0.16f * pxPerM, new Color(255, 180, 80));
                                break;
                            case ModuleKind.OreScanner:
                                if (mtex != null) IconBox(mtex, w / 2, y + h * 0.75f, w * 0.5f, h * 0.4f);
                                else pb.FillCircle(P(w / 2, y + h * 0.75f), w * 0.16f * pxPerM, new Color(255, 180, 80));
                                break;
                            case ModuleKind.Light:
                            {
                                var lc = P(0, y + h * 0.5f);
                                pb.FillCircle(lc, Math.Max(2f, w * 0.6f * pxPerM), new Color(255, 245, 200, 70));   // soft glow
                                if (mtex != null) IconBox(mtex, 0, y + h * 0.5f, w * 0.45f, h * 0.45f);
                                else pb.FillCircle(lc, Math.Max(1.5f, w * 0.12f * pxPerM), new Color(255, 250, 220));
                                break;
                            }
                        }
                    }
                }

                // radial-mounted parts beside the hull: each mount is a vertical sub-stack mirrored on both
                // sides, columns stacking outward. Geometry comes from the design round-trip tags
                // (RadialMountId/Side/Slot); untagged parts from old saves fall back to interleaved pairs.
                var mounts = new System.Collections.Generic.SortedDictionary<int, (float w, System.Collections.Generic.List<(int slot, float h)> slots)>();
                foreach (var r in p.Radials)
                {
                    if (r.RadialMountId < 0) continue;
                    if (!mounts.TryGetValue(r.RadialMountId, out var m)) { m = (0f, new System.Collections.Generic.List<(int, float)>()); mounts[r.RadialMountId] = m; }
                    m.w = Math.Max(m.w, (float)r.Def.Width);
                    // record each sub-stack slot once; a mirrored pair has both sides, a single-sided mount
                    // (lateral thruster) only one, so key off the slot rather than assuming side 0 is present
                    bool seen = false;
                    foreach (var sl in m.slots) if (sl.slot == r.RadialSlot) { seen = true; break; }
                    if (!seen) m.slots.Add((r.RadialSlot, (float)r.Def.Height));
                    mounts[r.RadialMountId] = m;
                }
                var mountOffset = new System.Collections.Generic.Dictionary<int, float>();
                float runX = w / 2f + 0.15f;
                foreach (var kv in mounts) { mountOffset[kv.Key] = runX + kv.Value.w / 2f; runX += kv.Value.w + 0.1f; }

                for (int k = 0; k < p.Radials.Count; k++)
                {
                    var r = p.Radials[k];
                    var rd = r.Def;
                    float rw = (float)rd.Width, rh = (float)rd.Height;
                    float xc, yb;
                    if (r.RadialMountId >= 0 && mounts.TryGetValue(r.RadialMountId, out var m2))
                    {
                        float colH = 0, yOff = 0;
                        foreach (var s in m2.slots) { colH += s.h; if (s.slot < r.RadialSlot) yOff += s.h; }
                        float sign = (r.RadialSide == 0) ? 1f : -1f;
                        xc = sign * mountOffset[r.RadialMountId];
                        // flight local-Y increases toward the nose, so invert the slot offset to keep
                        // sub-stack slot 0 at the nose side (matching the editor's top-down sub-stack);
                        // landing gear attaches at the bottom third of the host part
                        yb = rd.Kind == PartKind.LandingGear
                            ? y + h * 0.33f - rh
                            : y + (h - colH) * 0.5f + (colH - yOff - rh);
                    }
                    else
                    {
                        float sign = (k % 2 == 0) ? 1f : -1f;   // legacy untagged fallback
                        int slot = k / 2;
                        xc = sign * (w / 2f + rw / 2f + 0.15f + slot * (rw + 0.1f));
                        // landing gear attaches at the bottom third rather than centered
                        yb = rd.Kind == PartKind.LandingGear ? y + h * 0.33f - rh : y + (h - rh) * 0.5f;
                    }
                    Color rdark = PlanetRenderer.Darken(rd.Tint, 0.40f), rlight = PlanetRenderer.Lighten(rd.Tint, 0.12f);

                    pickHits?.Add((r, new[] { P(xc - rw / 2, yb + rh), P(xc + rw / 2, yb + rh), P(xc + rw / 2, yb), P(xc - rw / 2, yb) }));

                    var rt = tex?.Part(rd.Id);
                    if (rt != null)
                    {
                        // textures are authored right-oriented; mirror the left-side (RadialSide 1) copy
                        // landing gear only shows its textured body when deployed
                        if (rd.Kind != PartKind.LandingGear || r.Deployed)
                            pb.TexturedQuad(rt, P(xc - rw / 2, yb + rh), P(xc + rw / 2, yb + rh), P(xc + rw / 2, yb), P(xc - rw / 2, yb), Color.White, r.RadialSide != 0);
                    }
                    else if (rd.Kind == PartKind.SolidBooster)
                    {
                        pb.Tri(P(xc, yb + rh), P(xc - rw / 2, yb + rh * 0.9f), P(xc + rw / 2, yb + rh * 0.9f), rlight);
                        pb.Quad(P(xc - rw / 2, yb), P(xc - rw / 2, yb + rh * 0.9f), P(xc + rw / 2, yb + rh * 0.9f), P(xc + rw / 2, yb), rdark, rdark, rlight, rlight);
                    }
                    else if (rd.Kind != PartKind.LandingGear || r.Deployed)
                    {
                        // hide the gear body when retracted
                        pb.Quad(P(xc - rw / 2, yb), P(xc - rw / 2, yb + rh), P(xc + rw / 2, yb + rh), P(xc + rw / 2, yb), rdark, rdark, rlight, rlight);
                    }

                    // a radial liquid engine carries no fuel of its own (it burns the radial tank's
                    // cross-fed pool), so gate its flame on vessel thrust like the axial engines above;
                    // solids burn self-contained fuel, so keep their own-fuel check.
                    bool rOffAxis = rd.Kind == PartKind.Engine && Math.Abs(rd.ThrustAngle) > 1e-6;
                    bool rFlame = r.Ignited && !rOffAxis &&
                        (rd.Kind == PartKind.SolidBooster ? r.Fuel > 0
                         : rd.Kind == PartKind.Engine ? (flaming && v.Throttle > 0)
                         : false);
                    if (rFlame)
                    {
                        float flick = 1f + 0.12f * (float)Math.Sin(anim * 37 + (i + k) * 2.1);
                        float rThr = rd.Kind == PartKind.SolidBooster ? 1f : (float)v.Throttle;
                        float flen = rh * (0.9f + 2.0f * rThr) * flick;
                        DrawPlume(xc, yb, rw * 0.30f, flen, rThr, rd, flick);
                    }
                    // off-axis (radial) engine firing on the Q/E command: plume exits opposite its thrust
                    // direction. Local thrust dir for angle t is (sin t, cos t); command sign flips it.
                    // single-sided thruster only fires (and plumes) when the command matches the side it
                    // pushes away from (side 1 = left pushes +Right on latCmd>0; side 0 = right on latCmd<0)
                    bool rFires = r.RadialSide < 0 || Math.Sign(latCmd) == (r.RadialSide == 1 ? 1 : -1);
                    if (rOffAxis && r.Ignited && radialFlaming && Math.Abs(latCmd) > 1e-6f && rFires)
                    {
                        float t = (float)(rd.ThrustAngle * Math.PI / 180.0);
                        float s = Math.Sign(latCmd);
                        float ex = -(float)Math.Sin(t) * s, ey = -(float)Math.Cos(t) * s;   // exhaust = -thrust
                        float flick = 1f + 0.12f * (float)Math.Sin(anim * 37 + (i + k) * 2.1);
                        float mag = Math.Abs(latCmd);
                        float flen = Math.Max(rw, rh) * (0.9f + 2.0f * mag) * flick;
                        DrawDirPlume(xc, yb + rh * 0.5f, ex, ey, rw * 0.30f, flen, rd);
                    }

                    // deployed radial parachute: canopy above the radial part (mirrors the axial chute)
                    if (rd.Kind == PartKind.Parachute && r.Deployed)
                    {
                        var open = tex?.Part(rd.Id + "-open");
                        if (open != null) DrawOpenChute(open, P(xc, yb + rh), rd);
                        else
                        {
                            float wM = rd.ParachuteWidth > 0 ? (float)rd.ParachuteWidth : OpenChuteWidthM;
                            float hM = rd.ParachuteHeight > 0 ? (float)rd.ParachuteHeight : wM;
                            var top = P(xc, yb + rh);
                            var canopy = top + vertS * (hM * pxPerM);
                            var lTop = top + vertS * (hM * 0.85f * pxPerM) - vperpS * (wM * 0.4f * pxPerM);
                            var rTop = top + vertS * (hM * 0.85f * pxPerM) + vperpS * (wM * 0.4f * pxPerM);
                            pb.Line(P(xc - rw * 0.4f, yb + rh), lTop, Math.Max(1f, 0.06f * pxPerM), new Color(180, 180, 180, 180));
                            pb.Line(P(xc + rw * 0.4f, yb + rh), rTop, Math.Max(1f, 0.06f * pxPerM), new Color(180, 180, 180, 180));
                            pb.FillCircle(canopy, wM * 0.5f * pxPerM, new Color(235, 130, 60), PlanetRenderer.Darken(new Color(235, 130, 60), 0.3f));
                        }
                    }
                    // deployed radial solar shield: a reflective plate that unfolds PARALLEL to the host part
                    // (long along the part's length), projecting outboard just past its outer edge
                    if (rd.Kind == PartKind.SolarShield && r.Deployed)
                    {
                        float sign = xc >= 0 ? 1f : -1f;               // outboard direction (away from the hull)
                        float gap = 0.25f;                             // clearance past the part's outer edge
                        float halfLen = Math.Max(rh, 1.0f) * 0.9f;     // extent along the part (local Y)
                        float wide = Math.Max(rw, 0.9f) * 1.4f;        // outboard projection (local X)
                        float xIn = xc + sign * (rw * 0.5f + gap);
                        float xOut = xIn + sign * wide;
                        float yMid = yb + rh * 0.5f, yLo = yMid - halfLen, yHi = yMid + halfLen;
                        var open = tex?.Part(rd.Id + "-open");
                        var a = P(xIn, yHi); var b = P(xOut, yHi); var bb = P(xOut, yLo); var aa = P(xIn, yLo);
                        // struts from the part's outer edge to the inboard side of the plate
                        pb.Line(P(xc + sign * rw * 0.45f, yMid - rh * 0.3f), P(xIn, yMid - rh * 0.3f), Math.Max(1f, 0.05f * pxPerM), new Color(170, 170, 175, 180));
                        pb.Line(P(xc + sign * rw * 0.45f, yMid + rh * 0.3f), P(xIn, yMid + rh * 0.3f), Math.Max(1f, 0.05f * pxPerM), new Color(170, 170, 175, 180));
                        // no flipX: the sign-based corner geometry already mirrors the plate for the
                        // left-side mount (U=0 at the inboard edge, U=1 outboard on both sides), so the
                        // texture flag would double-flip it.
                        if (open != null) pb.TexturedQuad(open, a, b, bb, aa, Color.White, false);
                        else
                        {
                            Color face = new Color(230, 224, 180), edge = PlanetRenderer.Darken(face, 0.4f);
                            pb.Quad(a, b, bb, aa, face, face, edge, edge);
                        }
                    }
                    // deployed radial landing gear: when no texture is authored, draw a procedural
                    // strut + foot pad. The texture (when present) already depicts the deployed gear,
                    // so skip the placeholder strut/pad over it.
                    if (rd.Kind == PartKind.LandingGear && r.Deployed && rt == null)
                    {
                        float gearTop = yb + rh;
                        float strutLen = rh * 0.85f, padW = rw * 0.7f, padH = rh * 0.16f;
                        Color strutC = new Color(180, 185, 195);
                        pb.Line(P(xc - rw * 0.14f, gearTop), P(xc - rw * 0.38f, gearTop - strutLen), Math.Max(1.2f, 1.8f * pxPerM), strutC);
                        pb.Line(P(xc + rw * 0.14f, gearTop), P(xc + rw * 0.38f, gearTop - strutLen), Math.Max(1.2f, 1.8f * pxPerM), strutC);
                        pb.Quad(P(xc - padW / 2, gearTop - strutLen - padH), P(xc + padW / 2, gearTop - strutLen - padH),
                                P(xc + padW / 2, gearTop - strutLen), P(xc - padW / 2, gearTop - strutLen),
                                rd.Tint, rd.Tint, rdark, rdark);
                    }
                }
                y += h;
            }
            }

            foreach (var (start, end, link) in v.SubStacks())
                DrawStack(start, end, link?.QuarterTurns ?? 0, link?.Offset ?? Vec2d.Zero);
        }
    }
}