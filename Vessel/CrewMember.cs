namespace Solar.Vessels
{
    /// <summary>A crew member's specialty. Roles are tracked now; gameplay bonuses (Pilot SAS,
    /// Engineer repair, Scientist science multiplier) are an easy follow-on.</summary>
    public enum CrewRole { Pilot, Engineer, Scientist }

    public enum CrewStatus { Active, KIA }

    /// <summary>A named crew member. The same instance is shared between the savegame roster, the
    /// editor assignment, and the in-flight <see cref="Solar.Parts.Part.Crew"/> list, so mutating
    /// <see cref="Status"/> (e.g. on death) is visible everywhere.</summary>
    public sealed class CrewMember
    {
        public string Name;
        public CrewRole Role;
        public CrewStatus Status = CrewStatus.Active;
        public double RadDose;   // accumulated radiation dose (0 = clean); decays when clear/shielded
        public double Illness;   // 0..1 sickness; lowers effective skill and can be fatal (0 = healthy)

        /// <summary>Transient (never saved): the net radiation dose rate (units/s) this crew member is
        /// taking right now, refreshed each <see cref="Threats.Tick"/>. Drives the HUD "time to death"
        /// readout; 0 when fully shielded or recovering.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double RadDoseRate;

        public CrewMember() { }
        public CrewMember(string name, CrewRole role) { Name = name; Role = role; }
    }
}
