using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Animation;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.View.Page
{
    /// <summary>
    /// Choreographs the login page fades from <see cref="LoginPageViewModel.Phase"/> changes:
    /// form out / loading in on login, loading slowly out on success, and the reverse fade on
    /// failure. All timing runs as WPF <see cref="DoubleAnimation"/>s on Opacity — nothing blocks
    /// the UI thread — and the view reports the final fade back to the view model so navigation
    /// happens exactly when the overlay has finished disappearing (the VM keeps a timeout fallback).
    /// </summary>
    public class LoginTransitionController
    {
        private static readonly TimeSpan FormFadeOut = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan FormFadeIn = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan LoadingFadeIn = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan LoadingFadeInDelay = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan LoadingFadeOutFast = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan LoadingFadeOutSlow = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan StatusTextCrossFade = TimeSpan.FromMilliseconds(250);

        private readonly FrameworkElement _formPanel;
        private readonly FrameworkElement _loadingOverlay;
        private readonly FrameworkElement _statusText;

        private LoginPageViewModel _viewModel;

        public LoginTransitionController(FrameworkElement view, FrameworkElement formPanel,
            FrameworkElement loadingOverlay, FrameworkElement statusText)
        {
            _formPanel = formPanel;
            _loadingOverlay = loadingOverlay;
            _statusText = statusText;

            Attach(view.DataContext as LoginPageViewModel);
            view.DataContextChanged += (_, e) => Attach(e.NewValue as LoginPageViewModel);

            // Cross-fade only the status text when it changes while the overlay is up.
            _statusText.AddHandler(Binding.TargetUpdatedEvent,
                new EventHandler<DataTransferEventArgs>(OnStatusTextUpdated));
        }

        private void Attach(LoginPageViewModel viewModel)
        {
            if (ReferenceEquals(_viewModel, viewModel))
                return;

            if (_viewModel != null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

            _viewModel = viewModel;

            if (_viewModel == null)
                return;

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyPhase(_viewModel.Phase, animate: false);
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LoginPageViewModel.Phase) && _viewModel != null)
                ApplyPhase(_viewModel.Phase, animate: true);
        }

        private void ApplyPhase(LoginPhase phase, bool animate)
        {
            switch (phase)
            {
                case LoginPhase.Form:
                    ShowForm(animate);
                    break;

                case LoginPhase.Loading:
                    ShowLoading(animate);
                    break;

                case LoginPhase.SuccessFade:
                    // Loading overlay simply stays; the status text cross-fades on its own.
                    break;

                case LoginPhase.Done:
                    FadeLoadingOutSlow(animate);
                    break;
            }
        }

        /// <summary>Initial state and the failure path: loading out, form fades in.</summary>
        private void ShowForm(bool animate)
        {
            _formPanel.Visibility = Visibility.Visible;

            if (!animate)
            {
                // Cold start: form still fades in so the page doesn't pop fully opaque.
                _formPanel.BeginAnimation(UIElement.OpacityProperty, null);
                _loadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                _formPanel.Opacity = 0;
                _loadingOverlay.Opacity = 0;
                _loadingOverlay.Visibility = Visibility.Collapsed;
                Fade(_formPanel, 1, FormFadeIn);
                return;
            }

            Fade(_loadingOverlay, 0, LoadingFadeOutFast, completed: () =>
            {
                if (_viewModel?.Phase == LoginPhase.Form)
                    _loadingOverlay.Visibility = Visibility.Collapsed;
            });
            Fade(_formPanel, 1, FormFadeIn);
        }

        /// <summary>Phase A: form fades out while the loading overlay fades in, slightly staggered.</summary>
        private void ShowLoading(bool animate)
        {
            _loadingOverlay.Visibility = Visibility.Visible;
            _formPanel.Visibility = Visibility.Visible;

            if (!animate)
            {
                _formPanel.BeginAnimation(UIElement.OpacityProperty, null);
                _loadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                _formPanel.Opacity = 0;
                _loadingOverlay.Opacity = 1;
                return;
            }

            Fade(_formPanel, 0, FormFadeOut);
            Fade(_loadingOverlay, 1, LoadingFadeIn, LoadingFadeInDelay);
        }

        /// <summary>Phase B → C hand-off: slow fade to black, then tell the VM to navigate.</summary>
        private void FadeLoadingOutSlow(bool animate)
        {
            if (!animate)
            {
                _loadingOverlay.BeginAnimation(UIElement.OpacityProperty, null);
                _loadingOverlay.Opacity = 0;
                _loadingOverlay.Visibility = Visibility.Collapsed;
                _viewModel?.NotifyLoadingFadeCompleted();
                return;
            }

            var viewModel = _viewModel;

            Fade(_loadingOverlay, 0, LoadingFadeOutSlow, completed: () =>
            {
                if (viewModel?.Phase != LoginPhase.Loading)
                    _loadingOverlay.Visibility = Visibility.Collapsed;

                viewModel?.NotifyLoadingFadeCompleted();
            });
        }

        private void OnStatusTextUpdated(object sender, DataTransferEventArgs e)
        {
            if (_loadingOverlay.Visibility != Visibility.Visible || _loadingOverlay.Opacity < 0.05)
                return;

            var fade = new DoubleAnimation(0.25, 1, StatusTextCrossFade)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            _statusText.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private static void Fade(FrameworkElement element, double to, TimeSpan duration,
            TimeSpan? beginTime = null, Action completed = null)
        {
            var animation = new DoubleAnimation(to, duration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                BeginTime = beginTime ?? TimeSpan.Zero,
                FillBehavior = FillBehavior.HoldEnd
            };

            if (completed != null)
                animation.Completed += (_, __) => completed();

            element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }
}
