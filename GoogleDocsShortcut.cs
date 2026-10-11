using System.Text.RegularExpressions;

internal static class GoogleDocsShortcut
{
    // Chromium's Docs PWA and legacy hosted-app IDs, not a particular user's ID.
    // https://chromium.googlesource.com/chromium/src/+/refs/heads/main/ash/constants/web_app_id_constants.h
    // https://chromium.googlesource.com/chromium/src/+/refs/heads/main/chrome/browser/web_applications/preinstalled_web_apps/google_docs.cc
    private static readonly HashSet<string> KnownAppIds = new(StringComparer.Ordinal)
    {
        "mpnpojknpmmopombnjdcgaaiekajbnjb", "aohghmighlieiainnegkcijnfilokake"
    };
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "chrome_proxy.exe", "msedge.exe", "msedge_proxy.exe", "brave.exe", "firefox.exe"
    };
    private static readonly Regex WebUrl = new("""https?://[^\s"'<>]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AppId = new("""(?:^|\s)--app-id(?:=|\s+)["']?(?<id>[a-p]{32})["']?(?=\s|$)""",
        RegexOptions.CultureInvariant);

    internal static bool Matches(string displayName, ShortcutInfo? link, string? url)
    {
        if (url is not null) return IsDocsUrl(url);
        if (link is null || !Browsers.Contains(Path.GetFileName(Environment.ExpandEnvironmentVariables(link.TargetPath))))
            return false;
        if (WebUrl.Matches(link.Arguments).Any(match => IsDocsUrl(match.Value))) return true;
        Match app = AppId.Match(link.Arguments);
        if (!app.Success) return false;
        // A user-created browser app may have a different ID. Only the full product
        // name gets that fallback; a generic "Docs" shortcut is not enough evidence.
        return KnownAppIds.Contains(app.Groups["id"].Value)
            || displayName.Equals("Google Docs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDocsUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
        && uri.UserInfo.Length == 0 && uri.IsDefaultPort
        && (uri.AbsolutePath == "/" || uri.AbsolutePath == "/document"
            || uri.AbsolutePath.StartsWith("/document/", StringComparison.Ordinal));
}
