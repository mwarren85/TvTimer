namespace TvTimer.Configuration;

public class SonyOptions
{
    public string TvIp { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Nickname { get; set; } = "TvTimer";
    public string AuthCookiePath { get; set; } = "/var/lib/tvtimer/auth-cookie.txt";

    /// <summary>
    /// Pre-shared key configured on the TV (Settings → Network & Internet → IP control →
    /// Authentication → Normal and Pre-Shared Key). When set, this is used instead of the
    /// actRegister PIN-pairing flow — no pairing step needed.
    /// </summary>
    public string Psk { get; set; } = "";

    /// <summary>Talk to the TV over HTTPS instead of HTTP. Requires <see cref="CaCertPath"/>.</summary>
    public bool UseHttps { get; set; } = false;

    /// <summary>
    /// Path to the TV's self-signed root certificate (exported from the TV's IP control /
    /// HTTPS settings). Required when <see cref="UseHttps"/> is true — the connection is
    /// trusted only if the TV presents a certificate chaining to this exact file, not to
    /// any public CA.
    /// </summary>
    public string CaCertPath { get; set; } = "";
}
