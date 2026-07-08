using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using SpotifyWPF.Model;

namespace SpotifyWPF.ViewModel.Component
{
    /// <summary>
    /// Reusable activity log backing <see cref="View.Component.ActivityLogView"/>:
    /// Default/Verbose filtering, a capped message list, and copy commands.
    /// Owned by page view models via composition (one instance per page).
    /// </summary>
    public class ActivityLogViewModel : ViewModelBase
    {
        private readonly List<LogEntry> _allLogMessages = new List<LogEntry>();

        private string _selectedLogFilter = "Default";

        private bool _showFilter = true;

        private bool _newestFirst;

        private int _maxEntries = 200;

        private FontFamily _fontFamily = new FontFamily("Consolas");

        private double _fontSize = 11;

        public ActivityLogViewModel()
        {
            CopySelectedLogMessagesCommand = new RelayCommand<IList>(CopySelectedLogMessages);
            CopyAllLogMessagesCommand = new RelayCommand(CopyAllLogMessages);
        }

        public ObservableCollection<string> LogMessages { get; } = new ObservableCollection<string>();

        public ObservableCollection<string> LogFilterOptions { get; } = new ObservableCollection<string>
        {
            "Default",
            "Verbose"
        };

        public string SelectedLogFilter
        {
            get => _selectedLogFilter;
            set
            {
                if (Set(ref _selectedLogFilter, value))
                    RefreshVisibleLogMessages();
            }
        }

        /// <summary>Shows the Default/Verbose filter row above the log.</summary>
        public bool ShowFilter
        {
            get => _showFilter;
            set => Set(ref _showFilter, value);
        }

        /// <summary>
        /// When true, new entries are inserted at the top. Default false:
        /// oldest first, appended at the bottom (Playlists page behavior).
        /// </summary>
        public bool NewestFirst
        {
            get => _newestFirst;
            set
            {
                if (Set(ref _newestFirst, value))
                    RefreshVisibleLogMessages();
            }
        }

        /// <summary>Cap on retained entries; oldest entries are dropped beyond it.</summary>
        public int MaxEntries
        {
            get => _maxEntries;
            set => Set(ref _maxEntries, Math.Max(1, value));
        }

        public FontFamily FontFamily
        {
            get => _fontFamily;
            set => Set(ref _fontFamily, value);
        }

        public double FontSize
        {
            get => _fontSize;
            set => Set(ref _fontSize, value);
        }

        public RelayCommand<IList> CopySelectedLogMessagesCommand { get; }

        public RelayCommand CopyAllLogMessagesCommand { get; }

        /// <summary>
        /// Appends an entry. Safe to call from any thread; the UI collection is
        /// updated via the dispatcher.
        /// </summary>
        public void Log(string message, bool verbose = false)
        {
            var logEntry = new LogEntry(DateTime.Now, message, verbose);
            Console.WriteLine(logEntry.FormattedMessage);

            Application.Current.Dispatcher.BeginInvoke((Action) (() =>
            {
                _allLogMessages.Add(logEntry);

                while (_allLogMessages.Count > MaxEntries)
                    _allLogMessages.RemoveAt(0);

                if (ShouldShowLog(logEntry))
                {
                    if (NewestFirst)
                        LogMessages.Insert(0, logEntry.FormattedMessage);
                    else
                        LogMessages.Add(logEntry.FormattedMessage);
                }

                while (LogMessages.Count > MaxEntries)
                    LogMessages.RemoveAt(NewestFirst ? LogMessages.Count - 1 : 0);
            }));
        }

        private void RefreshVisibleLogMessages()
        {
            LogMessages.Clear();

            var visibleEntries = _allLogMessages.Where(ShouldShowLog);

            if (NewestFirst)
                visibleEntries = visibleEntries.Reverse();

            foreach (var logEntry in visibleEntries)
                LogMessages.Add(logEntry.FormattedMessage);
        }

        private bool ShouldShowLog(LogEntry logEntry)
        {
            return SelectedLogFilter == "Verbose" || !logEntry.IsVerbose;
        }

        private void CopySelectedLogMessages(IList selectedMessages)
        {
            var messages = selectedMessages?.Cast<string>().ToList();

            if (messages == null || !messages.Any()) return;

            Clipboard.SetText(string.Join(Environment.NewLine, messages));
        }

        private void CopyAllLogMessages()
        {
            if (!LogMessages.Any()) return;

            Clipboard.SetText(string.Join(Environment.NewLine, LogMessages));
        }
    }
}
