using System;
using System.ComponentModel;
using System.Windows;
using GalaSoft.MvvmLight.Messaging;
using SpotifyWPF.View.Extension;
using SpotifyWPF.ViewModel;

namespace SpotifyWPF.View
{
    public partial class MainWindow
    {
        private Rect? _savedBounds;
        private WindowState _savedWindowState = WindowState.Normal;
        private bool _savedShowInTaskbar = true;
        private MiniPlayerWindow _miniPlayerWindow;

        public MainWindow()
        {
            InitializeComponent();
            DarkWindowChrome.Apply(this);
            Messenger.Default.Register<bool>(this, MessageType.MiniPlayerModeChanged, ApplyMiniPlayerMode);
            Closing += OnMainWindowClosing;
            Closed += (_, __) => Messenger.Default.Unregister(this);
        }

        public void ParkWebPlaybackView(UIElement view)
        {
            if (view == null)
                return;

            WebPlaybackHiddenHost.Child = view;
        }

        public void UnparkWebPlaybackView(UIElement view)
        {
            if (view != null && ReferenceEquals(WebPlaybackHiddenHost.Child, view))
                WebPlaybackHiddenHost.Child = null;
        }

        private void OnMainWindowClosing(object sender, CancelEventArgs e)
        {
            if (_miniPlayerWindow == null)
                return;

            _miniPlayerWindow.Closed -= OnMiniPlayerWindowClosed;
            _miniPlayerWindow.Close();
            _miniPlayerWindow = null;
        }

        private void OnMiniPlayerWindowClosed(object sender, EventArgs e)
        {
            if (_miniPlayerWindow != null)
                _miniPlayerWindow.Closed -= OnMiniPlayerWindowClosed;

            _miniPlayerWindow = null;

            if (Application.Current == null || Application.Current.Dispatcher.HasShutdownStarted)
                return;

            // Mini player was the only visible window; hidden MainWindow would keep the process alive.
            Application.Current.Shutdown();
        }

        private void ApplyMiniPlayerMode(bool enabled)
        {
            if (Application.Current?.Dispatcher.HasShutdownStarted == true)
                return;

            if (enabled)
            {
                _savedBounds = new Rect(Left, Top, Width, Height);
                _savedWindowState = WindowState;
                _savedShowInTaskbar = ShowInTaskbar;

                if (WindowState != WindowState.Normal)
                    WindowState = WindowState.Normal;

                if (_miniPlayerWindow == null)
                {
                    _miniPlayerWindow = new MiniPlayerWindow { Owner = this };
                    _miniPlayerWindow.Closed += OnMiniPlayerWindowClosed;
                }

                var centerX = Left + Width / 2;
                var centerY = Top + Height / 2;
                _miniPlayerWindow.Left = centerX - _miniPlayerWindow.Width / 2;
                _miniPlayerWindow.Top = centerY - _miniPlayerWindow.Height / 2;

                ShowInTaskbar = false;
                _miniPlayerWindow.Show();
                Hide();
                return;
            }

            _miniPlayerWindow?.Hide();

            ShowInTaskbar = _savedShowInTaskbar;
            Show();

            if (_savedBounds.HasValue && _savedBounds.Value.Width > 0 && _savedBounds.Value.Height > 0)
            {
                Left = _savedBounds.Value.X;
                Top = _savedBounds.Value.Y;
                Width = _savedBounds.Value.Width;
                Height = _savedBounds.Value.Height;
            }

            WindowState = _savedWindowState;
            Activate();
            _savedBounds = null;
        }
    }
}
