using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Solar.Core;
using Solar.Parts;
using Solar.UI;
using Solar.Vessels;

namespace Solar.Scenes
{
    /// <summary>VAB-style rocket editor: palette on the left, stack in the middle, stats on the right.</summary>
    public sealed class EditorScene : Scene
    {
        private const int PaletteW = 270;
        private const int StatsW = 300;

        private PartDef _held;        // part type "in hand", to be inserted
        private int _selected = -1;   // selected stack index
        private int _hoverGap = -1;
        private bool _nameFocus;      // the ship-name text field has keyboard focus
        private bool _showRnd;        // R&D tech-tree overlay open
        private bool _showModulePicker;  // "Add Module" overlay open
        private int _pickerTarget = -1;  // stack index the picker fits modules to
        private float _pickerScroll;     // module-picker scroll offset (px)
        private bool _showCrewPicker;    // "Assign Crew" overlay open
        private int _crewTarget = -1;    // stack index the crew picker assigns to
        private bool _showLoadShip;      // "Load Ship" design-library overlay open
        private float _loadShipScroll;   // load-ship list scroll offset (px)

        // palette search / categories / scroll
        private string _searchText = "";
        private bool _searchFocus;
        private float _paletteScroll;
        private bool _paletteDrag;
        private readonly HashSet<string> _collapsed = new();
        // right ROCKET STATS panel scroll (whole body, below the fixed name field)
        private float _statsScroll;
        private bool _statsDrag;
        private float _statsContentH;    // last frame's measured body height, used to clamp
        // build-area view: zoom multiplies the auto-fit scale, panY shifts the centered stack
        private float _zoom = 1f;
        private float _panY = 0f;
        private bool _stackPan;          // middle-button vertical drag in progress
        private float _lastMouseY;
        // hover tooltip (deferred so it paints on top of every panel)
        private PartDef _tooltipDef;
        private StackEntry _tooltipEntry;   // set when hovering a placed part, so the tooltip shows its fitted modules
        private Vector2 _tooltipAt;
        private ModuleDef _modTipDef;       // set when hovering a fitted-slot module row, so its tooltip paints on top
        private Vector2 _modTipAt;
        // screen rects of radial sub-stack parts, rebuilt each frame by DrawRadials for hit-testing
        private readonly List<(int stackIndex, int mountIndex, int partIndex, Rectangle rect)> _radialHits = new();

        /// <summary>Palette categories in display order: a label and the kinds it gathers.</summary>
        private static readonly (string Name, PartKind[] Kinds)[] Categories =
        {
            ("Command",        new[] { PartKind.Pod }),
            ("Fuel Tanks",     new[] { PartKind.Tank }),
            ("Engines",        new[] { PartKind.Engine, PartKind.SolidBooster }),
            ("Structural",     new[] { PartKind.Decoupler, PartKind.RadialDecoupler, PartKind.StructuralBay }),
            ("Aero",           new[] { PartKind.Fins, PartKind.Parachute, PartKind.Aero }),
            ("Docking & Gear", new[] { PartKind.DockingPort, PartKind.LandingGear }),
        };

        /// <summary>Module picker categories in display order.</summary>
        private static readonly (string Name, ModuleKind[] Kinds)[] ModuleCategories =
        {
            ("Power",         new[] { ModuleKind.SolarPanel, ModuleKind.Rtg, ModuleKind.Battery, ModuleKind.FuelCell }),
            ("Life Support",  new[] { ModuleKind.LifeSupport }),
            ("Science",       new[] { ModuleKind.Science }),
            ("Comms",         new[] { ModuleKind.Antenna }),
            ("Resource",      new[] { ModuleKind.Harvester, ModuleKind.Tank }),
            ("Control",       new[] { ModuleKind.ReactionWheel, ModuleKind.RCS }),
            ("Storage",       new[] { ModuleKind.Storage }),
            ("Utility",       new[] { ModuleKind.LandingLeg, ModuleKind.Light }),
        };

        public EditorScene(GameContext ctx) : base(ctx) { }

        private List<StackEntry> Stack => Ctx.Design.Stack;

        /// <summary>Remove a part and everything below it (toward the engines): the nose is the
        /// root, so lower parts hang off the one being removed and go with it.</summary>
        private void RemoveCascade(int index)
        {
            if (index < 0 || index >= Stack.Count) return;
            Stack.RemoveRange(index, Stack.Count - index);
            _selected = -1;
        }

        public override void Update(double dt)
        {
            var inp = Ctx.Input;
            if (!_nameFocus && !_searchFocus && !_showModulePicker && inp.Pressed(Keys.R)) _showRnd = !_showRnd;
            // close the module picker if its target part is gone or no longer slot-bearing
            if (_showModulePicker && (_pickerTarget < 0 || _pickerTarget >= Stack.Count || Stack[_pickerTarget].Def.Slots <= 0))
                _showModulePicker = false;
            // close the crew picker if its target part is gone or no longer has seats
            if (_showCrewPicker && (_crewTarget < 0 || _crewTarget >= Stack.Count || Stack[_crewTarget].SeatCount <= 0))
                _showCrewPicker = false;
            if (inp.Pressed(Keys.Escape))
            {
                if (_showLoadShip) _showLoadShip = false;
                else if (_showCrewPicker) _showCrewPicker = false;
                else if (_showModulePicker) _showModulePicker = false;
                else if (_showRnd) _showRnd = false;
                else if (_searchFocus) _searchFocus = false;
                else if (_nameFocus) _nameFocus = false;
                else if (_held != null) _held = null;
                else { Ctx.Scenes.SwitchTo(new HubScene(Ctx)); return; }
            }
            // Enter launches, unless the user is typing a name (then it confirms the field).
            if (inp.Pressed(Keys.Enter) && !_nameFocus && !_searchFocus && Ctx.Design.Validate() == null)
            {
                Ctx.Scenes.SwitchTo(new FlightScene(Ctx));
                return;
            }
            if (inp.Pressed(Keys.Enter) && (_nameFocus || _searchFocus)) { _nameFocus = false; _searchFocus = false; }
            if (!_nameFocus && !_searchFocus && inp.Pressed(Keys.Delete) && _selected >= 0 && _selected < Stack.Count)
                RemoveCascade(_selected);
            if (inp.RightClick && _held != null) _held = null;   // right-click only drops the held part
        }

        public override void Draw()
        {
            var pb = Ctx.Pb; var sb = Ctx.Sb; var f = Ctx.Font; var inp = Ctx.Input;
            int w = Ctx.W, h = Ctx.H;
            pb.Begin();
            sb.Begin();

            Ctx.Stars.Draw(pb, w, h, Vec2d.Zero);
            sb.DrawString(Ctx.FontBig, "VEHICLE ASSEMBLY", new Vector2(PaletteW + 24, 14), new Color(200, 215, 235));

            sb.DrawString(f, $"Science: {Ctx.State.Science:0}", new Vector2(PaletteW + 24, 44), new Color(150, 230, 150));
            if (Ctx.State.IsSandbox)
                sb.DrawString(f, "SANDBOX (all tech)", new Vector2(PaletteW + 24 + f.MeasureString($"Science: {Ctx.State.Science:0}").X + 18, 44), new Color(255, 200, 90));

            // The R&D tech tree is modal: while open it owns all input, so the editor panels below
            // (which handle clicks immediately as they draw) are not drawn underneath it.
            if (_showRnd)
            {
                DrawRnd(pb, sb, f, inp, w, h);
            }
            else if (_showModulePicker)
            {
                DrawModulePicker(pb, sb, f, inp, w, h);
            }
            else if (_showCrewPicker)
            {
                DrawCrewPicker(pb, sb, f, inp, w, h);
            }
            else if (_showLoadShip)
            {
                DrawLoadShip(pb, sb, f, inp, w, h);
            }
            else
            {
                _tooltipDef = null; _tooltipEntry = null; _modTipDef = null;
                if (UiDraw.Button(pb, sb, f, new Rectangle(PaletteW + 180, 38, 110, 26), "R&D  [R]", inp)) _showRnd = true;
                DrawPalette(pb, sb, f, inp, h);
                DrawStack(pb, sb, f, inp, w, h);
                DrawStats(pb, sb, f, inp, w, h);
                if (_tooltipDef != null) DrawTooltip(pb, sb, f, _tooltipDef, _tooltipEntry, _tooltipAt, w, h);
                if (_modTipDef != null) UiDraw.ModuleTooltip(pb, sb, f, _modTipDef, _modTipAt, w, h);
            }

            pb.End();
            sb.End();
        }

        // ---------- palette ----------
        private void DrawPalette(Rendering.PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                                 Microsoft.Xna.Framework.Graphics.SpriteFont f, InputState inp, int h)
        {
            var panel = new Rectangle(8, 8, PaletteW - 16, h - 16);
            UiDraw.Panel(pb, panel);
            sb.DrawString(f, "PARTS  (click, then click a slot)", new Vector2(20, 18), UiDraw.TextDim);

            // --- search box (fixed header) ---
            var searchR = new Rectangle(16, 38, PaletteW - 32, 26);
            if (UiDraw.TextField(pb, sb, f, searchR, ref _searchText, _searchFocus, inp, 20))
                { _searchFocus = true; _nameFocus = false; }
            if (string.IsNullOrEmpty(_searchText) && !_searchFocus)
                sb.DrawString(f, "search...", new Vector2(searchR.X + 8, searchR.Y + 5), new Color(90, 100, 120));

            // --- gather available parts by category, honoring the search filter ---
            string q = (_searchText ?? "").Trim().ToLowerInvariant();
            bool searching = q.Length > 0;
            var groups = new List<(string Name, List<PartDef> Parts)>();
            foreach (var (name, kinds) in Categories)
            {
                var list = new List<PartDef>();
                foreach (var def in PartCatalog.All)
                {
                    if (System.Array.IndexOf(kinds, def.Kind) < 0) continue;
                    if (!Progression.TechTree.PartAvailable(Ctx.State, def.Name)) continue;
                    if (searching && !def.Name.ToLowerInvariant().Contains(q)) continue;
                    list.Add(def);
                }
                if (list.Count > 0) groups.Add((name, list));
            }

            // --- body region (scrolled) ---
            const int headerRow = 24, partRow = 47;
            int bodyTop = 74, bodyBot = panel.Bottom - 10;
            int bodyH = bodyBot - bodyTop;
            var bodyR = new Rectangle(16, bodyTop, PaletteW - 32 - 12, bodyH);   // reserve 12px for the scrollbar
            int rowW = bodyR.Width;

            int contentH = 0;
            foreach (var g in groups)
            {
                contentH += headerRow;
                if (searching || !_collapsed.Contains(g.Name)) contentH += g.Parts.Count * partRow;
            }

            bool overBody = bodyR.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);
            if (overBody) _paletteScroll -= inp.WheelDelta * 0.4f;
            _paletteScroll = Math.Clamp(_paletteScroll, 0, Math.Max(0, contentH - bodyH));

            float yc = bodyTop - _paletteScroll;
            foreach (var g in groups)
            {
                bool collapsed = !searching && _collapsed.Contains(g.Name);
                // category header
                if (yc + headerRow > bodyTop && yc < bodyBot)
                {
                    var hr = new Rectangle(bodyR.X, (int)yc, rowW, headerRow - 3);
                    bool hHover = hr.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y) && overBody;
                    pb.FillRect(hr, hHover ? new Color(40, 60, 90, 235) : new Color(30, 44, 66, 230));
                    pb.RectOutline(hr, 1, UiDraw.PanelBorder);
                    sb.DrawString(f, $"{(collapsed ? "+" : "-")} {g.Name} ({g.Parts.Count})", new Vector2(hr.X + 8, hr.Y + 3), new Color(190, 205, 225));
                    if (hHover && inp.LeftClick)
                    {
                        if (collapsed) _collapsed.Remove(g.Name); else _collapsed.Add(g.Name);
                    }
                }
                yc += headerRow;
                if (collapsed) continue;

                foreach (var def in g.Parts)
                {
                    if (yc + partRow > bodyTop && yc < bodyBot)   // only rows at least partly in view
                    {
                        var r = new Rectangle(bodyR.X, (int)yc, rowW, 42);
                        bool hover = r.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y) && overBody;
                        bool isHeld = _held == def;
                        pb.FillRect(r, isHeld ? new Color(60, 95, 140, 230) : hover ? new Color(45, 70, 105, 230) : new Color(24, 36, 56, 220));
                        pb.RectOutline(r, 1, isHeld ? UiDraw.Accent : UiDraw.PanelBorder);
                        // icon box (texture or procedural-shape placeholder), then name + small stat line
                        var iconBox = new Rectangle(r.X + 4, r.Y + 4, 34, 34);
                        pb.FillRect(iconBox, new Color(14, 20, 32, 220));
                        DrawPartIcon(pb, def, new Rectangle(iconBox.X + 2, iconBox.Y + 2, iconBox.Width - 4, iconBox.Height - 4), Ctx.Textures);
                        pb.RectOutline(iconBox, 1, new Color(50, 66, 92));
                        sb.DrawString(f, def.Name, new Vector2(r.X + 46, r.Y + 4), Color.White);
                        UiDraw.SmallText(sb, f, def.StatLine, new Vector2(r.X + 46, r.Y + 24), UiDraw.TextDim, 0.8f);
                        if (hover) { _tooltipDef = def; _tooltipEntry = null; _tooltipAt = inp.MousePos; }
                        if (hover && inp.LeftClick) { _held = def; _selected = -1; }
                    }
                    yc += partRow;
                }
            }

            if (groups.Count == 0)
                sb.DrawString(f, searching ? "no parts match" : "no parts unlocked", new Vector2(bodyR.X + 4, bodyTop + 4), UiDraw.TextDim);

            // scrollbar gutter on the right edge of the body
            var track = new Rectangle(panel.Right - 14, bodyTop, 10, bodyH);
            _paletteScroll = UiDraw.VScrollbar(pb, track, _paletteScroll, bodyH, contentH, inp, ref _paletteDrag);
        }

        /// <summary>Build a stack entry for a freshly placed part, pre-seeding any modules the part
        /// ships with (an "advanced" part) into its slots, up to the slot budget. Loaded designs use the
        /// plain <see cref="StackEntry"/> constructor instead, so their saved modules stay authoritative.</summary>
        private static StackEntry NewEntry(PartDef d)
        {
            var e = new StackEntry(d);
            int used = 0;
            foreach (var name in d.DefaultModules)
            {
                var md = ModuleCatalog.Get(name);
                if (md == null || used + md.SlotCost > d.Slots) continue;
                e.Modules.Add(md);
                used += md.SlotCost;
            }
            return e;
        }

        /// <summary>Effective activation stage of each axial stack entry: the player's explicit tags where
        /// set, otherwise the geometry-derived defaults (shared with launch via <see cref="Staging.AssignDefaultStages"/>).
        /// Used to draw the stage bands and the per-part stage controls.</summary>
        private int[] EffectiveStages()
        {
            var parts = new List<Part>(Stack.Count);
            foreach (var e in Stack)
            {
                var p = new Part(e.Def) { Stage = e.Stage };
                VesselDesign.MaterializeRadials(e, p);
                parts.Add(p);
            }
            Staging.AssignDefaultStages(parts);
            var res = new int[parts.Count];
            for (int i = 0; i < parts.Count; i++) res[i] = parts[i].Stage;
            return res;
        }

        // ---------- stack ----------
        private void DrawStack(Rendering.PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                               Microsoft.Xna.Framework.Graphics.SpriteFont f, InputState inp, int w, int h)
        {
            float cx = PaletteW + (w - PaletteW - StatsW) / 2f;
            _radialHits.Clear();   // rebuilt below by DrawRadials, then used for placement/selection
            double totalH = 0;
            foreach (var e in Stack) totalH += e.Def.Height;

            float baseScale = (float)Math.Min(15.0, (h - 170) / Math.Max(totalH, 8));

            // build area = central column between the palette and stats panels; zoom/pan only react here
            var buildRegion = new Rectangle(PaletteW, 0, w - PaletteW - StatsW, h);
            bool overBuild = buildRegion.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);

            // wheel zoom toward the cursor (gated on overBuild so the palette/stats wheel-scroll is unaffected)
            if (overBuild && inp.WheelDelta != 0)
            {
                float oldScale = baseScale * _zoom;
                float oldTopY = (float)(h - totalH * oldScale) / 2f + _panY;
                float worldY = (inp.MousePos.Y - oldTopY) / Math.Max(oldScale, 1e-4f);
                _zoom = Math.Clamp(_zoom * (inp.WheelDelta > 0 ? 1.1f : 1f / 1.1f), 0.3f, 8f);
                float ns = baseScale * _zoom;
                _panY = inp.MousePos.Y - (float)worldY * ns - (float)(h - totalH * ns) / 2f;
            }

            // middle-button drag pans vertically
            if (overBuild && inp.MiddleDown && !_stackPan) { _stackPan = true; _lastMouseY = inp.MousePos.Y; }
            if (_stackPan && inp.MiddleDown) { _panY += inp.MousePos.Y - _lastMouseY; _lastMouseY = inp.MousePos.Y; }
            if (!inp.MiddleDown) _stackPan = false;

            if (_zoom <= 1f) _panY = 0f;   // at/below fit, keep the stack centered

            float scale = baseScale * _zoom;
            // clamp pan so at least part of the stack stays on screen
            float stackPx = (float)(totalH * scale);
            float maxPan = Math.Max(0, stackPx / 2f + h * 0.4f);
            _panY = Math.Clamp(_panY, -maxPan, maxPan);
            float topY = (float)(h - totalH * scale) / 2f + _panY;

            // zoom controls cluster (bottom-center of the build area); placement clicks ignore this strip
            var zoomBtns = new Rectangle((int)(cx - 62), h - 46, 124, 30);
            bool overZoom = zoomBtns.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);

            // gap boundaries (screen Y for insertion indices 0..Count)
            var gaps = new float[Stack.Count + 1];
            float gy = topY;
            gaps[0] = gy;
            for (int i = 0; i < Stack.Count; i++) { gy += (float)(Stack[i].Def.Height * scale); gaps[i + 1] = gy; }

            // stage bands: each part's effective activation stage (explicit tags or geometry defaults),
            // painted as an alternating translucent band behind each run of same-stage parts with an S{n} label.
            if (Stack.Count > 0)
            {
                var stageOf = EffectiveStages();
                int s = 0;
                while (s < Stack.Count)
                {
                    int e = s;
                    while (e + 1 < Stack.Count && stageOf[e + 1] == stageOf[s]) e++;
                    float top = gaps[s], bot = gaps[e + 1];
                    Color band = (stageOf[s] % 2 == 1) ? new Color(70, 100, 145, 34) : new Color(40, 60, 90, 22);
                    pb.FillRect(cx - 142, top, 284, bot - top, band);
                    sb.DrawString(f, $"S{stageOf[s]}", new Vector2(cx - 138, (top + bot) / 2 - 8), new Color(150, 180, 215));
                    s = e + 1;
                }
            }

            // launch pad line
            pb.FillRect(cx - 120, gaps[Stack.Count] + 6, 240, 3, new Color(80, 95, 115));

            // parts
            float y = topY;
            int hoverPart = -1;
            for (int i = 0; i < Stack.Count; i++)
            {
                var d = Stack[i].Def;
                float ph = (float)(d.Height * scale);
                float pw = (float)(d.Width * scale);
                var rect = new Rectangle((int)(cx - pw / 2), (int)y, (int)pw, (int)ph);
                DrawPartShape(pb, d, cx, y, pw, ph, Ctx.Textures);
                DrawRadials(pb, i, Stack[i], cx, y, pw, ph, scale);
                DrawModuleSprites(pb, Stack[i], cx, y, pw, ph);
                if (Stack[i].Modules.Count > 0)
                    pb.FillCircle(new Vector2(rect.Right + 8, rect.Center.Y), 4, new Color(120, 210, 255));
                bool hover = rect.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);
                if (hover) hoverPart = i;
                if (hover && _held == null) { _tooltipDef = d; _tooltipEntry = Stack[i]; _tooltipAt = inp.MousePos; }
                if (i == _selected) pb.RectOutline(new Rectangle(rect.X - 3, rect.Y - 2, rect.Width + 6, rect.Height + 4), 1.5f, UiDraw.Accent);
                else if (hover && _held == null) pb.RectOutline(new Rectangle(rect.X - 3, rect.Y - 2, rect.Width + 6, rect.Height + 4), 1, new Color(90, 110, 140));
                if (hover && _held == null && inp.LeftClick) _selected = i;
                y += ph;
            }

            // which radial sub-stack part is the mouse over? (for append-below and radial selection)
            int hitStack = -1, hitMount = -1;
            foreach (var hit in _radialHits)
                if (hit.rect.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y)) { hitStack = hit.stackIndex; hitMount = hit.mountIndex; }

            // placement: radial roots mount to a hovered part's sides; stackable parts append below an
            // existing radial sub-stack when hovered over one; otherwise everything inserts into an axial gap
            _hoverGap = -1;
            if (_held != null && inp.MousePos.X > PaletteW && inp.MousePos.X < w - StatsW && !overZoom)
            {
                // any part can be side-mounted while holding Alt; inherently-radial parts always mount radially
                bool radialMode = _held.Radial || inp.Down(Keys.LeftAlt) || inp.Down(Keys.RightAlt);
                if (radialMode)
                {
                    if (hoverPart >= 0)
                    {
                        var e = Stack[hoverPart];
                        float ph = (float)(e.Def.Height * scale), pw = (float)(e.Def.Width * scale);
                        float yy = gaps[hoverPart];
                        pb.RectOutline(new Rectangle((int)(cx - pw / 2 - 10), (int)yy - 2, (int)pw + 20, (int)ph + 4), 2, new Color(120, 230, 140));
                        // ghost the symmetric pair at the column where it would land
                        float gw = (float)(_held.Width * scale), gh = (float)(_held.Height * scale);
                        float off = NextMountOffset(e, pw, scale, gw);
                        float gyTop = yy + (ph - gh) * 0.5f;
                        DrawGhost(pb, _held, cx + off, gyTop, gw, gh);
                        DrawGhost(pb, _held, cx - off, gyTop, gw, gh);
                        if (inp.LeftClick) { e.AddRadial(_held); _selected = hoverPart; }
                    }
                    else
                    {
                        var msg = "Hover a part to mount " + _held.Name + " radially (mirrored pair)";
                        sb.DrawString(f, msg, new Vector2(cx - f.MeasureString(msg).X / 2, topY - 22), new Color(120, 230, 140));
                    }
                }
                else if (hitStack >= 0)
                {
                    // append below the hovered radial mount, mirrored on both sides
                    float gw = (float)(_held.Width * scale), gh = (float)(_held.Height * scale);
                    float bottom = float.MinValue, leftX = cx, rightX = cx;
                    foreach (var hit in _radialHits)
                        if (hit.stackIndex == hitStack && hit.mountIndex == hitMount)
                        {
                            bottom = Math.Max(bottom, hit.rect.Bottom);
                            float cxh = hit.rect.X + hit.rect.Width / 2f;
                            if (cxh < cx) leftX = cxh; else rightX = cxh;
                        }
                    DrawGhost(pb, _held, leftX, bottom, gw, gh);
                    DrawGhost(pb, _held, rightX, bottom, gw, gh);
                    var tip = "Attach " + _held.Name + " below this radial (mirrored)";
                    sb.DrawString(f, tip, new Vector2(cx - f.MeasureString(tip).X / 2, topY - 22), new Color(120, 230, 140));
                    if (inp.LeftClick) { Stack[hitStack].AppendToMount(hitMount, _held); _selected = hitStack; }
                }
                else
                {
                    int best = 0; float bestD = float.MaxValue;
                    for (int i = 0; i <= Stack.Count; i++)
                    {
                        float dist = Math.Abs(inp.MousePos.Y - gaps[i]);
                        if (dist < bestD) { bestD = dist; best = i; }
                    }
                    _hoverGap = best;
                    pb.FillRect(cx - 90, gaps[best] - 2, 180, 4, new Color(120, 230, 140));
                    var hint = "hold Alt + hover a part to mount " + _held.Name + " radially";
                    sb.DrawString(f, hint, new Vector2(cx - f.MeasureString(hint).X / 2, topY - 22), new Color(110, 130, 160));
                    if (inp.LeftClick) Stack.Insert(best, NewEntry(_held));
                }
            }
            else if (_held == null && hitStack >= 0 && inp.LeftClick && !overZoom)
                _selected = hitStack;   // click a radial part to select its host and open the radial panel

            if (Stack.Count == 0)
            {
                string msg = "Pick parts from the palette and click here to stack them";
                var sz = f.MeasureString(msg);
                sb.DrawString(f, msg, new Vector2(cx - sz.X / 2, h / 2f), UiDraw.TextDim);
            }

            // zoom controls: -  Fit  +  with the current factor above them
            sb.DrawString(f, $"x{_zoom:0.0}", new Vector2(cx - f.MeasureString($"x{_zoom:0.0}").X / 2, zoomBtns.Y - 18), UiDraw.TextDim);
            if (UiDraw.Button(pb, sb, f, new Rectangle(zoomBtns.X, zoomBtns.Y, 30, 30), "-", inp))
                _zoom = Math.Clamp(_zoom / 1.25f, 0.3f, 8f);
            if (UiDraw.Button(pb, sb, f, new Rectangle(zoomBtns.X + 34, zoomBtns.Y, 56, 30), "Fit", inp))
                { _zoom = 1f; _panY = 0f; }
            if (UiDraw.Button(pb, sb, f, new Rectangle(zoomBtns.X + 94, zoomBtns.Y, 30, 30), "+", inp))
                _zoom = Math.Clamp(_zoom * 1.25f, 0.3f, 8f);

            if (_held != null)
                sb.DrawString(f, _held.Name, inp.MousePos + new Vector2(14, 10), UiDraw.Accent);
        }

        /// <summary>Full-stat hover tooltip for a part, drawn last so it floats over every panel. ASCII units.
        /// The title line is full-size; detail lines render small. When <paramref name="entry"/> is supplied
        /// (hovering a placed part) its fitted modules are listed; otherwise the part's built-in
        /// <see cref="PartDef.DefaultModules"/> are shown.</summary>
        private static void DrawTooltip(Rendering.PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                                        Microsoft.Xna.Framework.Graphics.SpriteFont f, PartDef d, StackEntry entry, Vector2 mouse, int w, int h)
        {
            const float small = 0.8f;
            var detail = new List<string> { d.Kind.ToString() };
            double total = d.DryMass + d.FuelCapacity;
            detail.Add(d.FuelCapacity > 0 ? $"Mass: {total / 1000:0.00} t  (dry {d.DryMass / 1000:0.00} t)"
                                          : $"Mass: {d.DryMass / 1000:0.00} t");
            if (d.Kind == PartKind.Engine || d.Kind == PartKind.SolidBooster)
            {
                detail.Add($"Thrust: {d.Thrust / 1000:0} kN   Isp: {d.Isp:0} s");
                detail.Add($"Flow: {d.FuelFlowAtMax:0.0} kg/s");
            }
            if (d.FuelCapacity > 0) detail.Add($"Fuel: {d.FuelCapacity:0} kg");
            detail.Add($"Size: {d.Width:0.0} x {d.Height:0.0} m   Drag (CdA): {d.CdA:0.00} m2");
            if (d.Slots > 0) detail.Add($"Slots: {d.Slots}");

            // modules: actual fitted ones for a placed part, else the part's built-in loadout
            var modules = new List<string>();
            if (entry != null) foreach (var m in entry.Modules) modules.Add(m.SlotCost > 1 ? $"{m.Name} [{m.SlotCost}]" : m.Name);
            else foreach (var name in d.DefaultModules) modules.Add(name);
            if (modules.Count > 0) detail.Add("Modules: " + string.Join(", ", modules));
            else if (d.Slots > 0) detail.Add("Modules: (none fitted)");

            float lhTitle = f.MeasureString("X").Y + 2;
            float lhSmall = f.MeasureString("X").Y * small + 2;
            float tw = f.MeasureString(d.Name).X;
            foreach (var ln in detail) tw = Math.Max(tw, f.MeasureString(ln).X * small);
            int bw = (int)tw + 18, bh = (int)(lhTitle + detail.Count * lhSmall) + 12;
            int bx = (int)mouse.X + 16, by = (int)mouse.Y + 14;
            if (bx + bw > w - 4) bx = (int)mouse.X - bw - 12;   // flip left near the right edge
            if (by + bh > h - 4) by = h - 4 - bh;
            if (bx < 4) bx = 4; if (by < 4) by = 4;

            UiDraw.Panel(pb, new Rectangle(bx, by, bw, bh));
            float ty = by + 6;
            sb.DrawString(f, d.Name, new Vector2(bx + 9, ty), Color.White);
            ty += lhTitle;
            for (int i = 0; i < detail.Count; i++)
            {
                Color c = i == 0 ? UiDraw.Accent : UiDraw.TextDim;
                UiDraw.SmallText(sb, f, detail[i], new Vector2(bx + 9, ty), c, small);
                ty += lhSmall;
            }
        }

        /// <summary>Outward X offset (from the host center) where the NEXT radial mount column would be
        /// centered, given the host's existing mounts. Mirrors the column layout in <see cref="DrawRadials"/>.</summary>
        private static float NextMountOffset(StackEntry e, float hostPw, float scale, float newW)
        {
            float colX = hostPw / 2 + 4;
            foreach (var mount in e.Mounts)
            {
                if (mount.Parts.Count == 0) continue;
                float colW = 0;
                foreach (var rd in mount.Parts) colW = Math.Max(colW, (float)(rd.Width * scale));
                colX += colW + 3;
            }
            return colX + newW / 2;
        }

        /// <summary>Translucent placement ghost of a part: a tinted fill plus a green outline.</summary>
        private static void DrawGhost(Rendering.PrimitiveBatch pb, PartDef d, float cx, float yTop, float gw, float gh)
        {
            var r = new Rectangle((int)(cx - gw / 2), (int)yTop, (int)gw, (int)gh);
            pb.FillRect(r, new Color(d.Tint.R, d.Tint.G, d.Tint.B, (byte)90));
            pb.RectOutline(r, 1, new Color(120, 230, 140));
        }

        /// <summary>Draw a stack entry's radial mounts (symmetric pairs of vertical sub-stacks) on both
        /// sides of the part, and record each drawn part's screen rect into <see cref="_radialHits"/> so the
        /// placement/selection code can hit-test radial parts.</summary>
        private void DrawRadials(Rendering.PrimitiveBatch pb, int stackIndex, StackEntry e, float cx, float y, float pw, float ph, float scale)
        {
            float colX = pw / 2 + 4;   // running outward offset from the host edge to each mount's column
            for (int mi = 0; mi < e.Mounts.Count; mi++)
            {
                var mount = e.Mounts[mi];
                if (mount.Parts.Count == 0) continue;
                float colW = 0, colH = 0;   // column width = widest part; height = sum of sub-stack heights
                foreach (var rd in mount.Parts) { colW = Math.Max(colW, (float)(rd.Width * scale)); colH += (float)(rd.Height * scale); }
                float cxOff = colX + colW / 2;
                float yTop = y + (ph - colH) * 0.5f;   // vertically center the whole sub-stack on the host
                for (int s = -1; s <= 1; s += 2)
                {
                    float side = cx + s * cxOff;
                    float yy = yTop;
                    for (int pi = 0; pi < mount.Parts.Count; pi++)
                    {
                        var rd = mount.Parts[pi];
                        float rw = (float)(rd.Width * scale), rh = (float)(rd.Height * scale);
                        DrawPartShape(pb, rd, side, yy, rw, rh, Ctx.Textures, flipX: s < 0);   // mirror the left column
                        _radialHits.Add((stackIndex, mi, pi, new Rectangle((int)(side - rw / 2), (int)yy, (int)rw, (int)rh)));
                        yy += rh;
                    }
                    // "included" mounts ride the core stage: outline the whole column green to show the choice
                    if (!mount.Separate)
                        pb.RectOutline(new Rectangle((int)(side - colW / 2) - 1, (int)yTop - 1, (int)colW + 2, (int)colH + 2), 1, new Color(150, 230, 150));
                }
                colX += colW + 3;   // the next mount sits further out
            }
        }

        /// <summary>Axis-aligned editor rendering of a part (screen coords, Y down, part spans [y, y+ph]).</summary>
        /// <summary>Draw a part's icon (its texture, or the procedural shape fallback) fitted into
        /// <paramref name="box"/> preserving the part's aspect ratio. Used by the palette so each entry
        /// reads as a real part rather than a color swatch.</summary>
        private static void DrawPartIcon(Rendering.PrimitiveBatch pb, PartDef d, Rectangle box, Rendering.TextureStore tex)
        {
            double aspect = d.Height > 0 ? d.Width / d.Height : 1.0;
            float pw = box.Width, ph = box.Height;
            if (aspect >= 1) ph = (float)(pw / aspect); else pw = (float)(ph * aspect);
            float cx = box.X + box.Width / 2f;
            float y = box.Y + (box.Height - ph) / 2f;
            DrawPartShape(pb, d, cx, y, pw, ph, tex);
        }

        /// <summary>Overlay the icon sprites of fitted deployable modules (solar wings, drill, light) on a
        /// stack part in the VAB, so the assembled ship previews how it will look deployed in flight.</summary>
        private void DrawModuleSprites(Rendering.PrimitiveBatch pb, StackEntry e, float cx, float y, float pw, float ph)
        {
            float my = y + ph / 2f;
            foreach (var m in e.Modules)
            {
                var t = Ctx.Textures?.Module(m.Id);
                if (t == null) continue;
                switch (m.Kind)
                {
                    case ModuleKind.SolarPanel:
                    {
                        float pl = pw * 1.3f, hh = Math.Max(3f, ph * 0.35f);
                        pb.TexturedQuad(t, new Vector2(cx + pw / 2, my - hh), new Vector2(cx + pw / 2 + pl, my - hh),
                                           new Vector2(cx + pw / 2 + pl, my + hh), new Vector2(cx + pw / 2, my + hh), Color.White, false);
                        pb.TexturedQuad(t, new Vector2(cx - pw / 2, my - hh), new Vector2(cx - pw / 2 - pl, my - hh),
                                           new Vector2(cx - pw / 2 - pl, my + hh), new Vector2(cx - pw / 2, my + hh), Color.White, true);
                        break;
                    }
                    case ModuleKind.Harvester:
                    {
                        float s = Math.Min(pw, ph) * 0.5f;
                        pb.TexturedQuad(t, new Vector2(cx - s / 2, my - s / 2), new Vector2(cx + s / 2, my - s / 2),
                                           new Vector2(cx + s / 2, my + s / 2), new Vector2(cx - s / 2, my + s / 2), Color.White);
                        break;
                    }
                    case ModuleKind.Light:
                    {
                        pb.FillCircle(new Vector2(cx, my), Math.Max(4f, pw * 0.5f), new Color(255, 245, 200, 70));
                        float s = Math.Min(pw, ph) * 0.4f;
                        pb.TexturedQuad(t, new Vector2(cx - s / 2, my - s / 2), new Vector2(cx + s / 2, my - s / 2),
                                           new Vector2(cx + s / 2, my + s / 2), new Vector2(cx - s / 2, my + s / 2), Color.White);
                        break;
                    }
                }
            }
        }

        private static void DrawPartShape(Rendering.PrimitiveBatch pb, PartDef d, float cx, float y, float pw, float ph, Rendering.TextureStore tex = null, bool flipX = false)
        {
            Color dark = Rendering.PlanetRenderer.Darken(d.Tint, 0.4f);
            Color light = Rendering.PlanetRenderer.Lighten(d.Tint, 0.12f);
            float l = cx - pw / 2, r = cx + pw / 2, b = y + ph;

            var pt = tex?.Part(d.Id);
            if (pt != null)
            {
                pb.TexturedQuad(pt, new Vector2(l, y), new Vector2(r, y), new Vector2(r, b), new Vector2(l, b), Color.White, flipX);
                return;
            }

            switch (d.Kind)
            {
                case PartKind.Pod:
                    pb.Quad(new Vector2(cx - pw * 0.18f, y), new Vector2(cx + pw * 0.18f, y), new Vector2(r, b), new Vector2(l, b),
                            light, light, dark, dark);
                    pb.FillCircle(new Vector2(cx, y + ph * 0.55f), pw * 0.15f, new Color(40, 60, 100));
                    break;
                case PartKind.Engine:
                    pb.FillRect(cx - pw * 0.28f, y, pw * 0.56f, ph * 0.55f, d.Tint);
                    pb.Quad(new Vector2(cx - pw * 0.18f, y + ph * 0.55f), new Vector2(cx + pw * 0.18f, y + ph * 0.55f),
                            new Vector2(r, b), new Vector2(l, b), light, light, dark, dark);
                    break;
                case PartKind.Fins:
                    pb.FillRect(cx - pw * 0.18f, y, pw * 0.36f, ph, d.Tint);
                    pb.Tri(new Vector2(cx - pw * 0.18f, y), new Vector2(l, b), new Vector2(cx - pw * 0.18f, b), d.Tint);
                    pb.Tri(new Vector2(cx + pw * 0.18f, y), new Vector2(r, b), new Vector2(cx + pw * 0.18f, b), d.Tint);
                    break;
                case PartKind.Parachute:
                    pb.Quad(new Vector2(cx - pw * 0.3f, y), new Vector2(cx + pw * 0.3f, y), new Vector2(r, b), new Vector2(l, b),
                            d.Tint, d.Tint, dark, dark);
                    break;
                case PartKind.SolidBooster:
                    pb.Tri(new Vector2(cx, y), new Vector2(l, y + ph * 0.1f), new Vector2(r, y + ph * 0.1f), light);
                    pb.Quad(new Vector2(l, y + ph * 0.1f), new Vector2(r, y + ph * 0.1f), new Vector2(r, b), new Vector2(l, b),
                            dark, light, light, dark);
                    break;
                case PartKind.Aero: // nose cone (apex up)
                    pb.Tri(new Vector2(l, b), new Vector2(r, b), new Vector2(cx, y), dark, dark, light);
                    break;
                default:
                    pb.Quad(new Vector2(l, y), new Vector2(r, y), new Vector2(r, b), new Vector2(l, b),
                            dark, light, light, dark);
                    break;
            }
        }

        // ---------- stats ----------
        private void DrawStats(Rendering.PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                               Microsoft.Xna.Framework.Graphics.SpriteFont f, InputState inp, int w, int h)
        {
            UiDraw.Panel(pb, new Rectangle(w - StatsW + 8, 8, StatsW - 16, h - 16));
            float x = w - StatsW + 22;
            float y = 18;
            sb.DrawString(f, "SHIP NAME", new Vector2(x, y), UiDraw.TextDim); y += 20;
            if (UiDraw.TextField(pb, sb, f, new Rectangle((int)x, (int)y, StatsW - 44, 30), ref Ctx.Design.Name, _nameFocus, inp))
                { _nameFocus = true; _searchFocus = false; }
            y += 42;
            // ---- scrollable body: everything below the fixed name field, above the launch buttons ----
            int bodyTop = (int)y;
            int bodyBot = h - 166;                     // clear of the fixed LAUNCH/SAVE button cluster (by = h-158)
            int bodyH = bodyBot - bodyTop;
            var bodyView = new Rectangle(w - StatsW + 8, bodyTop, StatsW - 16, bodyH);
            if (bodyView.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y)) _statsScroll -= inp.WheelDelta * 0.4f;
            _statsScroll = Math.Clamp(_statsScroll, 0, Math.Max(0, _statsContentH - bodyH));
            float bodyStartY = bodyTop - _statsScroll;
            y = bodyStartY;
            // clipped draw/button helpers: only render (and accept clicks) within the viewport
            bool Vis(float top, float ht) => top >= bodyTop && top + ht <= bodyBot;
            void Str(string s, float sx, float sy, Color c) { if (Vis(sy, 18)) sb.DrawString(f, s, new Vector2(sx, sy), c); }
            bool Btn(Rectangle r, string label, bool enabled = true) => Vis(r.Y, r.Height) && UiDraw.Button(pb, sb, f, r, label, inp, enabled);

            Str("ROCKET STATS", x, y, UiDraw.TextDim); y += 26;

            double mass = 0, height = 0;
            foreach (var e in Stack)
            {
                mass += e.Def.DryMass + e.Def.FuelCapacity;
                foreach (var m in e.Modules) mass += m.DryMass;
                foreach (var mount in e.Mounts) foreach (var rd in mount.Parts) mass += 2 * (rd.DryMass + rd.FuelCapacity); // symmetric pair
                height += e.Def.Height;
            }
            Str($"Parts: {Stack.Count}    Mass: {mass / 1000:0.0} t", x, y, Color.White); y += 20;
            Str($"Height: {height:0.0} m", x, y, Color.White); y += 28;

            var stages = Staging.ComputeStages(Stack);
            double totalDv = 0;
            foreach (var st in stages) totalDv += st.DeltaV;
            Str("STAGES (S0 fires first; select a part to re-stage)", x, y, UiDraw.TextDim); y += 22;
            for (int i = 0; i < stages.Count; i++)
            {
                var st = stages[i];
                Str($"S{st.Number}: {st.Engines}{(st.Decouples ? "  [decouple]" : "")}", x, y, Color.White);
                string line = st.DeltaV > 0
                    ? $"   dV {st.DeltaV:0} m/s  TWR {st.Twr:0.00}  {st.BurnTime:0}s"
                    : "   (no thrust)";
                Color c = st.DeltaV > 0 && st.Number == stages.Count && st.Twr < 1.0 ? new Color(255, 150, 90) : UiDraw.TextDim;
                Str(line, x, y + 18, c);
                y += 38;
            }
            y += 8;
            Str($"Total dV: {totalDv:0} m/s", x, y, UiDraw.Accent); y += 22;
            Str("~3400 m/s reaches Earth orbit", x, y, UiDraw.TextDim); y += 30;

            // ---- power & resources summary ----
            double ecCap = 0, ecGen = 0, ecDraw = 0, oxCap = 0, waCap = 0, foCap = 0;
            int seats = 0, crewAssigned = 0;
            foreach (var e in Stack)
            {
                if (e.Def.Kind == PartKind.Pod) { ecCap += Vessel.PodEcCapacity; ecDraw += Vessel.PodEcDraw; }
                seats += e.SeatCount;
                crewAssigned += System.Math.Min(e.CrewNames.Count, e.SeatCount);
                foreach (var m in e.Modules)
                {
                    ecCap  += m.EcCapacity;
                    ecGen  += m.EcProduce;
                    ecDraw += m.EcDraw;
                    oxCap  += m.OxygenCapacity;
                    waCap  += m.WaterCapacity;
                    foCap  += m.FoodCapacity;
                }
            }
            if (ecCap > 0 || ecGen > 0 || seats > 0)
            {
                Str("POWER & RESOURCES", x, y, UiDraw.TextDim); y += 22;
                if (ecCap > 0 || ecGen > 0 || ecDraw > 0)
                {
                    Str($"EC cap: {ecCap:0}  Gen: +{ecGen:0.#}/s  Draw: {ecDraw:0.#}/s", x, y, Color.White); y += 18;
                    double ecNet = ecGen - ecDraw;
                    string netTxt = ecNet >= 0 ? $"Net: +{ecNet:0.#}/s" : $"Net: {ecNet:0.#}/s";
                    Color netCol = ecNet < 0 ? new Color(255, 150, 90) : UiDraw.Accent;
                    Str(netTxt, x, y, netCol); y += 22;
                }
                if (seats > 0)
                {
                    Str($"Crew: {crewAssigned}/{seats} seats", x, y, Color.White); y += 18;
                    bool hasLs = oxCap > 0 || waCap > 0 || foCap > 0;
                    if (crewAssigned > 0 && hasLs)
                    {
                        double endurance = System.Math.Min(System.Math.Min(
                            oxCap / (crewAssigned * Vessel.OxygenPerCrew),
                            waCap / (crewAssigned * Vessel.WaterPerCrew)),
                            foCap / (crewAssigned * Vessel.FoodPerCrew));
                        Str($"Life support: {UiDraw.Time(endurance)}", x, y, Color.White); y += 18;
                    }
                    else if (crewAssigned > 0)
                    { Str("No life support fitted!", x, y, new Color(255, 150, 90)); y += 18; }
                }
                y += 6;
            }

            // ---- science summary (so the player can see what earns research points) ----
            int instruments = 0; bool hasAntenna = false;
            foreach (var e in Stack)
                foreach (var m in e.Modules)
                {
                    if (m.Kind == ModuleKind.Science) instruments++;
                    if (m.Kind == ModuleKind.Antenna) hasAntenna = true;
                }
            Str("SCIENCE", x, y, UiDraw.TextDim); y += 20;
            Str(instruments > 0 ? $"{instruments} instrument(s) aboard" : "No instruments fitted",
                          x, y, instruments > 0 ? Color.White : UiDraw.TextDim); y += 18;
            Str(hasAntenna ? "Antenna: readings transmit as science" : "Add an Antenna to transmit readings",
                          x, y, hasAntenna ? new Color(150, 230, 150) : new Color(255, 190, 90)); y += 18;
            Str("Also earn science from mission milestones", x, y, UiDraw.TextDim); y += 26;

            string err = Ctx.Design.Validate();
            if (err != null) { Str(err, x, y, new Color(255, 150, 90)); y += 24; }

            // bottom-stage TWR warning
            if (stages.Count > 0 && stages[0].DeltaV > 0 && stages[0].Twr < 1.0)
            { Str("First stage TWR < 1: won't lift off!", x, y, new Color(255, 150, 90)); y += 24; }

            // ---- slot modules for the selected part ----
            y += 6;
            if (_selected >= 0 && _selected < Stack.Count && Stack[_selected].Def.Slots > 0)
            {
                var entry = Stack[_selected];
                int used = 0;
                foreach (var em in entry.Modules) used += em.SlotCost;
                Str($"SLOTS  {entry.Def.Name}  ({used}/{entry.Def.Slots})", x, y, UiDraw.Accent); y += 22;
                int removeIdx = -1;
                for (int i = 0; i < entry.Modules.Count; i++)
                {
                    var em = entry.Modules[i];
                    if (Vis(y, 22))
                    {
                        var iconR = new Rectangle((int)x, (int)y, 20, 20);
                        UiDraw.Icon(pb, Ctx.Textures.Module(em.Id), iconR, em.Tint);
                        pb.RectOutline(iconR, 1, new Color(50, 66, 92));
                        sb.DrawString(f, em.Name, new Vector2(x + 26, y + 2), Color.White);
                        if (em.SlotCost > 1)
                            UiDraw.SmallText(sb, f, $"[{em.SlotCost}]", new Vector2(w - 96, y + 4), UiDraw.TextDim);
                        var rowR = new Rectangle((int)x, (int)y, w - 70 - (int)x, 22);
                        if (rowR.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y)) { _modTipDef = em; _modTipAt = inp.MousePos; }
                    }
                    if (Btn(new Rectangle(w - 64, (int)y, 38, 22), "del")) removeIdx = i;
                    y += 26;
                }
                if (removeIdx >= 0) entry.Modules.RemoveAt(removeIdx);
                if (used < entry.Def.Slots)
                {
                    if (Btn(new Rectangle((int)x, (int)y, StatsW - 44, 26), "+ Add module"))
                    { _showModulePicker = true; _pickerTarget = _selected; _pickerScroll = 0; }
                    y += 30;
                }
            }
            else if (_selected < 0)
                Str("Select a part to fit slot modules", x, y, UiDraw.TextDim);

            // ---- crew seats for the selected part ----
            if (_selected >= 0 && _selected < Stack.Count && Stack[_selected].SeatCount > 0)
            {
                var entry = Stack[_selected];
                // a removed crew cabin can drop the seat count below what's assigned: trim the overflow
                while (entry.CrewNames.Count > entry.SeatCount) entry.CrewNames.RemoveAt(entry.CrewNames.Count - 1);
                Str($"CREW  {entry.Def.Name}  ({entry.CrewNames.Count}/{entry.SeatCount})", x, y, UiDraw.Accent); y += 22;
                int removeCrew = -1;
                for (int i = 0; i < entry.CrewNames.Count; i++)
                {
                    var cm = Ctx.State.Roster.Find(c => c.Name == entry.CrewNames[i]);
                    string role = cm != null ? cm.Role.ToString() : "?";
                    Str($"- {entry.CrewNames[i]} ({role})", x, y + 3, Color.White);
                    if (Btn(new Rectangle(w - 64, (int)y, 38, 22), "del")) removeCrew = i;
                    y += 26;
                }
                if (removeCrew >= 0) entry.CrewNames.RemoveAt(removeCrew);
                if (entry.CrewNames.Count < entry.SeatCount && AvailableCrew().Count > 0)
                {
                    if (Btn(new Rectangle((int)x, (int)y, StatsW - 44, 26), "+ Assign crew"))
                    { _showCrewPicker = true; _crewTarget = _selected; }
                    y += 30;
                }
            }

            // ---- radial mounts on the selected part (independent of module slots) ----
            if (_selected >= 0 && _selected < Stack.Count && Stack[_selected].Mounts.Count > 0)
            {
                var entry = Stack[_selected];
                Str($"RADIAL  {entry.Def.Name}", x, y, UiDraw.Accent); y += 20;
                Str("STG = own stage   KEEP = rides core", x, y, UiDraw.TextDim); y += 18;
                Str("Hold a part, hover a radial to stack below it", x, y, UiDraw.TextDim); y += 20;
                int hostEff = EffectiveStages()[_selected];
                int toggleMount = -1, delMount = -1, delPartMount = -1, delPart = -1, dropDelta = 0, dropMount = -1;
                for (int mi = 0; mi < entry.Mounts.Count; mi++)
                {
                    var mount = entry.Mounts[mi];
                    bool sep = mount.Separate;
                    Color modeCol = sep ? new Color(120, 210, 255) : new Color(150, 230, 150);
                    string root = mount.Root != null ? mount.Root.Name : "(empty)";
                    int drop = mount.Stage >= 0 ? mount.Stage : hostEff + 1;   // effective drop stage
                    Str($"Mount {mi + 1}: {root} x2", x, y + 3, Color.White);
                    Str(sep ? $"drop S{drop}" : "on core", x + 14, y + 19, modeCol);
                    if (Btn(new Rectangle(w - 110, (int)y, 44, 22), sep ? "STG" : "KEEP")) toggleMount = mi;
                    if (Btn(new Rectangle(w - 62, (int)y, 38, 22), "del")) delMount = mi;
                    // drop-stage steppers for a separate mount, on the second row
                    if (sep)
                    {
                        if (Btn(new Rectangle(w - 110, (int)y + 22, 21, 20), "-", drop > 0)) { dropMount = mi; dropDelta = -1; }
                        if (Btn(new Rectangle(w - 86, (int)y + 22, 21, 20), "+")) { dropMount = mi; dropDelta = 1; }
                    }
                    y += 44;
                    // sub-stack parts (skip the lone root to keep single-part mounts compact)
                    if (mount.Parts.Count > 1)
                        for (int pi = 0; pi < mount.Parts.Count; pi++)
                        {
                            Str("   - " + mount.Parts[pi].Name, x, y + 1, UiDraw.TextDim);
                            if (Btn(new Rectangle(w - 62, (int)y, 38, 20), "del")) { delPartMount = mi; delPart = pi; }
                            y += 24;
                        }
                }
                if (toggleMount >= 0) entry.Mounts[toggleMount].Separate = !entry.Mounts[toggleMount].Separate;
                else if (dropMount >= 0)
                {
                    var m = entry.Mounts[dropMount];
                    int cur = m.Stage >= 0 ? m.Stage : hostEff + 1;
                    m.Stage = Math.Max(0, cur + dropDelta);
                }
                else if (delPartMount >= 0) entry.RemoveFromMount(delPartMount, delPart);
                else if (delMount >= 0) entry.RemoveRadial(delMount);
            }

            // set the selected part's activation stage (independent KSP-style ordering) and delete it
            if (_selected >= 0 && _selected < Stack.Count)
            {
                var sel = Stack[_selected];
                int eff = EffectiveStages()[_selected];
                y += 4;
                Str($"STAGE: S{eff}   (when this part fires)", x, y, UiDraw.Accent); y += 22;
                int halfW = (StatsW - 44 - 6) / 2;
                if (Btn(new Rectangle((int)x, (int)y, halfW, 26), "- earlier", eff > 0))
                    sel.Stage = Math.Max(0, eff - 1);
                if (Btn(new Rectangle((int)x + halfW + 6, (int)y, halfW, 26), "+ later"))
                    sel.Stage = eff + 1;
                y += 30;
                if (sel.Stage >= 0 && Btn(new Rectangle((int)x, (int)y, StatsW - 44, 22), "auto (clear stage)"))
                    sel.Stage = -1;
                if (sel.Stage >= 0) y += 26;
                y += 6;
                if (Btn(new Rectangle((int)x, (int)y, StatsW - 44, 28), "Delete part (+ below)"))
                { RemoveCascade(_selected); }
                y += 34;
            }

            // measure body height for next frame's clamp, then draw the scrollbar gutter when it overflows
            _statsContentH = y - bodyStartY;
            if (_statsContentH > bodyH)
            {
                var track = new Rectangle(w - 22, bodyTop, 10, bodyH);
                _statsScroll = UiDraw.VScrollbar(pb, track, _statsScroll, bodyH, _statsContentH, inp, ref _statsDrag);
            }

            int half = (StatsW - 48) / 2;
            int bx0 = w - StatsW + 20, bx1 = w - StatsW + 24 + half;
            int by = h - 158;
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx0, by, StatsW - 40, 44), "LAUNCH  [Enter]", inp, err == null))
            { _nameFocus = false; Ctx.Scenes.SwitchTo(new FlightScene(Ctx)); return; }
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx0, by + 50, half, 30), "NEW", inp))
            { _nameFocus = false; Stack.Clear(); _selected = -1; Ctx.Design.Name = "Ship 1"; }
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx1, by + 50, half, 30), "DEFAULT", inp))
            { _nameFocus = false; Ctx.Design.Stack = PartCatalog.DefaultDesign().ConvertAll(NewEntry); _selected = -1; }
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx0, by + 84, half, 30), "SAVE", inp))
            { _nameFocus = false; ShipLibrary.Save(DesignState.From(Ctx.Design), Ctx.Design.Name); }
            if (UiDraw.Button(pb, sb, f, new Rectangle(bx1, by + 84, half, 30), "LOAD", inp))
            { _nameFocus = false; _showLoadShip = true; _loadShipScroll = 0; }

            sb.DrawString(f, "[Del] / Delete button removes part + everything below", new Vector2(x, h - 28), UiDraw.TextDim);
        }

        // ---------- R&D tech-tree overlay ----------
        private void DrawRnd(Rendering.PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                             Microsoft.Xna.Framework.Graphics.SpriteFont f, InputState inp, int w, int h)
        {
            var gs = Ctx.State;
            var nodes = Progression.TechTree.Nodes;
            pb.FillRect(0, 0, w, h, new Color(0, 0, 0, 170));
            int pw = 560, rowH = 50;
            int ph = 80 + nodes.Count * rowH;
            int px = w / 2 - pw / 2, py = Math.Max(20, h / 2 - ph / 2);
            UiDraw.Panel(pb, new Rectangle(px, py, pw, ph));
            sb.DrawString(Ctx.FontBig, "RESEARCH & DEVELOPMENT", new Vector2(px + 20, py + 14), new Color(200, 215, 235));
            sb.DrawString(f, $"Science: {gs.Science:0}", new Vector2(px + pw - 150, py + 18), new Color(150, 230, 150));

            int y = py + 56;
            foreach (var n in nodes)
            {
                if (n.Cost <= 0) continue;   // the free starting node isn't shown
                bool unlocked = Progression.TechTree.IsUnlocked(gs, n.Id);
                bool prereq = Progression.TechTree.PrereqsMet(gs, n);
                bool canBuy = Progression.TechTree.CanUnlock(gs, n);
                var items = string.Join(", ", System.Linq.Enumerable.Concat(n.Parts, n.Modules));

                var rowR = new Rectangle(px + 16, y, pw - 32, rowH - 6);
                if (unlocked)
                {
                    pb.FillRect(rowR, new Color(28, 56, 36, 220));
                    pb.RectOutline(rowR, 1, new Color(90, 160, 110));
                    sb.DrawString(f, $"{n.Title}   (unlocked)", new Vector2(rowR.X + 10, rowR.Y + 6), new Color(150, 230, 150));
                }
                else if (canBuy)
                {
                    bool hover = rowR.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);
                    pb.FillRect(rowR, hover ? new Color(60, 95, 140, 235) : new Color(36, 56, 84, 230));
                    pb.RectOutline(rowR, 1, UiDraw.Accent);
                    sb.DrawString(f, $"{n.Title}   -   {n.Cost:0} sci  [unlock]", new Vector2(rowR.X + 10, rowR.Y + 6), Color.White);
                    if (hover && inp.LeftClick) Progression.TechTree.Unlock(gs, n);
                }
                else
                {
                    pb.FillRect(rowR, new Color(24, 30, 40, 220));
                    pb.RectOutline(rowR, 1, UiDraw.PanelBorder);
                    string why = !prereq ? $"needs {string.Join(", ", n.Prereqs)}" : $"{n.Cost:0} sci";
                    sb.DrawString(f, $"{n.Title}   -   {why}", new Vector2(rowR.X + 10, rowR.Y + 6), UiDraw.TextDim);
                }
                sb.DrawString(f, items, new Vector2(rowR.X + 10, rowR.Y + 24), UiDraw.TextDim);
                y += rowH;
            }
            sb.DrawString(f, "[R]/[Esc] close", new Vector2(px + 20, py + ph - 26), UiDraw.TextDim);
        }

        // ---------- "Add Module" overlay ----------
        private void DrawModulePicker(Rendering.PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                                     Microsoft.Xna.Framework.Graphics.SpriteFont f, InputState inp, int w, int h)
        {
            if (_pickerTarget < 0 || _pickerTarget >= Stack.Count) { _showModulePicker = false; return; }
            var entry = Stack[_pickerTarget];
            var gs = Ctx.State;
            int usedSlots = 0;
            foreach (var em in entry.Modules) usedSlots += em.SlotCost;
            int totalSlots = entry.Def.Slots;
            bool full = usedSlots >= totalSlots;

            // Build categorized module list
            var groups = new List<(string Name, List<ModuleDef> Modules)>();
            foreach (var (catName, kinds) in ModuleCategories)
            {
                var list = new List<ModuleDef>();
                foreach (var m in ModuleCatalog.All)
                {
                    if (System.Array.IndexOf(kinds, m.Kind) < 0) continue;
                    list.Add(m);
                }
                if (list.Count > 0) groups.Add((catName, list));
            }

            const int catHeaderH = 22, rowH = 34, headerH = 56, footerH = 30;
            // compute content height
            int contentH = 0;
            foreach (var g in groups) { contentH += catHeaderH + g.Modules.Count * rowH; }
            int bodyH = Math.Min(contentH, h - 120 - headerH - footerH);
            int ph = headerH + bodyH + footerH;
            int px = w / 2 - 300, pw = 600, py = Math.Max(20, h / 2 - ph / 2);

            pb.FillRect(0, 0, w, h, new Color(0, 0, 0, 170));
            UiDraw.Panel(pb, new Rectangle(px, py, pw, ph));
            sb.DrawString(Ctx.FontBig, "ADD MODULE", new Vector2(px + 20, py + 12), new Color(200, 215, 235));
            sb.DrawString(f, $"{entry.Def.Name}  ({usedSlots}/{totalSlots} used)",
                          new Vector2(px + pw - 220, py + 18), UiDraw.Accent);

            int bodyTop = py + headerH, bodyBot = bodyTop + bodyH;
            int maxScroll = Math.Max(0, contentH - bodyH);
            bool inPanel = inp.MousePos.X >= px && inp.MousePos.X <= px + pw && inp.MousePos.Y >= bodyTop && inp.MousePos.Y <= bodyBot;
            if (inPanel) _pickerScroll -= inp.WheelDelta * 0.4f;
            _pickerScroll = Math.Clamp(_pickerScroll, 0, maxScroll);

            float yc = bodyTop - _pickerScroll;
            foreach (var (catName, catMods) in groups)
            {
                // category header
                if (yc + catHeaderH > bodyTop && yc < bodyBot)
                {
                    var hr = new Rectangle(px + 16, (int)yc, pw - 32, catHeaderH - 2);
                    pb.FillRect(hr, new Color(28, 44, 66, 220));
                    pb.RectOutline(hr, 1, UiDraw.PanelBorder);
                    sb.DrawString(f, catName, new Vector2(hr.X + 6, hr.Y + 2), new Color(180, 200, 220));
                }
                yc += catHeaderH;

                foreach (var m in catMods)
                {
                    if (yc + rowH > bodyTop && yc < bodyBot)
                    {
                        var rowR = new Rectangle(px + 16, (int)yc + 2, pw - 32, rowH - 4);
                        bool avail = Progression.TechTree.ModuleAvailable(gs, m.Name);
                        bool fits = usedSlots + m.SlotCost <= totalSlots;
                        bool selectable = avail && fits && !full;
                        bool hover = selectable && rowR.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);

                        // row background + outline by state
                        Color bg = selectable ? (hover ? new Color(60, 95, 140, 235) : new Color(36, 56, 84, 230))
                                 : !avail ? new Color(24, 30, 40, 220) : new Color(28, 40, 56, 220);
                        pb.FillRect(rowR, bg);
                        pb.RectOutline(rowR, 1, selectable ? UiDraw.Accent : UiDraw.PanelBorder);

                        // col 1: icon (dimmed when not selectable)
                        var iconR = new Rectangle(rowR.X + 6, rowR.Y + (rowR.Height - 26) / 2, 26, 26);
                        UiDraw.Icon(pb, Ctx.Textures.Module(m.Id), iconR, m.Tint, !selectable);
                        pb.RectOutline(iconR, 1, new Color(50, 66, 92));

                        // col 2: name
                        Color nameC = selectable ? Color.White : !avail ? new Color(120, 130, 145) : UiDraw.TextDim;
                        sb.DrawString(f, m.Name, new Vector2(iconR.Right + 10, rowR.Y + 6), nameC);

                        // col 3 (right-aligned): "cost" = slots + mass, or the gating reason
                        if (!avail)
                        {
                            string node = Progression.TechTree.Node(Progression.TechTree.TechForModule(m.Name))?.Title ?? "R&D";
                            string need = $"needs {node}";
                            sb.DrawString(f, need, new Vector2(rowR.Right - f.MeasureString(need).X - 10, rowR.Y + 8), new Color(160, 135, 105));
                        }
                        else if (!fits)
                        {
                            string why = $"needs {m.SlotCost} free slot(s)";
                            sb.DrawString(f, why, new Vector2(rowR.Right - f.MeasureString(why).X - 10, rowR.Y + 8), new Color(255, 150, 90));
                        }
                        else
                        {
                            string slots = m.SlotCost > 1 ? $"{m.SlotCost} slots" : "1 slot";
                            string massS = $"{m.DryMass:0} kg";
                            float sw = f.MeasureString(slots).X * 0.8f, mw = f.MeasureString(massS).X * 0.8f;
                            UiDraw.SmallText(sb, f, slots, new Vector2(rowR.Right - 10 - sw, rowR.Y + 3), UiDraw.TextDim);
                            UiDraw.SmallText(sb, f, massS, new Vector2(rowR.Right - 10 - mw, rowR.Y + 16), UiDraw.TextDim);
                        }

                        if (hover)
                        {
                            UiDraw.ModuleTooltip(pb, sb, f, m, inp.MousePos, w, h);
                            if (inp.LeftClick) { entry.Modules.Add(m); _showModulePicker = false; }
                        }
                    }
                    yc += rowH;
                }
            }

            string footer = full ? "all slots full  -  [Esc] close"
                          : maxScroll > 0 ? "[Esc] close   -   scroll for more" : "[Esc] close";
            sb.DrawString(f, footer, new Vector2(px + 20, py + ph - 24), UiDraw.TextDim);
        }

        /// <summary>Living crew not yet assigned to any seat in the current design.</summary>
        private List<CrewMember> AvailableCrew()
        {
            var assigned = new HashSet<string>();
            foreach (var e in Stack) foreach (var n in e.CrewNames) assigned.Add(n);
            var list = new List<CrewMember>();
            foreach (var c in Ctx.State.Roster)
                if (c.Status == CrewStatus.Active && !assigned.Contains(c.Name)) list.Add(c);
            return list;
        }

        // ---------- "Assign Crew" overlay ----------
        private void DrawCrewPicker(Rendering.PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                                    Microsoft.Xna.Framework.Graphics.SpriteFont f, InputState inp, int w, int h)
        {
            if (_crewTarget < 0 || _crewTarget >= Stack.Count) { _showCrewPicker = false; return; }
            var entry = Stack[_crewTarget];
            var crew = AvailableCrew();
            bool full = entry.CrewNames.Count >= entry.SeatCount;

            pb.FillRect(0, 0, w, h, new Color(0, 0, 0, 170));
            const int pw = 480, rowH = 30, headerH = 56, footerH = 30;
            int contentH = Math.Max(rowH, crew.Count * rowH);
            int bodyH = Math.Min(contentH, h - 120 - headerH - footerH);
            int ph = headerH + bodyH + footerH;
            int px = w / 2 - pw / 2, py = Math.Max(20, h / 2 - ph / 2);
            UiDraw.Panel(pb, new Rectangle(px, py, pw, ph));
            sb.DrawString(Ctx.FontBig, "ASSIGN CREW", new Vector2(px + 20, py + 12), new Color(200, 215, 235));
            sb.DrawString(f, $"{entry.Def.Name}  ({entry.CrewNames.Count}/{entry.SeatCount})",
                          new Vector2(px + pw - 180, py + 18), UiDraw.Accent);

            int bodyTop = py + headerH;
            if (crew.Count == 0)
                sb.DrawString(f, "No available crew (all assigned or KIA).", new Vector2(px + 20, bodyTop + 6), UiDraw.TextDim);
            for (int i = 0; i < crew.Count; i++)
            {
                float ry = bodyTop + i * rowH;
                if (ry + rowH > bodyTop + bodyH) break;
                var rowR = new Rectangle(px + 16, (int)ry + 2, pw - 32, rowH - 4);
                bool hover = !full && rowR.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);
                pb.FillRect(rowR, hover ? new Color(60, 95, 140, 235) : new Color(36, 56, 84, 230));
                pb.RectOutline(rowR, 1, full ? UiDraw.PanelBorder : UiDraw.Accent);
                sb.DrawString(f, $"{crew[i].Name}  -  {crew[i].Role}", new Vector2(rowR.X + 10, rowR.Y + 6), full ? UiDraw.TextDim : Color.White);
                if (hover && inp.LeftClick) { entry.CrewNames.Add(crew[i].Name); _showCrewPicker = false; }
            }

            string footer = full ? "all seats full  -  [Esc] close" : "[Esc] close";
            sb.DrawString(f, footer, new Vector2(px + 20, py + ph - 24), UiDraw.TextDim);
        }

        // ---------- "Load Ship" design-library overlay ----------
        private void DrawLoadShip(Rendering.PrimitiveBatch pb, Microsoft.Xna.Framework.Graphics.SpriteBatch sb,
                                  Microsoft.Xna.Framework.Graphics.SpriteFont f, InputState inp, int w, int h)
        {
            var ships = ShipLibrary.List();

            pb.FillRect(0, 0, w, h, new Color(0, 0, 0, 170));
            const int pw = 480, rowH = 30, headerH = 56, footerH = 30;
            int contentH = Math.Max(rowH, ships.Count * rowH);
            int bodyH = Math.Min(contentH, h - 120 - headerH - footerH);
            int ph = headerH + bodyH + footerH;
            int px = w / 2 - pw / 2, py = Math.Max(20, h / 2 - ph / 2);
            UiDraw.Panel(pb, new Rectangle(px, py, pw, ph));
            sb.DrawString(Ctx.FontBig, "LOAD SHIP", new Vector2(px + 20, py + 12), new Color(200, 215, 235));
            sb.DrawString(f, $"{ships.Count} saved", new Vector2(px + pw - 110, py + 18), UiDraw.Accent);

            int bodyTop = py + headerH, bodyBot = bodyTop + bodyH;
            int maxScroll = Math.Max(0, contentH - bodyH);
            bool inPanel = inp.MousePos.X >= px && inp.MousePos.X <= px + pw && inp.MousePos.Y >= bodyTop && inp.MousePos.Y <= bodyBot;
            if (inPanel) _loadShipScroll -= inp.WheelDelta * 0.4f;
            _loadShipScroll = Math.Clamp(_loadShipScroll, 0, maxScroll);

            if (ships.Count == 0)
                sb.DrawString(f, "No saved ships. Use SAVE to store the current design.", new Vector2(px + 20, bodyTop + 6), UiDraw.TextDim);
            for (int i = 0; i < ships.Count; i++)
            {
                float ry = bodyTop + i * rowH - _loadShipScroll;
                if (ry < bodyTop || ry + rowH > bodyBot) continue;   // only fully-visible rows
                var rowR = new Rectangle(px + 16, (int)ry + 2, pw - 32, rowH - 4);
                bool hover = rowR.Contains((int)inp.MousePos.X, (int)inp.MousePos.Y);
                pb.FillRect(rowR, hover ? new Color(60, 95, 140, 235) : new Color(36, 56, 84, 230));
                pb.RectOutline(rowR, 1, UiDraw.Accent);
                sb.DrawString(f, ships[i], new Vector2(rowR.X + 10, rowR.Y + 6), Color.White);
                if (hover && inp.LeftClick)
                {
                    var d = ShipLibrary.Load(ships[i]);
                    if (d != null) { d.ApplyTo(Ctx.Design); _selected = -1; _held = null; }
                    _showLoadShip = false;
                }
            }

            sb.DrawString(f, maxScroll > 0 ? "[Esc] close   -   scroll for more" : "[Esc] close",
                          new Vector2(px + 20, py + ph - 24), UiDraw.TextDim);
        }
    }
}
