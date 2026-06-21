using System.IO;
using System.Text.Json;

namespace Solar.Core
{
    /// <summary>Global gameplay tunables for the survival hazards (module wear/failure, radiation, contagion)
    /// and life support. The authoritative source is Content/balance.json, loaded by <see cref="Load"/> at
    /// startup. Mirrors the data-driven pattern of <see cref="Solar.Parts.ModuleCatalog"/>, but every field
    /// has an in-code default: a missing file or a field omitted from the JSON falls back to the value here,
    /// so the game is fully playable with no balance.json at all. Rates are per-second unless noted; the
    /// JSON authors them as readable "...Hours" durations where that is clearer.</summary>
    public static class Balance
    {
        // ----- module malfunctions & wear -----
        public static double WearPerSec        = 1.0 / (5840 * 3600);  // a running module is fully worn after ~8 months
        public static double BreakBaseRate     = 1.0 / (1000 * 3600);  // break chance/s at zero wear, reliability 1 (~6 weeks)
        public static double RepairPerSec      = 1.0 / (2 * 3600);     // an engineer repairs a broken module in ~2 h

        // ----- radiation -----
        public static double RadDeathDose      = 1000.0;               // accumulated dose that kills a crew member
        public static double RadDecayPerSec    = 1000.0 / (18 * 3600); // dose clears outside any belt (~18 h to fully clear)

        // ----- biological contagion -----
        public static double InfectBaseRate    = 1.0 / (8760 * 3600);  // base infection chance/s per healthy crew (~yearly)
        public static double IllnessGrowPerSec = 1.0 / (72 * 3600);    // untreated sickness worsens over ~72 h
        public static double IllnessDeathRate  = 1.0 / (24 * 3600);    // death chance/s once illness is terminal

        // ----- life support (per-crew consumption, units/s) -----
        public static double OxygenPerCrew = 0.5;
        public static double WaterPerCrew  = 0.2;
        public static double FoodPerCrew   = 0.1;
        public static double LsDeathTime   = 21600;                    // 6 h of an empty resource kills one crew member

        private static string FilePath => Path.Combine(System.AppContext.BaseDirectory, "Content", "balance.json");

        /// <summary>Override the defaults above from Content/balance.json if present. Each field is nullable in
        /// the DTO, so a partial or absent file simply leaves the in-code default in place. An unreadable or
        /// corrupt file is ignored (defaults stand). Durations in the JSON are in hours and converted here.</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var d = JsonSerializer.Deserialize<BalanceDto>(File.ReadAllText(FilePath));
                if (d == null) return;

                if (d.WearHours is { } wh && wh > 0)         WearPerSec = 1.0 / (wh * 3600);
                if (d.BreakHours is { } bh && bh > 0)        BreakBaseRate = 1.0 / (bh * 3600);
                if (d.RepairHours is { } rh && rh > 0)       RepairPerSec = 1.0 / (rh * 3600);
                if (d.RadDeathDose is { } rd && rd > 0)      RadDeathDose = rd;
                if (d.RadDecayHours is { } rdh && rdh > 0)   RadDecayPerSec = RadDeathDose / (rdh * 3600);
                if (d.InfectHours is { } ih && ih > 0)       InfectBaseRate = 1.0 / (ih * 3600);
                if (d.IllnessGrowHours is { } igh && igh > 0) IllnessGrowPerSec = 1.0 / (igh * 3600);
                if (d.IllnessDeathHours is { } idh && idh > 0) IllnessDeathRate = 1.0 / (idh * 3600);
                if (d.OxygenPerCrew is { } o && o >= 0)      OxygenPerCrew = o;
                if (d.WaterPerCrew is { } w && w >= 0)       WaterPerCrew = w;
                if (d.FoodPerCrew is { } f && f >= 0)        FoodPerCrew = f;
                if (d.LsDeathHours is { } lsh && lsh > 0)    LsDeathTime = lsh * 3600;
            }
            catch { /* unreadable/corrupt balance.json: keep the in-code defaults */ }
        }

        /// <summary>JSON mirror with nullable fields so any subset can be authored; durations are in hours.</summary>
        public sealed class BalanceDto
        {
            public double? WearHours { get; set; }
            public double? BreakHours { get; set; }
            public double? RepairHours { get; set; }
            public double? RadDeathDose { get; set; }
            public double? RadDecayHours { get; set; }
            public double? InfectHours { get; set; }
            public double? IllnessGrowHours { get; set; }
            public double? IllnessDeathHours { get; set; }
            public double? OxygenPerCrew { get; set; }
            public double? WaterPerCrew { get; set; }
            public double? FoodPerCrew { get; set; }
            public double? LsDeathHours { get; set; }
        }
    }
}
