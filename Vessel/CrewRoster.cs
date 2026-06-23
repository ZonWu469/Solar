using System.Collections.Generic;
using Solar.Core;

namespace Solar.Vessels
{
    /// <summary>The player's finite crew pool. A save begins with exactly <see cref="PoolSize"/> named
    /// kerbals; deaths (KIA) stay counted in the pool and are never replaced, so the roster only ever
    /// shrinks ("100 crew to spend"). Generation and the load-time top-up both live here so the pool
    /// size has a single source of truth.</summary>
    public static class CrewRoster
    {
        /// <summary>How many crew a save commands in total (Active + KIA combined).</summary>
        public const int PoolSize = 100;

        /// <summary>The canonical four, in order, so every save opens with the familiar faces.</summary>
        private static readonly (string Name, CrewRole Role)[] Canonical =
        {
            ("Jeb Kerman", CrewRole.Pilot),
            ("Bill Kerman", CrewRole.Engineer),
            ("Bob Kerman", CrewRole.Scientist),
            ("Val Kerman", CrewRole.Pilot),
        };

        /// <summary>First-name pool for generated kerbals; exhausted entries get a roman-numeral suffix.</summary>
        private static readonly string[] FirstNames =
        {
            "Gus", "Ada", "Lin", "Mac", "Ned", "Pip", "Rae", "Sven", "Tig", "Uma",
            "Wes", "Zara", "Cal", "Dex", "Eli", "Fenn", "Gil", "Hux", "Ivo", "Jax",
            "Kip", "Lor", "Moe", "Nyx", "Obo", "Pax", "Quin", "Rip", "Sol", "Tav",
            "Ulf", "Vlad", "Wynn", "Xan", "Yuri", "Zeb", "Arn", "Bex", "Cob", "Dov",
        };

        /// <summary>A fresh full pool of <see cref="PoolSize"/> unique, role-balanced, Active crew.</summary>
        public static List<CrewMember> NewPool()
        {
            var roster = new List<CrewMember>();
            var taken = new HashSet<string>();
            foreach (var (name, role) in Canonical)
            {
                roster.Add(new CrewMember(name, role));
                taken.Add(name);
            }
            int i = roster.Count;
            while (roster.Count < PoolSize)
            {
                roster.Add(new CrewMember(UniqueName(taken, i), BalancedRole(i)));
                i++;
            }
            return roster;
        }

        /// <summary>Bring an existing save's roster up to <see cref="PoolSize"/> by adding Active crew,
        /// preserving every current member (including KIA). Idempotent: once the roster holds
        /// <see cref="PoolSize"/> entries it adds nothing, so deaths permanently shrink the available pool.
        /// Returns how many crew were added.</summary>
        public static int TopUp(GameState gs)
        {
            if (gs == null) return 0;
            gs.Roster ??= new List<CrewMember>();
            var taken = new HashSet<string>();
            foreach (var c in gs.Roster) taken.Add(c.Name);

            int added = 0;
            int i = gs.Roster.Count;
            while (gs.Roster.Count < PoolSize)
            {
                gs.Roster.Add(new CrewMember(UniqueName(taken, i), BalancedRole(i)));
                i++;
                added++;
            }
            return added;
        }

        /// <summary>Round-robin Pilot/Engineer/Scientist so the pool is evenly skilled.</summary>
        private static CrewRole BalancedRole(int index) => (CrewRole)(index % 3);

        /// <summary>A "&lt;First&gt; Kerman" name not in <paramref name="taken"/>, suffixed if needed.
        /// Adds the chosen name to <paramref name="taken"/>.</summary>
        private static string UniqueName(HashSet<string> taken, int index)
        {
            for (int pass = 0; ; pass++)
            {
                string first = FirstNames[(index + pass) % FirstNames.Length];
                int suffix = (index + pass) / FirstNames.Length;
                string name = suffix == 0 ? $"{first} Kerman" : $"{first} Kerman {Roman(suffix + 1)}";
                if (taken.Add(name)) return name;
            }
        }

        /// <summary>Small roman numerals for name suffixes (II, III, ...); falls back to plain digits.</summary>
        private static string Roman(int n)
        {
            string[] r = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };
            return n >= 0 && n < r.Length ? r[n] : n.ToString();
        }
    }
}
