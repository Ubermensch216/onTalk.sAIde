namespace OnTalk.Sidecar.Core;

/// <summary>화면 위의 사각형. 장치 픽셀 기준이며, 주 모니터 왼쪽·위쪽의 모니터에서는 좌표가 음수다.</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public enum DockSide
{
    Right,
    Left,
}

/// <param name="Overlaps">양옆 어디에도 자리가 없어 온톡 창 위에 겹쳐 놓았는지.</param>
public sealed record DockPlacement(ScreenRect Bounds, DockSide Side, bool Overlaps);

/// <summary>온톡 창 옆에 패널을 붙일 자리를 정한다.</summary>
public static class DockPlanner
{
    /// <param name="target">온톡 창.</param>
    /// <param name="workArea">온톡 창이 있는 모니터의 작업 영역.</param>
    /// <param name="overlapInset">겹쳐 놓을 때 온톡 창 위쪽에서 내려 놓는 거리. 온톡의 창 단추를 가리지 않기 위한 값이다.</param>
    public static DockPlacement Place(ScreenRect target, ScreenRect workArea, double width, double height,
        double overlapInset = 0)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0 || width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }
        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);

        var rightSpace = workArea.Right - target.Right;
        var leftSpace = target.X - workArea.X;
        var fits = rightSpace >= width || leftSpace >= width;
        // 오른쪽을 먼저 쓴다. 어느 쪽에도 자리가 없으면 더 넓은 쪽 가장자리에 겹친다.
        var side = rightSpace >= width || (leftSpace < width && rightSpace >= leftSpace) ? DockSide.Right : DockSide.Left;

        var x = side == DockSide.Right ? target.Right : target.X - width;
        x = Math.Clamp(x, workArea.X, workArea.Right - width);
        var y = Math.Clamp(target.Y + (fits ? 0 : overlapInset), workArea.Y, workArea.Bottom - height);
        return new DockPlacement(new ScreenRect(x, y, width, height), side, !fits);
    }
}
