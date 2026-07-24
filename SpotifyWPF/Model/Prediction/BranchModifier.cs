using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SpotifyWPF.Model.Prediction
{
    /// <summary>
    /// Effect stack attached to a locked branch. Applied only when playback source is Local WAV —
    /// Spotify streaming never runs these modifiers.
    /// </summary>
    public class BranchModifier
    {
        /// <summary>Identity of the lock this modifier stretches from.</summary>
        [JsonPropertyName("fromBeatIndex")]
        public int FromBeatIndex { get; set; }

        [JsonPropertyName("toBeatIndex")]
        public int ToBeatIndex { get; set; }

        /// <summary>"none" | "supercharge" | "turbocharge" — UI intensity preset.</summary>
        [JsonPropertyName("tier")]
        public string Tier { get; set; } = ModifierTiers.Supercharge;

        /// <summary>How far the ring chord is stretched outward (0–1 visual + mix wetness).</summary>
        [JsonPropertyName("stretch")]
        public double Stretch { get; set; } = 0.35;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>Simple 3-band EQ gains in dB (low / mid / high).</summary>
        [JsonPropertyName("eqLowDb")]
        public double EqLowDb { get; set; }

        [JsonPropertyName("eqMidDb")]
        public double EqMidDb { get; set; }

        [JsonPropertyName("eqHighDb")]
        public double EqHighDb { get; set; }

        /// <summary>Extra gain applied while the destination region plays after a hop (dB).</summary>
        [JsonPropertyName("gainDb")]
        public double GainDb { get; set; }

        /// <summary>Optional relative paths under Prediction/overlays for layered SFX.</summary>
        [JsonPropertyName("overlayPaths")]
        public List<string> OverlayPaths { get; set; } = new List<string>();

        /// <summary>Drive amount for turbocharge-style saturation (0–1).</summary>
        [JsonPropertyName("drive")]
        public double Drive { get; set; }

        public static BranchModifier CreatePreset(int fromBeat, int toBeat, string tier)
        {
            var mod = new BranchModifier
            {
                FromBeatIndex = fromBeat,
                ToBeatIndex = toBeat,
                Tier = tier ?? ModifierTiers.Supercharge,
                Enabled = true
            };

            if (string.Equals(tier, ModifierTiers.Turbocharge, System.StringComparison.OrdinalIgnoreCase))
            {
                mod.Stretch = 0.7;
                mod.EqLowDb = 3;
                mod.EqMidDb = 1;
                mod.EqHighDb = 4;
                mod.GainDb = 1.5;
                mod.Drive = 0.35;
            }
            else
            {
                mod.Stretch = 0.4;
                mod.EqLowDb = 2;
                mod.EqMidDb = 0;
                mod.EqHighDb = 2;
                mod.GainDb = 0.75;
                mod.Drive = 0.15;
            }

            return mod;
        }
    }

    public static class ModifierTiers
    {
        public const string None = "none";
        public const string Supercharge = "supercharge";
        public const string Turbocharge = "turbocharge";
    }
}
