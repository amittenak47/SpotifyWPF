namespace SpotifyWPF.Service.Prediction
{
    /// <summary>
    /// Seam through which an external controller steers the beat walk, without the navigator
    /// knowing anything about audio energy. Keeps <see cref="BeatNavigator"/> deterministic and
    /// unit-testable (pass <see cref="NullHopBias.Instance"/>) while the controller and its state
    /// live in <see cref="LoopController"/>.
    /// </summary>
    public interface IHopBias
    {
        /// <summary>Controller output u, clamped to [-1, 1]. Positive = "we need more energy".</summary>
        double Output { get; }

        /// <summary>
        /// Multiplier (&gt;= 0, 1 = neutral) applied to the branch chance at this beat. Applied at
        /// read time only — never written back into the navigator's ramp accumulator.
        /// </summary>
        double BranchChanceMultiplier(int beatIndex);

        /// <summary>
        /// Hard veto on hopping at this beat (buildup gate). MUST be a pure function of precomputed
        /// analysis, with no controller feedback state: the navigator evaluates it at simulated
        /// future beats during <see cref="BeatNavigator.PlanNextJump"/>, where feedback state does
        /// not exist yet.
        /// </summary>
        bool SuppressHops(int beatIndex);

        /// <summary>
        /// Signed [-1, 1] desirability of landing on <paramref name="toBeatIndex"/> given the
        /// current target. Multiplied by u in the Softmax score, so it only steers when the
        /// controller actually wants something.
        /// </summary>
        double EnergyScore(int fromBeatIndex, int toBeatIndex);
    }

    /// <summary>Neutral bias: the navigator behaves exactly as it did before energy control existed.</summary>
    public sealed class NullHopBias : IHopBias
    {
        public static readonly NullHopBias Instance = new NullHopBias();

        private NullHopBias()
        {
        }

        public double Output => 0;

        public double BranchChanceMultiplier(int beatIndex) => 1;

        public bool SuppressHops(int beatIndex) => false;

        public double EnergyScore(int fromBeatIndex, int toBeatIndex) => 0;
    }
}
