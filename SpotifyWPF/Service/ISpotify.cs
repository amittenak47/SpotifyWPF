using SpotifyAPI.Web;
using System;
using System.Threading.Tasks;

namespace SpotifyWPF.Service
{
    public interface ISpotify
    {
        Task LoginAsync(Action onSuccess);

        Task ReauthorizeAsync(Action onSuccess);

        void ResetAuthenticationState();

        Task<PrivateUser> GetPrivateProfileAsync();

        /// <summary>
        /// Returns a currently valid access token, refreshing it first when it is close to expiry.
        /// Returns null when the user is not logged in. Used to hand tokens to the Web Playback SDK.
        /// </summary>
        Task<string> GetAccessTokenAsync();

        ISpotifyClient Api { get; }
    }
}
