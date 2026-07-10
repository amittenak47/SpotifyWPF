namespace SpotifyWPF.Model
{
    /// <summary>User-toggleable eye candy. Everything defaults to off (plain black background).</summary>
    public class VisualEffectsSettings
    {
        public bool FractalBackgroundEnabled { get; set; }

        public static VisualEffectsSettings CreateDefaults()
        {
            return new VisualEffectsSettings
            {
                FractalBackgroundEnabled = false
            };
        }
    }
}
