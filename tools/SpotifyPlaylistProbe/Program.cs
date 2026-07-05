using System.Diagnostics;
using System.Text.Json;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

var options = ProbeOptions.Parse(args);

if (string.IsNullOrWhiteSpace(options.ClientId))
{
    Console.WriteLine("Missing Spotify Client ID.");
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project tools/SpotifyPlaylistProbe -- --client-id <client-id> [--port 4002] [--limit 50] [--all] [--reauth]");
    Console.WriteLine();
    Console.WriteLine("You can also set SPOTIFY_CLIENT_ID instead of passing --client-id.");
    return 1;
}

var redirectUri = new Uri($"http://127.0.0.1:{options.Port}/callback");
var tokenCachePath = GetTokenCachePath(options.ClientId);

Console.WriteLine($"Client ID: {Mask(options.ClientId)}");
Console.WriteLine($"Redirect URI: {redirectUri}");
Console.WriteLine($"Token cache: {tokenCachePath}");
Console.WriteLine($"Limit: {options.Limit}");
Console.WriteLine();

try
{
    var token = options.ForceReauth ? null : await LoadCachedTokenAsync(tokenCachePath);

    if (token == null)
    {
        token = await AuthenticateAsync(options.ClientId, redirectUri);
        await SaveTokenAsync(tokenCachePath, token);
        Console.WriteLine("Authenticated and cached a new PKCE token.");
    }
    else
    {
        Console.WriteLine("Loaded cached PKCE token.");
    }

    var authenticator = new PKCEAuthenticator(options.ClientId, token);
    authenticator.TokenRefreshed += (_, refreshedToken) =>
    {
        Console.WriteLine("Token refreshed; updating cache.");
        SaveTokenAsync(tokenCachePath, refreshedToken).GetAwaiter().GetResult();
    };

    var spotify = new SpotifyClient(SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator));

    await PrintCurrentUserAsync(spotify);
    Console.WriteLine();

    await RunParameterlessPlaylistRequestAsync(spotify);
    Console.WriteLine();

    await RunExplicitPlaylistRequestAsync(spotify, options.Limit);
    Console.WriteLine();

    if (options.LoadAll)
        await LoadAllPlaylistsAsync(spotify, options.Limit);

    await SaveTokenAsync(tokenCachePath, token);

    return 0;
}
catch (APITooManyRequestsException ex)
{
    Console.WriteLine($"Spotify rate limited the probe. RetryAfter: {ex.RetryAfter}");
    return 2;
}
catch (APIException ex)
{
    Console.WriteLine($"Spotify API error. Status: {ex.Response?.StatusCode}. Message: {ex.Message}");
    return 3;
}
catch (Exception ex)
{
    Console.WriteLine(ex);
    return 4;
}

static async Task<PKCETokenResponse> AuthenticateAsync(string clientId, Uri redirectUri)
{
    var completionSource = new TaskCompletionSource<PKCETokenResponse>();
    var (verifier, challenge) = PKCEUtil.GenerateCodes();

    using var server = new EmbedIOAuthServer(redirectUri, redirectUri.Port);

    server.AuthorizationCodeReceived += async (_, response) =>
    {
        await server.Stop();

        try
        {
            var token = await new OAuthClient().RequestToken(
                new PKCETokenRequest(clientId, response.Code, redirectUri, verifier));

            completionSource.SetResult(token);
        }
        catch (Exception ex)
        {
            completionSource.SetException(ex);
        }
    };

    server.ErrorReceived += async (_, error, state) =>
    {
        await server.Stop();
        completionSource.SetException(new InvalidOperationException($"Spotify auth error: {error}; state={state}"));
    };

    await server.Start();

    var request = new LoginRequest(redirectUri, clientId, LoginRequest.ResponseType.Code)
    {
        CodeChallengeMethod = "S256",
        CodeChallenge = challenge,
        Scope = new List<string>
        {
            Scopes.UserReadPrivate,
            Scopes.PlaylistReadPrivate,
            Scopes.PlaylistReadCollaborative,
            Scopes.PlaylistModifyPrivate,
            Scopes.PlaylistModifyPublic
        }
    };

    Console.WriteLine("Opening browser for Spotify login...");
    Process.Start(new ProcessStartInfo
    {
        FileName = request.ToUri().ToString(),
        UseShellExecute = true
    });

    return await completionSource.Task;
}

static async Task PrintCurrentUserAsync(ISpotifyClient spotify)
{
    var user = await spotify.UserProfile.Current();

    Console.WriteLine("Authenticated user:");
    Console.WriteLine($"  Id: {user.Id}");
    Console.WriteLine($"  DisplayName: {user.DisplayName ?? "(none)"}");
    Console.WriteLine($"  Country: {user.Country ?? "(none)"}");
    Console.WriteLine($"  Product: {user.Product ?? "(none)"}");
}

static async Task RunParameterlessPlaylistRequestAsync(ISpotifyClient spotify)
{
    Console.WriteLine("Calling Playlists.CurrentUsers()...");
    var page = await spotify.Playlists.CurrentUsers();
    PrintPlaylistPage("Parameterless response", page);
}

static async Task RunExplicitPlaylistRequestAsync(ISpotifyClient spotify, int limit)
{
    var request = new PlaylistCurrentUsersRequest
    {
        Limit = limit,
        Offset = 0
    };

    Console.WriteLine("Calling Playlists.CurrentUsers(request)...");
    Console.WriteLine($"  Request: limit={request.Limit}, offset={request.Offset}, locale={request.Locale ?? "(default)"}");

    var page = await spotify.Playlists.CurrentUsers(request);
    PrintPlaylistPage("Explicit request response", page);
}

static async Task LoadAllPlaylistsAsync(ISpotifyClient spotify, int limit)
{
    Console.WriteLine();
    Console.WriteLine("Loading all playlists with explicit offset paging...");

    var totalLoaded = 0;
    var offset = 0;

    while (true)
    {
        var request = new PlaylistCurrentUsersRequest
        {
            Limit = limit,
            Offset = offset
        };

        var page = await spotify.Playlists.CurrentUsers(request);
        var itemCount = page.Items?.Count ?? 0;

        totalLoaded += itemCount;
        Console.WriteLine($"  Page offset={offset}, items={itemCount}, totalLoaded={totalLoaded}, reportedTotal={page.Total}");

        if (itemCount == 0 || page.Next == null)
            break;

        offset += itemCount;
        await Task.Delay(150);
    }
}

static void PrintPlaylistPage(string label, Paging<FullPlaylist> page)
{
    Console.WriteLine(label + ":");
    Console.WriteLine($"  Total: {page.Total}");
    Console.WriteLine($"  Limit: {page.Limit}");
    Console.WriteLine($"  Offset: {page.Offset}");
    Console.WriteLine($"  Items: {page.Items?.Count ?? 0}");
    Console.WriteLine($"  Next: {page.Next ?? "(none)"}");
    Console.WriteLine($"  Previous: {page.Previous ?? "(none)"}");

    foreach (var playlist in (page.Items ?? new List<FullPlaylist>()).Take(20))
    {
        Console.WriteLine($"  - {playlist.Name} | id={playlist.Id} | owner={playlist.Owner?.DisplayName ?? playlist.Owner?.Id} | tracks={playlist.Tracks?.Total}");
    }
}

static async Task<PKCETokenResponse?> LoadCachedTokenAsync(string tokenCachePath)
{
    if (!File.Exists(tokenCachePath))
        return null;

    await using var stream = File.OpenRead(tokenCachePath);
    return await JsonSerializer.DeserializeAsync<PKCETokenResponse>(stream);
}

static async Task SaveTokenAsync(string tokenCachePath, PKCETokenResponse token)
{
    Directory.CreateDirectory(Path.GetDirectoryName(tokenCachePath)!);

    await using var stream = File.Create(tokenCachePath);
    await JsonSerializer.SerializeAsync(stream, token, new JsonSerializerOptions { WriteIndented = true });
}

static string GetTokenCachePath(string clientId)
{
    var safeClientId = new string(clientId.Where(char.IsLetterOrDigit).ToArray());
    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    return Path.Combine(localAppData, "SpotifyWPF", "PlaylistProbe", $"pkce-token-{safeClientId}.json");
}

static string Mask(string value)
{
    if (value.Length <= 8)
        return "********";

    return value.Substring(0, 4) + "..." + value.Substring(value.Length - 4);
}

sealed class ProbeOptions
{
    private const int DefaultLimit = 50;
    private const int DefaultPort = 4002;

    public string ClientId { get; private set; } = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_ID") ?? string.Empty;

    public int Port { get; private set; } = DefaultPort;

    public int Limit { get; private set; } = DefaultLimit;

    public bool LoadAll { get; private set; }

    public bool ForceReauth { get; private set; }

    public static ProbeOptions Parse(string[] args)
    {
        var options = new ProbeOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--client-id":
                    options.ClientId = GetValue(args, ref i);
                    break;
                case "--port":
                    options.Port = int.Parse(GetValue(args, ref i));
                    break;
                case "--limit":
                    options.Limit = Math.Clamp(int.Parse(GetValue(args, ref i)), 1, 50);
                    break;
                case "--all":
                    options.LoadAll = true;
                    break;
                case "--reauth":
                    options.ForceReauth = true;
                    break;
            }
        }

        return options;
    }

    private static string GetValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {args[index]}");

        index++;
        return args[index];
    }
}
