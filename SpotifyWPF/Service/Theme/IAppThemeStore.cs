using SpotifyWPF.Model;

namespace SpotifyWPF.Service.Theme
{
    public interface IAppThemeStore
    {
        AppThemePalette Get();

        void Save(AppThemePalette palette);
    }
}
