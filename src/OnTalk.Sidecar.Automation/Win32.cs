using System.Runtime.InteropServices;
using System.Text;
using Accessibility;

namespace OnTalk.Sidecar.Automation;

public static class Win32
{
    internal const uint WM_GETOBJECT = 0x003D;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;
    internal const uint OBJID_CLIENT = 0xFFFFFFFC;

    // Chromium이 스크린리더 감지용으로 쓰는 객체 ID. 보조기술이 이 ID로 WM_GETOBJECT를
    // 보내면 브라우저 프로세스의 접근성 트리가 켜진다.
    internal const int ScreenReaderHoneyPotId = 1;

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder sb, int max);

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessageTimeoutW(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    [DllImport("oleacc.dll")]
    internal static extern int AccessibleObjectFromWindow(
        IntPtr hwnd, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? acc);

    [DllImport("oleacc.dll")]
    internal static extern int AccessibleChildren(
        IAccessible container, int start, int count, [Out] object[] children, out int obtained);

    /// <summary>화면에 보이는 온톡 창과 그 안의 렌더 창.</summary>
    internal static List<(IntPtr Hwnd, IntPtr Render)> VisibleOnTalkWindows()
    {
        var pids = System.Diagnostics.Process.GetProcessesByName("온톡").Select(p => (uint)p.Id).ToHashSet();
        var found = new List<(IntPtr, IntPtr)>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid) && IsWindowVisible(hwnd) && !IsIconic(hwnd) &&
                ClassName(hwnd).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) &&
                FindRenderChild(hwnd) is { } render)
            {
                found.Add((hwnd, render));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>온톡 창 안에서 웹 화면을 그리는 자식 창. 접근성 트리는 이 창에서 읽는다.</summary>
    internal static IntPtr? FindRenderChild(IntPtr hwnd)
    {
        IntPtr? found = null;
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (ClassName(child) != "Chrome_RenderWidgetHostHWND")
            {
                return true;
            }
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    internal static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
