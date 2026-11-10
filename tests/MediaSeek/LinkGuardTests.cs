using System;
using WorkflowDashboard;
using Xunit;

public class LinkGuardTests
{
    [Fact]
    public void Yield_suppressed_during_pane_click_quiet()
    {
        var now = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(LiveLinkGuard.ShouldSuppressOutsideYield(now, now.AddMilliseconds(900), DateTime.MinValue));
    }

    [Fact]
    public void Yield_suppressed_during_link_quiet()
    {
        var now = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(LiveLinkGuard.ShouldSuppressOutsideYield(now, DateTime.MinValue, now.AddMilliseconds(1500)));
    }

    [Fact]
    public void Yield_allowed_after_both_quiet_windows()
    {
        var now = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(LiveLinkGuard.ShouldSuppressOutsideYield(now, now.AddMilliseconds(-1), now.AddMilliseconds(-1)));
    }


    [Fact]
    public void Yield_suppressed_during_link_interaction_quiet()
    {
        var now = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(LiveLinkGuard.ShouldSuppressOutsideYield(
            now, DateTime.MinValue, DateTime.MinValue, now.AddMilliseconds(2500)));
    }

    [Fact]
    public void Yield_allowed_when_only_interaction_quiet_has_expired()
    {
        var now = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(LiveLinkGuard.ShouldSuppressOutsideYield(
            now, DateTime.MinValue, DateTime.MinValue, now.AddMilliseconds(-1)));
    }

    [Fact]
    public void Collapse_skipped_only_while_link_quiet()
    {
        var now = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(LiveLinkGuard.ShouldSkipCollapseDuringLinkSettle(now, now.AddMilliseconds(1500)));
        Assert.False(LiveLinkGuard.ShouldSkipCollapseDuringLinkSettle(now, now.AddMilliseconds(-1)));
    }
}

public class LinkTargetIndicatorTests
{
    [Fact]
    public void Full_opacity_during_hold()
    {
        Assert.Equal(1, LinkTargetIndicator.OpacityAt(0));
        Assert.Equal(1, LinkTargetIndicator.OpacityAt(LinkTargetIndicator.HoldMs - 1));
    }

    [Fact]
    public void Zero_after_hold_and_fade()
    {
        Assert.Equal(0, LinkTargetIndicator.OpacityAt(LinkTargetIndicator.HoldMs + LinkTargetIndicator.FadeMs));
        Assert.Equal(0, LinkTargetIndicator.OpacityAt(LinkTargetIndicator.HoldMs + LinkTargetIndicator.FadeMs + 500));
    }

    [Fact]
    public void Mid_fade_is_half()
    {
        double mid = LinkTargetIndicator.OpacityAt(LinkTargetIndicator.HoldMs + LinkTargetIndicator.FadeMs / 2.0);
        Assert.InRange(mid, 0.49, 0.51);
    }

    [Fact]
    public void Immediate_fade_when_hold_is_zero()
    {
        Assert.Equal(1, LinkTargetIndicator.OpacityAt(0, holdMs: 0, fadeMs: 400));
        Assert.Equal(0.5, LinkTargetIndicator.OpacityAt(200, holdMs: 0, fadeMs: 400), 3);
        Assert.Equal(0, LinkTargetIndicator.OpacityAt(400, holdMs: 0, fadeMs: 400));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Invalid_elapsed_is_hidden(double elapsed)
        => Assert.Equal(0, LinkTargetIndicator.OpacityAt(elapsed));

    [Fact]
    public void IsVisible_threshold()
    {
        Assert.True(LinkTargetIndicator.IsVisible(1));
        Assert.True(LinkTargetIndicator.IsVisible(0.02));
        Assert.False(LinkTargetIndicator.IsVisible(0.01));
        Assert.False(LinkTargetIndicator.IsVisible(0));
    }

    [Fact]
    public void Stroke_is_thin_and_soft()
    {
        Assert.InRange(LinkTargetIndicator.Thickness, 1.5, 2.0);
        Assert.InRange(LinkTargetIndicator.PeakOpacity, 0.55, 0.70);
    }

    [Fact]
    public void Combined_opacity_peaks_softly_then_fades()
    {
        Assert.Equal(LinkTargetIndicator.PeakOpacity, LinkTargetIndicator.CombinedOpacity(0), 3);
        Assert.Equal(LinkTargetIndicator.PeakOpacity * 0.5,
            LinkTargetIndicator.CombinedOpacity(LinkTargetIndicator.HoldMs + LinkTargetIndicator.FadeMs / 2.0), 3);
        Assert.Equal(0, LinkTargetIndicator.CombinedOpacity(LinkTargetIndicator.HoldMs + LinkTargetIndicator.FadeMs));
    }
}
