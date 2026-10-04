using System.Diagnostics;
using System.Runtime.InteropServices;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

/// <param name="Window">온톡 창의 보이는 테두리. 장치 픽셀.</param>
/// <param name="WorkArea">그 창이 있는 모니터의 작업 영역.</param>
/// <param name="Scale">그 창의 화면 배율. 100%가 1.</param>
public readonly record struct DockTarget(IntPtr Hwnd, ScreenRect Window, ScreenRect WorkArea, double Scale);

/// <summary>
/// 패널이 붙을 온톡 창을 고른다. 사용자가 마지막으로 쓴 온톡 창(메인 창이든 쪽지 창이든)을 따라간다.
/// 창의 위치만 읽고 온톡을 조작하지 않는다.
/// </summary>
public sealed class OnTalkDock
{
    private const string ProcessName = "온톡";
    private const long ProcessRefreshMs = 3000;
    // 알림 풍선 같은 작은 창에는 붙지 않는다.
    private const int MinDockSize = 240;

    private HashSet<uint> _pids = [];
    private long _refreshedAt = long.MinValue;

    /// <summary>지금 붙어 있는 온톡 창. 없으면 <see cref="IntPtr.Zero"/>.</summary>
    public IntPtr Target { get; private set; }

    /// <summary>사용자가 지금 온톡 창을 쓰고 있는지.</summary>
    public bool OnTalkForeground { get; private set; }

    /// <summary>사용자가 지금 이 프로그램의 창을 쓰고 있는지.</summary>
    public bool OwnForeground { get; private set; }

    public DockTarget? Poll()
    {
        RefreshProcesses();
        var foreground = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(foreground, out var pid);
        OwnForeground = pid == (uint)Environment.ProcessId;
        OnTalkForeground = _pids.Contains(pid);

        if (OnTalkForeground && IsDockable(foreground))
        {
            Target = foreground;
        }
        else if (Target != IntPtr.Zero && !(IsOnTalk(Target) && IsDockable(Target)))
        {
            Target = IntPtr.Zero;
        }
        return Target == IntPtr.Zero ? null : Describe(Target);
    }

    /// <summary>붙어 있는 창이 없을 때, 화면에 보이는 온톡 창 가운데 가장 큰 것에 붙는다.</summary>
    public void Adopt()
    {
        if (Target != IntPtr.Zero)
        {
            return;
        }
        _refreshedAt = long.MinValue;
        RefreshProcesses();
        var best = IntPtr.Zero;
        long bestArea = 0;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (IsOnTalk(hwnd) && IsDockable(hwnd) && Win32.GetWindowRect(hwnd, out var rect) &&
                (long)rect.Width * rect.Height > bestArea)
            {
                best = hwnd;
                bestArea = (long)rect.Width * rect.Height;
            }
            return true;
        }, IntPtr.Zero);
        Target = best;
    }

    private void RefreshProcesses()
    {
        var now = Environment.TickCount64;
        if (_refreshedAt != long.MinValue && now - _refreshedAt < ProcessRefreshMs)
        {
            return;
        }
        _refreshedAt = now;
        var pids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                pids.Add((uint)process.Id);
            }
        }
        _pids = pids;
    }

    private bool IsOnTalk(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd))
        {
            return false;
        }
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        return _pids.Contains(pid);
    }

    private static bool IsDockable(IntPtr hwnd) =>
        Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd) &&
        Win32.ClassName(hwnd).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) &&
        Win32.GetWindowRect(hwnd, out var rect) && rect.Width >= MinDockSize && rect.Height >= MinDockSize;

    private static DockTarget? Describe(IntPtr hwnd)
    {
        // 창 테두리의 보이지 않는 여백을 뺀 자리에 붙여야 틈이 생기지 않는다.
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<Win32.Rect>()) != 0 &&
            !Win32.GetWindowRect(hwnd, out rect))
        {
            return null;
        }
        var monitor = new Win32.MonitorInfo { Size = Marshal.SizeOf<Win32.MonitorInfo>() };
        if (!Win32.GetMonitorInfoW(Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST), ref monitor))
        {
            return null;
        }
        var dpi = Win32.GetDpiForWindow(hwnd);
        return new DockTarget(hwnd, ToScreenRect(rect), ToScreenRect(monitor.Work), dpi > 0 ? dpi / 96.0 : 1);
    }

    private static ScreenRect ToScreenRect(Win32.Rect rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);
}
