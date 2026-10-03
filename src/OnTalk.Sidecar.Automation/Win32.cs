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
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

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

    internal static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
