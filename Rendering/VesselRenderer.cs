using System;
using Microsoft.Xna.Framework;
using Solar.Core;
using Solar.Parts;
using Solar.Vessels;

namespace Solar.Rendering
{
    /// <summary>Draws a vessel's part stack as procedural shapes, or a triangle icon when tiny/in map view.</summary>
    public static class VesselRenderer
    {
        public static void Draw(PrimitiveBatch pb, Camera2D cam, Vessel v, double ut, double anim, bool forceIcon = false, TextureStore tex = null)
        {
            Vec2d basePos = v.AbsolutePosition(ut);
            Vec2d sD = cam.WorldToScreenD(basePos);
            if (!cam.OnScreen(sD, 3000)) return;

            var baseS = new Vector2((float)sD.X, (float)sD.Y);
            float pxPerM = (float)(1.0 / cam.MetersPerPixel);
            float hPx = (float)Math.Max(v.TotalHeight, 2) * pxPerM;

            var upS = new Vector2((float)Math.Cos(v.Heading), -(float)Math.Sin(v.Heading));
            var rightS = new Vector2(-upS.Y, upS.X);

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
            float y = 0;
            for (int i = to - 1; i >= from; i--)
            {
                var p = v.Parts[i];
                var d = p.Def;
                float w = (float)d.Width, h = (float)d.Height;
                Color dark = PlanetRenderer.Darken(d.Tint, 0.40f);
                Color light = PlanetRenderer.Lighten(d.Tint, 0.12f);

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
                    pb.Tri(P(-w * 0.30f, y + 0.05f), P(w * 0.30f, y + 0.05f), P(0, y - flameLen), new Color(255, 140, 40, 200));
                    pb.Tri(P(-w * 0.16f, y + 0.05f), P(w * 0.16f, y + 0.05f), P(0, y - flameLen * 0.55f), new Color(255, 230, 120, 230));
                }
                if (d.Kind == PartKind.Parachute && p.Deployed)
                {
                    var canopy = P(0, y + h + 7f);
                    pb.Line(P(-w * 0.4f, y + h), P(-4.4f, y + h + 6f), Math.Max(1f, 0.06f * pxPerM), new Color(180, 180, 180, 180));
                    pb.Line(P(w * 0.4f, y + h), P(4.4f, y + h + 6f), Math.Max(1f, 0.06f * pxPerM), new Color(180, 180, 180, 180));
                    pb.FillCircle(canopy, 5.5f * pxPerM, new Color(235, 130, 60), PlanetRenderer.Darken(new Color(235, 130, 60), 0.3f));
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
                                float my = y + h * 0.5f, pl = w * 1.3f, ph2 = Math.Max(0.5f, h * 0.35f);
                                if (mtex != null)
                                {
                                    // two wings; the left one mirrored so the right-oriented art reads correctly
                                    pb.TexturedQuad(mtex, P(-w / 2, my + ph2), P(-w / 2 - pl, my + ph2), P(-w / 2 - pl, my - ph2), P(-w / 2, my - ph2), Color.White, true);
                                    pb.TexturedQuad(mtex, P(w / 2, my + ph2), P(w / 2 + pl, my + ph2), P(w / 2 + pl, my - ph2), P(w / 2, my - ph2), Color.White, false);
                                }
                                else
                                {
                                    Color pc = new Color(70, 130, 235), pcd = PlanetRenderer.Darken(pc, 0.4f);
                                    pb.Quad(P(-w / 2, my - ph2), P(-w / 2, my + ph2), P(-w / 2 - pl, my + ph2), P(-w / 2 - pl, my - ph2), pc, pc, pcd, pcd);
                                    pb.Quad(P(w / 2, my - ph2), P(w / 2, my + ph2), P(w / 2 + pl, my + ph2), P(w / 2 + pl, my - ph2), pc, pc, pcd, pcd);
                                }
                                break;
                            }
                            case ModuleKind.Harvester:
                                if (mtex != null) IconBox(mtex, 0, y + h * 0.5f, w * 0.5f, h * 0.5f);
                                else pb.FillCircle(P(0, y + h * 0.5f), w * 0.16f * pxPerM, new Color(255, 180, 80));
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
                    if (r.RadialSide == 0) m.slots.Add((r.RadialSlot, (float)r.Def.Height));
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
                    bool rFlame = r.Ignited &&
                        (rd.Kind == PartKind.SolidBooster ? r.Fuel > 0
                         : rd.Kind == PartKind.Engine ? (flaming && v.Throttle > 0)
                         : false);
                    if (rFlame)
                    {
                        float flick = 1f + 0.12f * (float)Math.Sin(anim * 37 + (i + k) * 2.1);
                        float flen = rh * (0.9f + 2.0f * (rd.Kind == PartKind.SolidBooster ? 1f : (float)v.Throttle)) * flick;
                        pb.Tri(P(xc - rw * 0.30f, yb + 0.05f), P(xc + rw * 0.30f, yb + 0.05f), P(xc, yb - flen), new Color(255, 140, 40, 200));
                        pb.Tri(P(xc - rw * 0.16f, yb + 0.05f), P(xc + rw * 0.16f, yb + 0.05f), P(xc, yb - flen * 0.55f), new Color(255, 230, 120, 230));
                    }

                    // deployed radial parachute: canopy above the radial part (mirrors the axial chute)
                    if (rd.Kind == PartKind.Parachute && r.Deployed)
                    {
                        var canopy = P(xc, yb + rh + 7f);
                        pb.Line(P(xc - rw * 0.4f, yb + rh), P(xc - 4.4f, yb + rh + 6f), Math.Max(1f, 0.06f * pxPerM), new Color(180, 180, 180, 180));
                        pb.Line(P(xc + rw * 0.4f, yb + rh), P(xc + 4.4f, yb + rh + 6f), Math.Max(1f, 0.06f * pxPerM), new Color(180, 180, 180, 180));
                        pb.FillCircle(canopy, 5.5f * pxPerM, new Color(235, 130, 60), PlanetRenderer.Darken(new Color(235, 130, 60), 0.3f));
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