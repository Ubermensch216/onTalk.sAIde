using System.Runtime.InteropServices;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

public enum ApplyStatus
{
    /// <summary>반영 후 다시 읽은 본문과 바이트 표시가 초안과 일치한다.</summary>
    Applied,
    /// <summary>작성 창이 아니거나 닫혔다.</summary>
    NotCompose,
    /// <summary>예약 설정 같은 대화상자가 떠 있다.</summary>
    DialogOpen,
    /// <summary>온톡 창의 본문이 마지막으로 맞춘 내용과 다르다. 사용자가 온톡에서 직접 고쳤다.</summary>
    Conflict,
    TooLong,
    /// <summary>입력창이 다른 창에 가려져 있고 앞으로 올리지 못했다.</summary>
    Covered,
    /// <summary>클릭했지만 대상 창이 앞에 오지 않았다. 입력 주입이 막혔을 수 있다.</summary>
    FocusFailed,
    /// <summary>사용자가 Ctrl·Alt·Shift·Win 키를 누르고 있다.</summary>
    ModifierDown,
    ClipboardBusy,
    /// <summary>붙여넣은 뒤 다시 읽은 내용이 초안과 다르다. 다시 시도하지 않는다.</summary>
    Mismatch,
}

/// <param name="After">반영 뒤 다시 읽은 작성 창 상태.</param>
/// <param name="ClipboardRestored">클립보드를 원래대로 되돌렸는지. 클립보드를 건드리지 않았으면 true.</param>
public sealed record ApplyResult(ApplyStatus Status, ComposeState? After, bool ClipboardRestored = true);

/// <summary>
/// 온톡 작성 창의 본문을 초안으로 바꾼다. 값을 직접 써넣지 않고 사용자가 하듯 클릭하고 붙여넣는다.
/// 값을 직접 써넣으면 화면에는 보이지만 온톡 내부 상태가 바뀌지 않아 빈 쪽지가 나갈 수 있다.
/// 보내는 입력은 입력창 클릭, Ctrl+A, Ctrl+V, Delete뿐이다. 전송은 하지 않는다.
/// </summary>
public static class ComposeWriter
{
    public static ComposeState? Read(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd) || Win32.FindRenderChild(hwnd) is not { } render ||
            MsaaReader.ReadTree(render) is not { } tree || !NoteWindowParser.IsNotePage(tree))
        {
            return null;
        }
        return NoteWindowParser.Parse(tree, DateTimeOffset.Now).Compose;
    }

    /// <param name="expectedCurrentBody">
    /// 마지막으로 온톡과 맞춘 본문. 지금 온톡 본문이 이와 다르면 덮어쓰지 않고 <see cref="ApplyStatus.Conflict"/>를
    /// 돌려준다. null이면 확인 없이 덮어쓴다(사용자가 덮어쓰기를 고른 경우).
    /// </param>
    public static ApplyResult ApplyBody(IntPtr hwnd, string body, string? expectedCurrentBody)
    {
        body = body.Replace("\r\n", "\n");
        if (Read(hwnd) is not { } before)
        {
            return new ApplyResult(ApplyStatus.NotCompose, null);
        }
        if (before.DialogOpen)
        {
            return new ApplyResult(ApplyStatus.DialogOpen, before);
        }
        if (expectedCurrentBody is not null && before.Body != expectedCurrentBody.Replace("\r\n", "\n"))
        {
            return new ApplyResult(ApplyStatus.Conflict, before);
        }
        if (NoteBytes.Count(body) > before.LimitBytes)
        {
            return new ApplyResult(ApplyStatus.TooLong, before);
        }
        if (before.Body == body)
        {
            return new ApplyResult(ApplyStatus.Applied, before);
        }
        if (AnyModifierDown())
        {
            return new ApplyResult(ApplyStatus.ModifierDown, before);
        }

        var render = Win32.FindRenderChild(hwnd)!.Value;
        var (x, y) = before.BodyBounds.Center;
        var point = new Point { X = x, Y = y };
        if (before.BodyBounds.IsEmpty)
        {
            return new ApplyResult(ApplyStatus.Covered, before);
        }
        if (!IsTarget(hwnd, render, WindowFromPoint(point)))
        {
            // 활성화 없이 맨 위로만 올린다.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
            // 다른 프로세스의 창이라 순서 변경이 바로 반영되지 않는다. 0.3초로는 모자란 경우가 있었다.
            for (var wait = 0; wait < 15 && !IsTarget(hwnd, render, WindowFromPoint(point)); wait++)
            {
                Thread.Sleep(100);
            }
            if (!IsTarget(hwnd, render, WindowFromPoint(point)))
            {
                return new ApplyResult(ApplyStatus.Covered, before);
            }
        }

        GetCursorPos(out var cursor);
        SetCursorPos(point.X, point.Y);
        Mouse(MouseLeftDown);
        Mouse(MouseLeftUp);
        Thread.Sleep(350);
        SetCursorPos(cursor.X, cursor.Y);
        if (Win32.GetForegroundWindow() != hwnd)
        {
            return new ApplyResult(ApplyStatus.FocusFailed, Read(hwnd));
        }

        if (body.Length == 0)
        {
            var cleared = Chord(hwnd, KeyA) && Tap(hwnd, KeyDelete);
            return Verify(hwnd, body, cleared, clipboardRestored: true);
        }
        if (ClipboardSnapshot.Capture() is not { } clipboard || !clipboard.Borrow(body))
        {
            return new ApplyResult(ApplyStatus.ClipboardBusy, Read(hwnd));
        }
        var typed = Chord(hwnd, KeyA) && Chord(hwnd, KeyV);
        var result = Verify(hwnd, body, typed, clipboardRestored: true);
        return result with { ClipboardRestored = clipboard.Restore() };
    }

    // 붙여넣기가 화면에 반영될 때까지 잠깐 기다리며 다시 읽는다.
    private static ApplyResult Verify(IntPtr hwnd, string body, bool typed, bool clipboardRestored)
    {
        if (!typed)
        {
            return new ApplyResult(ApplyStatus.FocusFailed, Read(hwnd), clipboardRestored);
        }
        ComposeState? after = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(200);
            after = Read(hwnd);
            if (after is not null && after.Body == body && after.UsedBytes == NoteBytes.Count(body))
            {
                return new ApplyResult(ApplyStatus.Applied, after, clipboardRestored);
            }
        }
        return new ApplyResult(after is null ? ApplyStatus.NotCompose : ApplyStatus.Mismatch, after, clipboardRestored);
    }

    private static bool IsTarget(IntPtr hwnd, IntPtr render, IntPtr under) => under == hwnd || under == render;

    // 대상 창이 앞에 있을 때만 키를 보낸다. 다른 창으로 입력이 새지 않게 한다.
    private static bool Chord(IntPtr hwnd, ushort key)
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

    private static bool Tap(IntPtr hwnd, ushort key)
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

    private const ushort KeyControl = 0x11, KeyA = 0x41, KeyV = 0x56, KeyDelete = 0x2E;
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
