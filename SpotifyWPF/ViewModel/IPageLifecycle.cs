using System.Threading.Tasks;

namespace SpotifyWPF.ViewModel
{
    /// <summary>
    /// Optional navigation hooks for page view models. Page view models are
    /// singletons while their views are recreated on every navigation, so any
    /// work done in these hooks must be idempotent — safe to run on every
    /// revisit, including after a previous failed initialization.
    /// See docs/architecture.md for the full lifecycle rules.
    /// </summary>
    public interface IPageLifecycle
    {
        Task OnNavigatedToAsync();

        Task OnNavigatedFromAsync();
    }
}
