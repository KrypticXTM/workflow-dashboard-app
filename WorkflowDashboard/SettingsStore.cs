using System.IO;
using System.Text.Json;

namespace WorkflowDashboard;

internal sealed class CropRectDto
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

internal sealed class AppSettings
{
    public int PaneCount { get; set; } = 3;
    public string Layout { get; set; } = "equal"; // equal | main | large
    public int ActivePane { get; set; } = 1;
    public bool SidePanelCollapsed { get; set; } = false;
    public bool LiveDesk { get; set; } = true;
    /// <summary>When true (default), main/large large pane is on the left. Flip toggles.</summary>
    public bool MainOnLeft { get; set; } = true;
    /// <summary>UI zoom factor for desk window content (0.8-1.5). Persisted.</summary>
    public double Zoom { get; set; } = 1.0;
    /// <summary>Dashboard size preset: full | 90 | 80 | 70 | empty. (tall migrated to LockHeight)</summary>
    public string SizePreset { get; set; } = "";
    /// <summary>Linked window fit: fill (stretch to pane, default) | fit (aspect bounds inside pane, centered).</summary>
    public string FitMode { get; set; } = "fill";
    /// <summary>When true, dashboard Height and Top stay fixed; width (sides) still resizable.</summary>
    public bool LockHeight { get; set; } = false;
    /// <summary>v1.0.1: press YouTube's Skip button automatically when it appears (On by default).</summary>
    public bool AutoSkipAds { get; set; } = true;
    /// <summary>v1.0.2: show the title card instead of the live Media preview while the playing window is itself
    /// visible (foreground or in a linked pane). On by default.</summary>
    public bool AutoTitleCardMedia { get; set; } = true;
    /// <summary>Legacy (curtain build, ad9cfcc): migrated into AutoTitleCardMedia on load, then dropped.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoCurtainMedia { get; set; }
    // v1.0.2 media preview hidden via the Media header chevron.
    public bool MediaPreviewCollapsed { get; set; } = false;
    /// <summary>v1.0.2: park the browser's automatic video pop-out (PiP) out of sight and show it in the Media preview.</summary>
    public bool HideBrowserPopOut { get; set; } = true;
    /// <summary>UI accent/text color as #RRGGBB (default canary).</summary>
    public string AccentHex { get; set; } = "#FFE566";
    /// <summary>User-chosen default accent (Set default on the color strip).</summary>
    public string AccentDefaultHex { get; set; } = "#FFE566";
    /// <summary>Desk background as #RRGGBB (default pure black).</summary>
    public string BackgroundHex { get; set; } = "#000000";
    /// <summary>User-chosen default background (Set default on the color strip).</summary>
    public string BackgroundDefaultHex { get; set; } = "#000000";
    /// <summary>Per process|title crop rects for PreviewProxy thumbnails.</summary>
    public Dictionary<string, CropRectDto> PreviewCrops { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string PathFile
    {
        get
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkflowDashboard");
            Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return new AppSettings();
            var json = File.ReadAllText(PathFile);
            var s = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            if (s.AutoCurtainMedia is bool legacyCurtain) { s.AutoTitleCardMedia = legacyCurtain; s.AutoCurtainMedia = null; }
            s.PaneCount = Math.Clamp(s.PaneCount, 1, 3);
            s.ActivePane = Math.Clamp(s.ActivePane, 1, 3);
            if (string.Equals(s.Layout, "focus", StringComparison.OrdinalIgnoreCase))
                s.Layout = "large";
            if (s.Layout is not ("equal" or "main" or "large"))
                s.Layout = "equal";
            if (double.IsNaN(s.Zoom) || s.Zoom < 0.8 || s.Zoom > 1.5)
                s.Zoom = 1.0;
            else
                s.Zoom = Math.Round(s.Zoom, 2);
            if (string.Equals(s.SizePreset, "tall", StringComparison.OrdinalIgnoreCase))
            {
                s.LockHeight = true;
                s.SizePreset = "";
            }
            if (s.SizePreset is not ("" or "full" or "90" or "80" or "70"))
                s.SizePreset = "";
            if (s.FitMode is not ("fit" or "fill"))
                s.FitMode = "fill";
            s.PreviewCrops ??= new Dictionary<string, CropRectDto>(StringComparer.OrdinalIgnoreCase);
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(PathFile, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch
        {
        }
    }

    public static string CropKey(string? processName, string? title)
    {
        var p = (processName ?? "").Trim();
        var t = TitleSanitizer.Clean(title ?? "");
        if (t.Length > 64) t = t[..64];
        return $"{p}|{t}";
    }
}
