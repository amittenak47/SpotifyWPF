using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using SpotifyWPF.Model;

namespace SpotifyWPF.Service
{
    /// <summary>
    /// Owns the deferred playlist action queue: pending actions, pause/resume
    /// state, and the sequential execution loop. The Playlists view model binds
    /// <see cref="QueuedActions"/> directly and supplies the executor callback
    /// that performs each action.
    /// </summary>
    public interface IPlaylistActionQueueService
    {
        /// <summary>Raised for queue activity reporting: (message, isVerbose).</summary>
        event Action<string, bool> LogMessage;

        /// <summary>Raised whenever queue contents or execution state change.</summary>
        event Action StateChanged;

        ObservableCollection<QueuedPlaylistAction> QueuedActions { get; }

        bool IsExecuting { get; }

        bool IsPaused { get; }

        void Enqueue(QueuedPlaylistAction action);

        void Remove(QueuedPlaylistAction action);

        void RemoveRange(IReadOnlyList<QueuedPlaylistAction> actions);

        void RemoveDetail(QueuedPlaylistAction action, QueuedActionDetailItem detail);

        QueuedPlaylistAction FindActionForDetail(QueuedActionDetailItem detail);

        void Clear();

        void Pause();

        void Resume();

        /// <summary>
        /// Runs queued actions front-to-back until the queue is empty, honoring
        /// pause/resume between actions. <paramref name="executor"/> performs a
        /// single action; its exceptions abort the loop and propagate.
        /// </summary>
        Task ExecuteAsync(Func<QueuedPlaylistAction, CancellationToken, Task> executor, CancellationToken cancellationToken);
    }
}
