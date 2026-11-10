using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WorkflowDashboard;

/// <summary>
/// DWM live thumbnail host for contained desk panes.
/// Default: letterbox (aspect preserved). With a crop source rect: uniform scale-to-fill the pane.
/// </summary>
internal sealed class ThumbnailHost : FrameworkElement
{
    private IntPtr _thumbnail = IntPtr.Zero;
    private IntPtr _sourceHwnd = IntPtr.Zero;
    private IntPtr _destTopLevel = IntPtr.Zero;
    private int _lastHr;
    private NativeMethods.Rect? _sourceCrop;
    private bool _fillHost;

    public IntPtr SourceHwnd => _sourceHwnd;
    public bool HasThumbnail => _thumbnail != IntPtr.Zero;
    public int LastRegisterHr => _lastHr;

    /// <summary>Optional crop in source thumbnail coordinates (DWM source size space).</summary>
    internal NativeMethods.Rect? SourceCrop
    {
        get => _sourceCrop;
        set
        {
            _sourceCrop = value;
            UpdateThumbnailLayout();
        }
    }

    /// <summary>When true (crop mode), destination covers the full host (uniform cover/fill).</summary>
    /// <summary>
    /// v1.0.2 Preview: crop the source to its visible frame minus the 1 px OS border, so no white/accent window
    /// edge (or transparent resize border) shows around the thumbnail. Ignored when an explicit crop is set.
    /// </summary>
    public bool TrimOsFrame { get; set; }

    private bool TryGetTrimRect(out NativeMethods.Rect src)
    {
        src = default;
        if (_sourceHwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(_sourceHwnd, out var wr)) return false;
        if (!NativeMethods.TryGetVisibleFrame(_sourceHwnd, out var fr) || fr.IsEmpty) fr = wr;
        const int border = 1;
        src = new NativeMethods.Rect
        {
            Left = Math.Max(0, fr.Left - wr.Left + border),
            Top = Math.Max(0, fr.Top - wr.Top + border),
            Right = Math.Min(wr.Width, fr.Right - wr.Left - border),
            Bottom = Math.Min(wr.Height, fr.Bottom - wr.Top - border),
        };
        return src.Width > 8 && src.Height > 8;
    }

    public bool FillHost
    {
        get => _fillHost;
        set
        {
            if (_fillHost == value) return;
            _fillHost = value;
            UpdateThumbnailLayout();
        }
    }

    public ThumbnailHost()
    {
        MinHeight = 40;
        MinWidth = 40;
        Focusable = false;
        IsHitTestVisible = false;
        SizeChanged += (_, _) => UpdateThumbnailLayout();
        IsVisibleChanged += (_, _) => UpdateThumbnailLayout();
        Loaded += (_, _) => UpdateThumbnailLayout();
        Unloaded += (_, _) => Clear();
    }

    public void Assign(IntPtr sourceHwnd)
    {
        Clear(keepCrop: true);
        if (sourceHwnd == IntPtr.Zero)
            return;

        _destTopLevel = ResolveTopLevelHwnd();
        if (_destTopLevel == IntPtr.Zero)
        {
            _lastHr = unchecked((int)0x80004005);
            return;
        }

        _lastHr = NativeMethods.DwmRegisterThumbnail(_destTopLevel, sourceHwnd, out _thumbnail);
        if (_lastHr != 0 || _thumbnail == IntPtr.Zero)
        {
            _thumbnail = IntPtr.Zero;
            _sourceHwnd = IntPtr.Zero;
            return;
        }

        _sourceHwnd = sourceHwnd;
        Dispatcher.BeginInvoke(UpdateThumbnailLayout, System.Windows.Threading.DispatcherPriority.Loaded);
        UpdateThumbnailLayout();
    }

    public void Clear(bool keepCrop = false)
    {
        if (_thumbnail != IntPtr.Zero)
        {
            NativeMethods.DwmUnregisterThumbnail(_thumbnail);
            _thumbnail = IntPtr.Zero;
        }
        _sourceHwnd = IntPtr.Zero;
        _lastHr = 0;
        if (!keepCrop)
        {
            _sourceCrop = null;
            _fillHost = false;
        }
    }

    public void RefreshLayout() => UpdateThumbnailLayout();

    public bool TryGetSourceSize(out int width, out int height)
    {
        width = height = 0;
        if (_thumbnail != IntPtr.Zero
            && NativeMethods.DwmQueryThumbnailSourceSize(_thumbnail, out var size) == 0
            && size.cx > 0 && size.cy > 0)
        {
            width = size.cx;
            height = size.cy;
            return true;
        }
        if (_sourceHwnd != IntPtr.Zero
            && NativeMethods.GetWindowRect(_sourceHwnd, out var wr)
            && wr.Width > 0 && wr.Height > 0)
        {
            width = wr.Width;
            height = wr.Height;
            return true;
        }
        return false;
    }

    private IntPtr ResolveTopLevelHwnd()
    {
        var w = Window.GetWindow(this);
        if (w != null)
        {
            var h = new WindowInteropHelper(w).EnsureHandle();
            if (h != IntPtr.Zero) return h;
        }
        return IntPtr.Zero;
    }

    private void UpdateThumbnailLayout()
    {
        if (_thumbnail == IntPtr.Zero)
            return;

        if (_destTopLevel == IntPtr.Zero)
            _destTopLevel = ResolveTopLevelHwnd();
        if (_destTopLevel == IntPtr.Zero)
            return;

        if (!TryGetDestinationRect(out var host))
            return;

        bool useCrop = _sourceCrop is { } crop && crop.Width > 1 && crop.Height > 1;
        NativeMethods.Rect trim = default;
        bool useTrim = !useCrop && TrimOsFrame && TryGetTrimRect(out trim);
        NativeMethods.Rect dest;
        if (useCrop || _fillHost)
            dest = CoverFill(host, useCrop ? _sourceCrop!.Value.Width : 0, useCrop ? _sourceCrop!.Value.Height : 0, _thumbnail, _sourceHwnd);
        else if (useTrim)
            dest = Letterbox(host, trim.Width, trim.Height);
        else
            dest = Letterbox(host, _thumbnail, _sourceHwnd);

        var props = new NativeMethods.DwmThumbnailProperties
        {
            Flags = NativeMethods.DWM_TNP_VISIBLE
                  | NativeMethods.DWM_TNP_RECTDESTINATION
                  | NativeMethods.DWM_TNP_OPACITY
                  | NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY,
            Destination = dest,
            Opacity = 255,
            Visible = true,
            SourceClientAreaOnly = false
        };

        if (useCrop)
        {
            props.Flags |= NativeMethods.DWM_TNP_RECTSOURCE;
            props.Source = _sourceCrop!.Value;
        }
        else if (useTrim)
        {
            props.Flags |= NativeMethods.DWM_TNP_RECTSOURCE;
            props.Source = trim;
        }

        NativeMethods.DwmUpdateThumbnailProperties(_thumbnail, ref props);
    }

    private static NativeMethods.Rect Letterbox(NativeMethods.Rect host, int srcW, int srcH)
    {
        if (host.Width < 1 || host.Height < 1 || srcW < 1 || srcH < 1) return host;
        double scale = Math.Min((double)host.Width / srcW, (double)host.Height / srcH);
        int drawW = Math.Max(1, (int)Math.Floor(srcW * scale));
        int drawH = Math.Max(1, (int)Math.Floor(srcH * scale));
        int x = host.Left + (host.Width - drawW) / 2;
        int y = host.Top + (host.Height - drawH) / 2;
        return new NativeMethods.Rect { Left = x, Top = y, Right = x + drawW, Bottom = y + drawH };
    }

    /// <summary>Fit source into host with uniform scale (letterbox). Never stretch/squish.</summary>
    private static NativeMethods.Rect Letterbox(NativeMethods.Rect host, IntPtr thumbnail, IntPtr sourceHwnd)
    {
        int hostW = host.Width;
        int hostH = host.Height;
        if (hostW < 1 || hostH < 1)
            return host;

        ResolveSrcSize(thumbnail, sourceHwnd, 0, 0, out int srcW, out int srcH);

        double scale = Math.Min((double)hostW / srcW, (double)hostH / srcH);
        int drawW = Math.Max(1, (int)Math.Floor(srcW * scale));
        int drawH = Math.Max(1, (int)Math.Floor(srcH * scale));
        int x = host.Left + (hostW - drawW) / 2;
        int y = host.Top + (hostH - drawH) / 2;

        return new NativeMethods.Rect
        {
            Left = x,
            Top = y,
            Right = x + drawW,
            Bottom = y + drawH
        };
    }

    /// <summary>
    /// Uniform scale so the source covers the full host (OBS-style fill). May crop edges of the scaled image.
    /// Destination is the full host rect — DWM source rect provides the cropped content.
    /// </summary>
    private static NativeMethods.Rect CoverFill(NativeMethods.Rect host, int preferW, int preferH, IntPtr thumbnail, IntPtr sourceHwnd)
    {
        // With RECTSOURCE set, destination is the pane; content fills it.
        if (host.Width < 1 || host.Height < 1)
            return host;
        return host;
    }

    private static void ResolveSrcSize(IntPtr thumbnail, IntPtr sourceHwnd, int preferW, int preferH, out int srcW, out int srcH)
    {
        srcW = preferW;
        srcH = preferH;
        if (srcW > 0 && srcH > 0) return;

        if (NativeMethods.DwmQueryThumbnailSourceSize(thumbnail, out var size) == 0
            && size.cx > 0 && size.cy > 0)
        {
            srcW = size.cx;
            srcH = size.cy;
            return;
        }
        if (sourceHwnd != IntPtr.Zero
            && NativeMethods.GetWindowRect(sourceHwnd, out var wr)
            && wr.Width > 0 && wr.Height > 0)
        {
            srcW = wr.Width;
            srcH = wr.Height;
            return;
        }
        srcW = 16;
        srcH = 9;
    }

    private bool TryGetDestinationRect(out NativeMethods.Rect dest)
    {
        dest = default;
        try
        {
            if (!IsVisible || ActualWidth < 1 || ActualHeight < 1)
                return false;

            var window = Window.GetWindow(this);
            if (window == null)
                return false;

            GeneralTransform gt = TransformToAncestor(window);
            Point tl = gt.Transform(new Point(0, 0));
            Point br = gt.Transform(new Point(ActualWidth, ActualHeight));

            var dpi = VisualTreeHelper.GetDpi(this);
            dest = new NativeMethods.Rect
            {
                Left = (int)Math.Floor(tl.X * dpi.DpiScaleX),
                Top = (int)Math.Floor(tl.Y * dpi.DpiScaleY),
                Right = (int)Math.Ceiling(br.X * dpi.DpiScaleX),
                Bottom = (int)Math.Ceiling(br.Y * dpi.DpiScaleY)
            };

            if (dest.Right <= dest.Left) dest.Right = dest.Left + 1;
            if (dest.Bottom <= dest.Top) dest.Bottom = dest.Top + 1;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
