namespace Solar.Physics
{
    /// <summary>A natural, dome-like sheltered spot on a body's surface. Landing a crewed vessel inside a
    /// niche's footprint halves the crew's life-support consumption (see <see cref="Solar.Vessels.Vessel.UpdateResources"/>).
    /// Niches are derived deterministically from the body (placed on terrain flat plains), so — like
    /// <see cref="Terrain"/> — they regenerate identically each load and carry no save data.</summary>
    public sealed class LivableNiche
    {
        public double CenterAngle;  // body-local longitude (rad) of the niche centre
        public double HalfWidth;    // angular half-extent (rad); the footprint is [Center-Half, Center+Half]
        public string Name;

        public LivableNiche(double centerAngle, double halfWidth, string name)
        {
            CenterAngle = centerAngle;
            HalfWidth = halfWidth;
            Name = name;
        }
    }
}
