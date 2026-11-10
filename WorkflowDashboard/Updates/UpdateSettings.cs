namespace WorkflowDashboard.Updates;

/// <summary>
/// Update-check configuration. Everything is OFF by default so current behavior is unchanged.
/// </summary>
public static class UpdateSettings
{
    /// <summary>
    /// Master switch for the startup check. false = no network call at all.
    /// (static readonly instead of const so the guarded code doesn't trip unreachable-code warnings.)
    /// TODO(updates): move to SettingsStore once the update UI exists.
    /// </summary>
    public static readonly bool UpdateCheckEnabled = false;

    public const string RepoOwner = "KrypticXTM";
    public const string RepoName = "workflow-dashboard";

    /// <summary>Installer asset naming convention used by the Inno Setup script (installer/WorkflowDashboard.iss).</summary>
    public const string InstallerAssetPrefix = "WorkflowDashboard-Setup-";

    public static string LatestReleaseApiUrl =>
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>
    /// PRIVATE REPO CAVEAT: the GitHub API returns 404 for releases of a private repo without auth.
    /// Either (a) make the release repo public (or publish releases to a separate public repo and change
    /// RepoOwner/RepoName), or (b) supply a fine-grained read-only token (Contents: read) here.
    /// TODO(updates): do NOT ship a token in the binary; read it from a per-user secure store
    /// (e.g. Windows Credential Manager / DPAPI-protected file). The env var below is a dev-only hook.
    /// </summary>
    public static string? GetAccessToken()
    {
        try
        {
            var t = Environment.GetEnvironmentVariable("WORKFLOW_DASHBOARD_GITHUB_TOKEN");
            return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        }
        catch { return null; }
    }
}
