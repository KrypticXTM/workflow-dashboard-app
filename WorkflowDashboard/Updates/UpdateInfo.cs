namespace WorkflowDashboard.Updates;

/// <summary>Result of a successful "newer release exists" check against GitHub Releases.</summary>
public sealed class UpdateInfo
{
    /// <summary>Remote version parsed from tag_name (leading 'v' stripped), normalized to Major.Minor.Build.</summary>
    public required Version Version { get; init; }

    /// <summary>Raw tag, e.g. "v1.1.0".</summary>
    public required string TagName { get; init; }

    /// <summary>browser_download_url of the installer asset (WorkflowDashboard-Setup-x.y.z.exe).</summary>
    public required string InstallerUrl { get; init; }

    public string InstallerFileName { get; init; } = "";

    public long InstallerSizeBytes { get; init; }

    /// <summary>GitHub "url" of the asset (API URL). Needed for private-repo downloads (Accept: application/octet-stream + token).</summary>
    public string? InstallerApiUrl { get; init; }

    /// <summary>html_url of the release page.</summary>
    public string? ReleasePageUrl { get; init; }

    /// <summary>Release notes (body).</summary>
    public string? Notes { get; init; }
}
