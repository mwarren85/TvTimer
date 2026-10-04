using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using TvTimer.Configuration;

namespace TvTimer;

/// <summary>
/// Talks to a Sony Bravia (Android/Google TV) over its local REST control API
/// (http(s)://&lt;tv-ip&gt;/sony/...). Supports two auth modes: a pre-shared key
/// (sent as an X-Auth-PSK header on every request), or the actRegister
/// device-authentication handshake — the TV displays a PIN on screen, which
/// is exchanged for a long-lived "auth" cookie reused for subsequent commands.
/// Over HTTPS, the TV's self-signed certificate is trusted only if it chains
/// to the specific root certificate configured via <see cref="SonyOptions.CaCertPath"/>.
/// </summary>
public class SonyBraviaClient
{
    private readonly HttpClient _http;
    private readonly SonyOptions _options;
    private readonly ILogger<SonyBraviaClient> _logger;
    private string? _authCookie;

    public SonyBraviaClient(HttpClient http, IOptions<SonyOptions> options, ILogger<SonyBraviaClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            _options.ClientId = $"TvTimer:{Guid.NewGuid()}";
        }
    }

    private bool UsePsk => !string.IsNullOrWhiteSpace(_options.Psk);
    private string Scheme => _options.UseHttps ? "https" : "http";

    private string AccessControlUrl => $"{Scheme}://{_options.TvIp}/sony/accessControl";
    private string SystemUrl => $"{Scheme}://{_options.TvIp}/sony/system";

    public bool TryLoadAuthCookie()
    {
        if (!File.Exists(_options.AuthCookiePath))
        {
            return false;
        }

        var cookie = File.ReadAllText(_options.AuthCookiePath).Trim();
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return false;
        }

        _authCookie = cookie;
        return true;
    }

    /// <summary>
    /// Either verifies a configured pre-shared key against the TV, or — if no PSK is
    /// configured — runs the interactive first-time PIN pairing flow: asks the TV to
    /// register this client, prompts on the console for the PIN the TV displays, then
    /// saves the resulting auth cookie to disk for the background worker to reuse.
    /// </summary>
    public async Task PairInteractivelyAsync(CancellationToken ct = default)
    {
        if (UsePsk)
        {
            await VerifyPskAsync(ct);
            return;
        }

        Console.WriteLine($"Requesting pairing PIN from TV at {_options.TvIp}...");
        using var initial = await SendRegisterAsync(authHeader: null, ct);

        if (initial.StatusCode == HttpStatusCode.OK)
        {
            await SaveCookieFromResponseAsync(initial);
            Console.WriteLine("TV accepted registration without a PIN. Pairing complete.");
            return;
        }

        if (initial.StatusCode != HttpStatusCode.Unauthorized)
        {
            var body = await initial.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Unexpected response from TV during registration: {(int)initial.StatusCode} {initial.StatusCode}. Body: {body}");
        }

        Console.WriteLine("Check your TV screen for a 4-digit PIN.");
        Console.Write("Enter the PIN shown on the TV: ");
        var pin = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(pin))
        {
            throw new InvalidOperationException("No PIN entered.");
        }

        var authHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($":{pin}"));
        using var confirmResponse = await SendRegisterAsync(authHeader, ct);

        if (confirmResponse.StatusCode != HttpStatusCode.OK)
        {
            var body = await confirmResponse.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"TV rejected the PIN ({(int)confirmResponse.StatusCode}): {body}");
        }

        await SaveCookieFromResponseAsync(confirmResponse);
        Console.WriteLine($"Pairing successful. Auth cookie saved to {_options.AuthCookiePath}.");
    }

    private async Task VerifyPskAsync(CancellationToken ct)
    {
        Console.WriteLine($"Pre-shared key configured — verifying it against the TV at {_options.TvIp} (no pairing needed for PSK auth)...");

        var payload = new
        {
            method = "getPowerStatus",
            id = 1,
            @params = Array.Empty<object>(),
            version = "1.0"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, SystemUrl);
        request.Content = JsonContent.Create(payload);
        ApplyAuth(request);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"TV rejected the pre-shared key ({(int)response.StatusCode}): {body}");
        }

        Console.WriteLine("PSK accepted. The worker will send it as an X-Auth-PSK header on every request — no further setup needed.");
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (UsePsk)
        {
            request.Headers.TryAddWithoutValidation("X-Auth-PSK", _options.Psk);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Cookie", _authCookie);
        }
    }

    private async Task<HttpResponseMessage> SendRegisterAsync(string? authHeader, CancellationToken ct)
    {
        var payload = new
        {
            method = "actRegister",
            id = 1,
            @params = new object[]
            {
                new { clientid = _options.ClientId, nickname = _options.Nickname, level = "private" },
                new[] { new { value = "yes", function = "WOL" } }
            },
            version = "1.0"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, AccessControlUrl);
        request.Content = JsonContent.Create(payload);
        if (authHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        }

        return await _http.SendAsync(request, ct);
    }

    private async Task SaveCookieFromResponseAsync(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            throw new InvalidOperationException("TV did not return an auth cookie.");
        }

        var authCookie = cookies
            .Select(c => c.Split(';')[0])
            .FirstOrDefault(c => c.StartsWith("auth=", StringComparison.OrdinalIgnoreCase));

        if (authCookie is null)
        {
            throw new InvalidOperationException("TV response did not contain an 'auth' cookie.");
        }

        var dir = Path.GetDirectoryName(_options.AuthCookiePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(_options.AuthCookiePath, authCookie);
        _authCookie = authCookie;
    }

    public async Task PowerOffAsync(CancellationToken ct = default)
    {
        if (!UsePsk && _authCookie is null && !TryLoadAuthCookie())
        {
            throw new InvalidOperationException(
                "No auth cookie found. Run 'dotnet TvTimer.dll --pair' first to register this device with the TV.");
        }

        var payload = new
        {
            method = "setPowerStatus",
            id = 55,
            @params = new object[] { new { status = false } },
            version = "1.0"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, SystemUrl);
        request.Content = JsonContent.Create(payload);
        ApplyAuth(request);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"TV returned {(int)response.StatusCode}: {body}");
        }

        _logger.LogDebug("TV response: {Body}", body);
    }
}
