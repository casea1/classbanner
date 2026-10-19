using ClassBanner.Core;

namespace ClassBanner.Core.Tests;

public class LayoutTests
{
    private static readonly MonitorInfo Primary = new(@"\\.\DISPLAY1", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, true);
    private static readonly MonitorInfo Secondary = new(@"\\.\DISPLAY2", new(1920, 0, 4480, 1440), new(1920, 0, 4480, 1440), 144, false);

    private static List<ExistingBanner> InPlace(IReadOnlyList<BannerSlot> slots) =>
        slots.Select((s, i) => new ExistingBanner(i, s, s.Strip)).ToList();

    [Fact]
    public void Both_edges_on_two_monitors_gives_four_slots_two_per_monitor()
    {
        var slots = BannerLayout.ComputeSlots([Primary, Secondary], BannerPosition.Both, 10);

        Assert.Equal(4, slots.Count);
        Assert.Equal(2, slots.Count(s => s.DeviceName == Primary.DeviceName));
        Assert.Equal(2, slots.Count(s => s.DeviceName == Secondary.DeviceName));
    }

    [Theory]
    [InlineData(BannerPosition.Top, BannerEdge.Top)]
    [InlineData(BannerPosition.Bottom, BannerEdge.Bottom)]
    public void Single_edge_positions(BannerPosition position, BannerEdge edge)
    {
        var slots = BannerLayout.ComputeSlots([Primary], position, 10);
        Assert.Single(slots);
        Assert.Equal(edge, slots[0].Edge);
    }

    [Theory]
    [InlineData(40, 96, 40)]
    [InlineData(40, 144, 60)]
    [InlineData(40, 192, 80)]
    [InlineData(12, 96, 12)]
    public void Custom_thickness_is_pixels_at_100_percent_scaled_per_monitor(int thickness, int dpi, int expected)
    {
        Assert.Equal(expected, BannerLayout.BannerHeight(10, dpi, thickness));
        Assert.All(BannerLayout.ComputeSlots([Primary with { Dpi = dpi }], BannerPosition.Both, 10, thickness), s => Assert.Equal(expected, s.Height));
    }

    [Fact]
    public void Zero_thickness_is_automatic()
    {
        Assert.Equal(BannerLayout.BannerHeight(10, 96), BannerLayout.BannerHeight(10, 96, 0));
    }

    [Fact]
    public void Text_shrinks_to_fit_a_thin_banner_but_never_grows()
    {
        Assert.Equal(BannerLayout.FontPixels(10, 96), BannerLayout.TextPixels(10, 96, 80));
        Assert.True(BannerLayout.TextPixels(24, 96, 16) <= 12);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("12", true)]
    [InlineData("120", true)]
    [InlineData("11", false)]
    [InlineData("121", false)]
    [InlineData("thick", false)]
    public void Thickness_setting_is_range_checked(string value, bool valid)
    {
        Assert.Equal(valid, SettingKeys.TryApply(new BannerSettings(), "BannerThickness", value));
    }

    [Fact]
    public void Height_scales_with_dpi_so_banner_looks_the_same_at_any_zoom()
    {
        var at100 = BannerLayout.BannerHeight(10, 96);
        var at150 = BannerLayout.BannerHeight(10, 144);
        var at200 = BannerLayout.BannerHeight(10, 192);

        Assert.InRange(at100, 20, 30);
        Assert.InRange(at150 / (double)at100, 1.4, 1.6);
        Assert.InRange(at200 / (double)at100, 1.9, 2.1);
    }

    [Fact]
    public void Each_monitor_uses_its_own_dpi()
    {
        var slots = BannerLayout.ComputeSlots([Primary, Secondary], BannerPosition.Top, 10);
        Assert.True(slots.Single(s => s.DeviceName == Secondary.DeviceName).Height > slots.Single(s => s.DeviceName == Primary.DeviceName).Height);
    }

    [Fact]
    public void Strip_hugs_the_requested_edge()
    {
        var slots = BannerLayout.ComputeSlots([Secondary], BannerPosition.Both, 10);
        var top = slots.Single(s => s.Edge == BannerEdge.Top).Strip;
        var bottom = slots.Single(s => s.Edge == BannerEdge.Bottom).Strip;

        Assert.Equal(new ScreenRect(1920, 0, 4480, top.Height), top);
        Assert.Equal(1440, bottom.Bottom);
        Assert.Equal(1920, bottom.Left);
    }

    [Fact]
    public void Reconcile_is_a_noop_when_everything_is_in_place()
    {
        var slots = BannerLayout.ComputeSlots([Primary, Secondary], BannerPosition.Both, 10);
        Assert.True(BannerLayout.Reconcile(slots, InPlace(slots)).IsNoOp);
    }

    [Fact]
    public void Bottom_banner_pushed_above_the_taskbar_is_still_in_place()
    {
        var slot = BannerLayout.ComputeSlots([Primary], BannerPosition.Bottom, 10)[0];
        var aboveTaskbar = new ScreenRect(0, 1040 - slot.Height, 1920, 1040);

        Assert.True(BannerLayout.IsInPlace(new ExistingBanner(0, slot, aboveTaskbar)));
    }

    /// <summary>
    /// Banner 3.1 bug: after unlock, the second monitor's banners were sitting on the main display
    /// (4 on the main screen, 0 on the second). Windows moved them there while the display was asleep/disconnected.
    /// </summary>
    [Fact]
    public void Unlock_bug_secondary_banners_moved_to_primary_are_put_back()
    {
        var slots = BannerLayout.ComputeSlots([Primary, Secondary], BannerPosition.Both, 10);
        var existing = InPlace(slots);

        // Windows dumped the secondary monitor's banners onto the primary.
        for (var i = 0; i < existing.Count; i++)
        {
            var b = existing[i];
            if (b.Slot.DeviceName != Secondary.DeviceName) continue;
            var onPrimary = b.Slot.Edge == BannerEdge.Top
                ? new ScreenRect(0, 0, 1920, b.Slot.Height)
                : new ScreenRect(0, 1080 - b.Slot.Height, 1920, 1080);
            existing[i] = b with { ActualRect = onPrimary };
        }

        var plan = BannerLayout.Reconcile(slots, existing);

        Assert.False(plan.RebuildAll);
        Assert.Equal(
            existing.Where(b => b.Slot.DeviceName == Secondary.DeviceName).Select(b => b.Id).Order(),
            plan.Reposition.Order());
    }

    [Fact]
    public void Monitor_that_disappears_and_returns_with_new_bounds_triggers_rebuild()
    {
        var before = BannerLayout.ComputeSlots([Primary, Secondary], BannerPosition.Both, 10);
        var movedSecondary = Secondary with { Bounds = new(-2560, 0, 0, 1440), WorkArea = new(-2560, 0, 0, 1440) };
        var after = BannerLayout.ComputeSlots([Primary, movedSecondary], BannerPosition.Both, 10);

        Assert.True(BannerLayout.Reconcile(after, InPlace(before)).RebuildAll);
    }

    [Fact]
    public void Monitor_unplugged_triggers_rebuild()
    {
        var before = BannerLayout.ComputeSlots([Primary, Secondary], BannerPosition.Both, 10);
        var after = BannerLayout.ComputeSlots([Primary], BannerPosition.Both, 10);

        var plan = BannerLayout.Reconcile(after, InPlace(before));
        Assert.True(plan.RebuildAll);
    }

    [Fact]
    public void Dpi_change_triggers_rebuild()
    {
        var before = BannerLayout.ComputeSlots([Primary], BannerPosition.Both, 10);
        var after = BannerLayout.ComputeSlots([Primary with { Dpi = 120 }], BannerPosition.Both, 10);

        Assert.True(BannerLayout.Reconcile(after, InPlace(before)).RebuildAll);
    }

    [Fact]
    public void Duplicate_banners_trigger_rebuild()
    {
        var slots = BannerLayout.ComputeSlots([Primary, Secondary], BannerPosition.Top, 10);
        var existing = new List<ExistingBanner>
        {
            new(0, slots[0], slots[0].Strip),
            new(1, slots[0], slots[0].Strip),
        };

        Assert.True(BannerLayout.Reconcile(slots, existing).RebuildAll);
    }

    [Fact]
    public void Banner_narrowed_by_a_taskbar_docked_on_the_side_is_still_in_place()
    {
        var slot = BannerLayout.ComputeSlots([Primary], BannerPosition.Top, 10)[0];
        var besideLeftTaskbar = slot.Strip with { Left = 62 };

        Assert.True(BannerLayout.IsInPlace(new ExistingBanner(0, slot, besideLeftTaskbar)));
    }

    [Fact]
    public void Banner_with_wrong_height_is_repositioned()
    {
        var slot = BannerLayout.ComputeSlots([Primary], BannerPosition.Top, 10)[0];
        var squashed = slot.Strip with { Bottom = slot.Strip.Top + 3 };

        Assert.False(BannerLayout.IsInPlace(new ExistingBanner(0, slot, squashed)));
    }
}
