using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solar.Core
{
    /// <summary>Reads/writes editor ship designs as named JSON files under ships/ next to the exe, so
    /// layouts can be saved in the VAB and reloaded later. Stores a <see cref="DesignState"/> (the same
    /// serializable form the savegame embeds), so radial mounts, modules, and crew round-trip exactly.</summary>
    public static class ShipLibrary
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static string Dir => Path.Combine(AppContext.BaseDirectory, "ships");

        private static string SafeName(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Ship" : name.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        public static string PathFor(string name) => Path.Combine(Dir, SafeName(name) + ".json");

        /// <summary>Names of saved designs (file stems), newest first.</summary>
        public static List<string> List()
        {
            var names = new List<string>();
            try
            {
                if (Directory.Exists(Dir))
                {
                    var files = new List<string>(Directory.GetFiles(Dir, "*.json"));
                    files.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                    foreach (var f in files) names.Add(Path.GetFileNameWithoutExtension(f));
                }
            }
            catch { /* unreadable ships dir: report none */ }
            return names;
        }

        public static void Save(DesignState design, string name)
        {
            if (design == null) return;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFor(name), JsonSerializer.Serialize(design, JsonOpts));
        }

        public static DesignState Load(string name)
        {
            try
            {
                var path = PathFor(name);
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<DesignState>(File.ReadAllText(path), JsonOpts);
            }
            catch { /* corrupt design file: fall through to null */ }
            return null;
        }
    }
}
