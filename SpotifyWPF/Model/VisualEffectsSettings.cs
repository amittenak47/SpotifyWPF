namespace SpotifyWPF.Model
{
    /// <summary>User-toggleable eye candy. Everything defaults to off (plain black background).</summary>
    public class VisualEffectsSettings
    {
        public bool FractalBackgroundEnabled { get; set; }

        /// <summary>When true, status/HUD text can appear in a hover box under the title.</summary>
        public bool ShowStatusOverlay { get; set; }

        /// <summary>
        /// Outer-ring equalizer look: <c>bars</c> (Winamp cascading) or <c>wave-ring</c>
        /// (soft spectrum wave envelope).
        /// </summary>
        public string EqualizerPreset { get; set; } = "bars";

        public static VisualEffectsSettings CreateDefaults()
        {
            return new VisualEffectsSettings
            {
                FractalBackgroundEnabled = false,
                ShowStatusOverlay = false,
                EqualizerPreset = "bars"
            };
        }
    }
}
