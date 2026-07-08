using System;
using System.Threading;
using System.Threading.Tasks;

namespace SpotifyWPF.Service
{
    /// <summary>
    /// Serializes outgoing Spotify requests and enforces a configurable minimum
    /// delay between them. Shared by playlist paging and deletion so their
    /// requests are spaced against each other, not just within one flow.
    /// </summary>
    public interface IRequestSpacingService
    {
        /// <summary>Minimum milliseconds between requests. Clamped to >= 0.</summary>
        int SpacingMilliseconds { get; set; }

        /// <summary>
        /// Waits for the spacing slot and delay, then releases the slot before
        /// returning (the caller issues its request afterwards).
        /// </summary>
        Task WaitForSpacingAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Waits for the spacing slot and delay, runs <paramref name="request"/>
        /// while still holding the slot, then releases it.
        /// </summary>
        Task RunSpacedAsync(Func<Task> request, CancellationToken cancellationToken);
    }
}
