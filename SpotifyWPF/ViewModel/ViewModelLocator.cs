using AutoMapper;
using CommonServiceLocator;
using GalaSoft.MvvmLight.Ioc;
using SpotifyWPF.Model;
using SpotifyWPF.Service;
using SpotifyWPF.Service.MessageBoxes;
using SpotifyWPF.ViewModel.Page;

namespace SpotifyWPF.ViewModel
{
    public class ViewModelLocator
    {
        public ViewModelLocator()
        {
            SimpleIoc.Default.Reset();

            ServiceLocator.SetLocatorProvider(() => SimpleIoc.Default);

            // Infrastructure
            SimpleIoc.Default.Register(() => AutoMapperConfiguration.Configure().CreateMapper());
            SimpleIoc.Default.Register<ISettingsProvider, SettingsProvider>();
            SimpleIoc.Default.Register<IMessageBoxService, MessageBoxService>();

            // Services
            SimpleIoc.Default.Register<ISpotify, Spotify>();
            SimpleIoc.Default.Register<IRequestSpacingService, RequestSpacingService>();
            SimpleIoc.Default.Register<IPlaylistLocalStore, PlaylistLocalStore>();
            SimpleIoc.Default.Register<IPlaylistPagingService, PlaylistPagingService>();
            SimpleIoc.Default.Register<IPlaylistDeletionService, PlaylistDeletionService>();
            SimpleIoc.Default.Register<IPlaylistActionQueueService, PlaylistActionQueueService>();

            // Page view models
            SimpleIoc.Default.Register<MainViewModel>();
            SimpleIoc.Default.Register<LoginPageViewModel>();
            SimpleIoc.Default.Register<PlaylistsPageViewModel>();
            SimpleIoc.Default.Register<AlbumsPageViewModel>();
            SimpleIoc.Default.Register<ArtistsPageViewModel>();
            SimpleIoc.Default.Register<SearchPageViewModel>();
        }

        public MainViewModel Main => ServiceLocator.Current.GetInstance<MainViewModel>();

        public PlaylistsPageViewModel PlaylistsPage => ServiceLocator.Current.GetInstance<PlaylistsPageViewModel>();

        public AlbumsPageViewModel AlbumsPage => ServiceLocator.Current.GetInstance<AlbumsPageViewModel>();

        public ArtistsPageViewModel ArtistsPage => ServiceLocator.Current.GetInstance<ArtistsPageViewModel>();

        public SearchPageViewModel Search => ServiceLocator.Current.GetInstance<SearchPageViewModel>();

        public LoginPageViewModel LoginPage => ServiceLocator.Current.GetInstance<LoginPageViewModel>();

        // Intentionally empty: everything registered above is an app-lifetime
        // singleton, torn down with the process. Views are recreated per
        // navigation and hold no resources that need explicit cleanup here.
        public static void Cleanup()
        {
        }
    }
}
