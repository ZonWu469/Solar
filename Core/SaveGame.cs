using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solar.Core
{
    /// <summary>Reads/writes <see cref="GameState"/> as named JSON slots under saves/ next to the exe.</summary>
    public static class SaveGame
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            IncludeFields = true,                 // Vec2d/OrbitalElements/PartState use public fields
            Converters = { new JsonStringEnumConverter() },
        };

        public static string Dir => Path.Combine(AppContext.BaseDirectory, "saves");

        private static string SafeName(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "game" : name.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        public static string PathFor(string name) => Path.Combine(Dir, SafeName(name) + ".json");

        /// <summary>Names of existing saves (file stems), newest first.</summary>
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
            catch { /* unreadable saves dir: report none */ }
            return names;
        }

        public static void Save(GameState state)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFor(state.Name), JsonSerializer.Serialize(state, JsonOpts));
        }

        public static GameState Load(string name)
        {
            try
            {
                var path = PathFor(name);
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<GameState>(File.ReadAllText(path), JsonOpts);
            }
            catch { /* corrupt save: fall through to null */ }
            return null;
        }
    }
}
