using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

public sealed class DockPlannerTests
{
    private static readonly ScreenRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void 오른쪽에_자리가_있으면_온톡_창_오른쪽에_틈_없이_붙는다()
    {
        var placement = DockPlanner.Place(new ScreenRect(400, 100, 480, 700), Work, 360, 700);

        Assert.Equal(new ScreenRect(880, 100, 360, 700), placement.Bounds);
        Assert.Equal(DockSide.Right, placement.Side);
        Assert.False(placement.Overlaps);
    }

    [Fact]
    public void 오른쪽이_좁으면_왼쪽에_붙는다()
    {
        var placement = DockPlanner.Place(new ScreenRect(1400, 100, 480, 700), Work, 360, 700);

        Assert.Equal(new ScreenRect(1040, 100, 360, 700), placement.Bounds);
        Assert.Equal(DockSide.Left, placement.Side);
        Assert.False(placement.Overlaps);
    }

    [Fact]
    public void 양쪽에_자리가_없으면_창_단추_아래에_겹쳐_놓는다()
    {
        var placement = DockPlanner.Place(Work, Work, 44, 180, overlapInset: 96);

        Assert.Equal(new ScreenRect(1876, 96, 44, 180), placement.Bounds);
        Assert.True(placement.Overlaps);
    }

    [Fact]
    public void 주_모니터_왼쪽의_모니터에서도_그_모니터_안에_둔다()
    {
        var work = new ScreenRect(-1920, 0, 1920, 1040);

        var placement = DockPlanner.Place(new ScreenRect(-600, 50, 500, 600), work, 360, 600);

        Assert.Equal(new ScreenRect(-960, 50, 360, 600), placement.Bounds);
        Assert.Equal(DockSide.Left, placement.Side);
    }

    [Fact]
    public void 작업_영역보다_크거나_벗어난_자리는_작업_영역_안으로_당긴다()
    {
        var placement = DockPlanner.Place(new ScreenRect(100, 800, 480, 700), Work, 360, 1200);

        Assert.Equal(new ScreenRect(580, 0, 360, 1040), placement.Bounds);
    }
}
