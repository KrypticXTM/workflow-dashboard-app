using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace WorkflowDashboard.Updates;

/// <summary>
/// Isolated GitHub-Releases update checker (scaffold).
/// Implemented: current version, latest-release query, tag/asset parsing, System.Version compare.
/// TODO: DownloadAsync / ApplyAsync (stubs only).
/// </summary>
public sealed class UpdateChecker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>This app's version from the assembly (csproj &lt;Version&gt;/&lt;AssemblyVersion&gt;), normalized to Major.Minor.Build.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = (Assembly.GetEntryAssembly() ?? typeof(UpdateChecker).Assembly).GetName().Version;
            return Normalize(v ?? new Version(0, 0, 0));
        }
    }

    /// <summary>
    /// GET /repos/{owner}/{repo}/releases/latest. Returns UpdateInfo when the release is newer than
    /// <see cref="CurrentVersion"/> and has an installer asset; otherwise null. Never throws (fail-silent).
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UpdateSettings.LatestReleaseApiUrl);
            req.Headers.UserAgent.ParseAdd($"WorkflowDashboard/{CurrentVersion}");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            // Private repo: needs a token (see UpdateSettings.GetAccessToken caveat). Public repo: no auth.
            var token = UpdateSettings.GetAccessToken();
            if (token != null)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null; // 404 = no release yet OR private repo without token; 403 = rate limit.

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return ParseLatestRelease(doc.RootElement, CurrentVersion);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pure parser (unit-testable): tag_name -> Version, pick installer asset, compare.</summary>
    public static UpdateInfo? ParseLatestRelease(JsonElement root, Version current)
    {
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!TryParseTagVersion(tag, out var remote)) return null;
        if (remote <= Normalize(current)) return null;

        string? url = null, apiUrl = null, name = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            JsonElement? pick = null;
            foreach (var a in assets.EnumerateArray())
            {
                var n = a.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
                if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.StartsWith(UpdateSettings.InstallerAssetPrefix, StringComparison.OrdinalIgnoreCase)) { pick = a; break; }
                pick ??= a; // fallback: first .exe
            }
            if (pick is JsonElement p)
            {
                name = p.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                url = p.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null;
                apiUrl = p.TryGetProperty("url", out var au) ? au.GetString() : null;
                size = p.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0;
            }
        }
        if (string.IsNullOrWhiteSpace(url)) return null;

        return new UpdateInfo
        {
            Version = remote,
            TagName = tag!,
            InstallerUrl = url!,
            InstallerApiUrl = apiUrl,
            InstallerFileName = name ?? "",
            InstallerSizeBytes = size,
            ReleasePageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null,
            Notes = root.TryGetProperty("body", out var b) ? b.GetString() : null
        };
    }

    /// <summary>"v1.2.3" / "1.2.3" / "v1.2.3-beta" -> 1.2.3.</summary>
    public static bool TryParseTagVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s.Substring(0, cut);
        if (!s.Contains('.')) s += ".0";
        if (!Version.TryParse(s, out var v) || v == null) return false;
        version = Normalize(v);
        return true;
    }

    private static Version Normalize(Version v) =>
        new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>
    /// TODO(updates): download the installer to %TEMP%\WorkflowDashboard\{InstallerFileName}.
    ///  - Public repo: GET info.InstallerUrl (browser_download_url) with User-Agent, follow redirects.
    ///  - Private repo: GET info.InstallerApiUrl with "Accept: application/octet-stream" + Bearer token
    ///    (browser_download_url does not work for private assets).
    ///  - Stream to a .partial file, report progress, verify size == InstallerSizeBytes, then rename.
    ///  - Optional: verify a SHA256 published in the release notes / a .sha256 asset.
    /// Returns the full path of the downloaded installer.
    /// </summary>
    public Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        _ = Path.Combine(Path.GetTempPath(), "WorkflowDashboard", info.InstallerFileName); // intended target
        throw new NotImplementedException("TODO(updates): DownloadAsync is scaffolded but not implemented.");
    }

    /// <summary>
    /// TODO(updates): launch the Inno Setup installer silently and exit so files can be replaced:
    ///   Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
    ///   then Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    /// The .iss uses CloseApplications + AppMutex so a running instance is closed/detected.
    /// Unlink/restore Live windows first (same path as normal app close) before shutting down.
    /// </summary>
    public Task ApplyAsync(string installerPath, CancellationToken ct = default)
    {
        _ = typeof(Process); // keeps the intended dependency visible
        throw new NotImplementedException("TODO(updates): ApplyAsync is scaffolded but not implemented.");
    }
}
