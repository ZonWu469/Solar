using System.Collections.Generic;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Solar.Parts;

namespace Solar.Rendering
{
    /// <summary>Maps a part/module <see cref="PartDef.Id"/> to its texture, loaded through the
    /// MonoGame content pipeline (Content/Textures/parts|modules/&lt;id&gt;.png registered in
    /// Content.mgcb). Ids with no compiled asset are simply absent, so renderers fall back to the
    /// procedural shapes. Lookups are O(1) and null-safe.</summary>
    public sealed class TextureStore
    {
        private readonly Dictionary<string, Texture2D> _parts = new();
        private readonly Dictionary<string, Texture2D> _modules = new();
        private readonly Dictionary<string, Texture2D> _bodies = new();
        private readonly Dictionary<string, Texture2D> _ui = new();
        private readonly Dictionary<string, Texture2D> _player = new();

        // EVA astronaut sprites by crew role (Content/Player/<role>.png). Absent = procedural fallback.
        private static readonly string[] PlayerIds = { "pilot", "engineer", "scientist" };

        // UI background textures (no catalog drives these; ids match Content/Textures/UI/*.png).
        private static readonly string[] UiIds =
        {
            "gameplay_vessel_panel", "gameplay_warp_panel", "gameplay_modules_panel",
            "gameplay_thrust_panel", "gameplay_accel_panel", "gameplay_progressbar",
            "quad_button",
            "icon_stability", "icon_prograde", "icon_retrograde", "icon_radialin",
            "icon_radialout", "icon_target", "icon_antitarget", "icon_relretro", "icon_maneuver",
            // map-view orbit-planner markers (procedural fallback when a PNG is absent)
            "marker_periapsis", "marker_apoapsis", "marker_soi_exit", "marker_soi_entry",
            "marker_impact", "marker_atmospheric_entry", "marker_closest_approach",
            "marker_you_closest_approach", "marker_target_closest_approach", "marker_target",
        };

        public TextureStore(ContentManager content)
        {
            foreach (var p in PartCatalog.All)
            {
                TryLoad(content, _parts, "Textures/parts/", p.Id);
                TryLoad(content, _parts, "Textures/parts/", p.Id + "-open");   // deployed-chute canopy art, where authored
            }
            foreach (var m in ModuleCatalog.All) TryLoad(content, _modules, "Textures/modules/", m.Id);
            foreach (var b in Solar.Physics.BodyCatalog.All) TryLoad(content, _bodies, "Textures/bodies/", b.Id);
            foreach (var id in UiIds) TryLoad(content, _ui, "Textures/UI/", id);
            foreach (var id in PlayerIds) TryLoad(content, _player, "Player/", id);
        }

        private static void TryLoad(ContentManager content, Dictionary<string, Texture2D> map, string dir, string id)
        {
            if (string.IsNullOrEmpty(id) || map.ContainsKey(id)) return;
            try { map[id] = content.Load<Texture2D>(dir + id); }
            catch { /* no compiled texture for this id: keep using the procedural shape */ }
        }

        public Texture2D Part(string id) => id != null && _parts.TryGetValue(id, out var t) ? t : null;
        public Texture2D Module(string id) => id != null && _modules.TryGetValue(id, out var t) ? t : null;
        public Texture2D Body(string id) => id != null && _bodies.TryGetValue(id, out var t) ? t : null;
        public Texture2D Ui(string id) => id != null && _ui.TryGetValue(id, out var t) ? t : null;
        /// <summary>EVA astronaut sprite for a crew role ("pilot"/"engineer"/"scientist"), or null if no art.</summary>
        public Texture2D Player(string role) => role != null && _player.TryGetValue(role, out var t) ? t : null;
    }
}
