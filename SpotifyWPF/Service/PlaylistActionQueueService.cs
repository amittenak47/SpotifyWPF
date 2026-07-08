using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpotifyWPF.Model;

namespace SpotifyWPF.Service
{
    /// <summary>
    /// All members must be used from the UI thread: the queue collection is
    /// bound to the view, and the execution loop's awaits resume on the
    /// dispatcher's synchronization context.
    /// </summary>
    public class PlaylistActionQueueService : IPlaylistActionQueueService
    {
        public event Action<string, bool> LogMessage;

        public event Action StateChanged;

        public ObservableCollection<QueuedPlaylistAction> QueuedActions { get; } = new ObservableCollection<QueuedPlaylistAction>();

        public bool IsExecuting { get; private set; }

        public bool IsPaused { get; private set; }

        public void Enqueue(QueuedPlaylistAction action)
        {
            QueuedActions.Add(action);
            Log($"Enqueued action: {action.DisplayName}");
            RaiseStateChanged();
        }

        public void Remove(QueuedPlaylistAction action)
        {
            if (action == null) return;

            QueuedActions.Remove(action);
            Log($"Removed queued action: {action.DisplayName}");
            RaiseStateChanged();
        }

        public void RemoveRange(IReadOnlyList<QueuedPlaylistAction> actions)
        {
            if (actions == null || !actions.Any()) return;

            foreach (var action in actions)
                QueuedActions.Remove(action);

            Log($"Removed {actions.Count} queued action(s).");
            RaiseStateChanged();
        }

        public void RemoveDetail(QueuedPlaylistAction action, QueuedActionDetailItem detail)
        {
            if (action == null || detail == null || !detail.CanRemove) return;

            action.DetailItems.Remove(detail);

            if (!string.IsNullOrWhiteSpace(detail.PlaylistId))
                action.PlaylistIds.Remove(detail.PlaylistId);

            if (!action.DetailItems.Any())
            {
                Remove(action);
                return;
            }

            action.RefreshDisplayName();
            Log($"Removed playlist from queued action: {detail.DisplayName}");
            RaiseStateChanged();
        }

        public QueuedPlaylistAction FindActionForDetail(QueuedActionDetailItem detail)
        {
            return QueuedActions.FirstOrDefault(action => action.DetailItems.Contains(detail));
        }

        public void Clear()
        {
            QueuedActions.Clear();
            Log("Cleared queued actions.");
            RaiseStateChanged();
        }

        public void Pause()
        {
            IsPaused = true;
            RaiseStateChanged();
        }

        public void Resume()
        {
            IsPaused = false;
            RaiseStateChanged();
        }

        public async Task ExecuteAsync(Func<QueuedPlaylistAction, CancellationToken, Task> executor, CancellationToken cancellationToken)
        {
            if (!QueuedActions.Any()) return;

            IsExecuting = true;
            IsPaused = false;

            try
            {
                while (QueuedActions.Any())
                {
                    await WaitWhilePausedAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var action = QueuedActions[0];
                    QueuedActions.RemoveAt(0);
                    RaiseStateChanged();

                    await executor(action, cancellationToken);
                }
            }
            finally
            {
                IsExecuting = false;
                IsPaused = false;
                RaiseStateChanged();
            }
        }

        private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
        {
            while (IsPaused)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(200, cancellationToken);
            }
        }

        private void Log(string message, bool verbose = false)
        {
            LogMessage?.Invoke(message, verbose);
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke();
        }
    }
}
