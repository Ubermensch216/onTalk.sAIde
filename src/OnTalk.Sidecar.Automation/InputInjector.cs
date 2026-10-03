using System.Runtime.InteropServices;

namespace OnTalk.Sidecar.Automation;

internal enum FocusOutcome
{
    Focused,
    /// <summary>대상이 다른 창에 가려져 있고 앞으로 올리지 못했다.</summary>
    Covered,
    /// <summary>클릭했지만 대상 창이 앞에 오지 않았다.</summary>
    NotForeground,
    /// <summary>사용자가 Ctrl·Alt·Shift·Win 키를 누르고 있다.</summary>
    ModifierDown,
}

/// <summary>
/// 온톡 입력창에 글자를 넣기 위한 실제 마우스·키보드 입력. 글자 입력은 사용자가 하듯 클릭하고 붙여넣어야
/// 온톡 내부 상태가 바뀐다. 키 입력은 대상 창이 앞에 있을 때만 보낸다. Alt 조합(전송 단축키)은 보내지 않는다.
/// </summary>
internal static class InputInjector
{
    public const ushort KeyA = 0x41, KeyV = 0x56, KeyDelete = 0x2E, KeyEnter = 0x0D;
    private const ushort KeyControl = 0x11;

    /// <summary>입력창 가운데를 한 번 클릭해 대상 창을 앞으로 가져오고 입력창에 포커스를 준다. 마우스 위치는 되돌린다.</summary>
    public static FocusOutcome FocusByClick(IntPtr hwnd, AccBounds bounds)
    {
        if (AnyModifierDown())
        {
            return FocusOutcome.ModifierDown;
        }
        if (bounds.IsEmpty || Win32.FindRenderChild(hwnd) is not { } render)
        {
            return FocusOutcome.Covered;
        }
        var (x, y) = bounds.Center;
        var point = new Point { X = x, Y = y };
        if (!IsTarget(hwnd, render, WindowFromPoint(point)))
        {
            // 활성화 없이 맨 위로만 올린다. 다른 프로세스의 창이라 순서 변경이 바로 반영되지 않는다.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
            for (var wait = 0; wait < 15 && !IsTarget(hwnd, render, WindowFromPoint(point)); wait++)
            {
                Thread.Sleep(100);
            }
            if (!IsTarget(hwnd, render, WindowFromPoint(point)))
            {
                return FocusOutcome.Covered;
            }
        }
        GetCursorPos(out var cursor);
        SetCursorPos(point.X, point.Y);
        Mouse(MouseLeftDown);
        Mouse(MouseLeftUp);
        Thread.Sleep(350);
        SetCursorPos(cursor.X, cursor.Y);
        return Win32.GetForegroundWindow() == hwnd ? FocusOutcome.Focused : FocusOutcome.NotForeground;
    }

    /// <summary>포커스가 있는 입력창의 내용을 통째로 바꾼다. 클립보드는 빌려 쓰고 되돌린다.</summary>
    /// <returns>Typed: 키를 모두 보냈는지. ClipboardRestored: 클립보드를 원래대로 되돌렸는지.</returns>
    public static (bool Typed, bool ClipboardRestored) ReplaceText(IntPtr hwnd, string text)
    {
        if (text.Length == 0)
        {
            return (Chord(hwnd, KeyA) && Tap(hwnd, KeyDelete), true);
        }
        if (ClipboardSnapshot.Capture() is not { } clipboard || !clipboard.Borrow(text))
        {
            return (false, true);
        }
        var typed = Chord(hwnd, KeyA) && Chord(hwnd, KeyV);
        // 붙여넣기가 처리될 시간을 준 뒤 되돌린다. 너무 빨리 되돌리면 원래 내용이 붙여넣어진다.
        Thread.Sleep(250);
        return (typed, clipboard.Restore());
    }

    public static bool Chord(IntPtr hwnd, ushort key)
    {
        if (Win32.GetForegroundWindow() != hwnd)
        {
            return false;
        }
        Key(KeyControl, up: false);
        Key(key, up: false);
        Key(key, up: true);
        Key(KeyControl, up: true);
        Thread.Sleep(150);
        return true;
    }

    public static bool Tap(IntPtr hwnd, ushort key)
    {
        if (Win32.GetForegroundWindow() != hwnd)
        {
            return false;
        }
        Key(key, up: false);
        Key(key, up: true);
        Thread.Sleep(150);
        return true;
    }

    private static bool AnyModifierDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    private static bool IsTarget(IntPtr hwnd, IntPtr render, IntPtr under) => under == hwnd || under == render;

    private static void Key(ushort vk, bool up)
    {
        var input = new Input { Type = 1 };
        input.Data.Keyboard = new KeyboardInput { Vk = vk, Flags = up ? KeyUp : 0 };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void Mouse(uint flags)
    {
        var input = new Input { Type = 0 };
        input.Data.Mouse = new MouseInput { Flags = flags };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private const uint KeyUp = 0x0002, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
