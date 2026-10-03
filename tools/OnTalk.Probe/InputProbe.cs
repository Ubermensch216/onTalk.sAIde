using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace OnTalk.Probe;

/// <summary>
/// 빈 "새 쪽지" 창에 시험 문장을 붙여넣어 본문과 바이트 표시에 반영되는지 본다.
/// 보내는 입력은 입력창 클릭 1회, Ctrl+A, Ctrl+V, Delete뿐이다. 전송(Alt+S)은 보내지 않는다.
/// </summary>
internal static class InputProbe
{
    private static readonly (string Label, string Text)[] Cases =
    [
        ("ASCII 3자", "abc"),
        ("한글 3자", "가나다"),
        ("줄바꿈 1개", "a\nb"),
        ("줄바꿈 2개", "a\n\nb"),
        ("기호 ㅇ□", "ㅇ□"),
        ("체크 ✅", "✅"),
        ("체크+변형 ✔️", "✔️"),
        ("이모지 😀", "😀"),
        ("혼합", "안녕하세요. Test 123\n둘째 줄 ✅"),
    ];

    public static int Run(Win window, Func<List<Node>> read)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp949 = Encoding.GetEncoding(949);

        var nodes = read();
        var field = nodes.FirstOrDefault(n => n.Role == "text" && n.Name.StartsWith("쪽지 내용", StringComparison.Ordinal));
        if (!nodes.Any(n => n.Role == "statictext" && n.Name == "새 쪽지") || field is null ||
            field.Value.Length > 0 || Counter(nodes) != "0/4000 Bytes")
        {
            Console.WriteLine("중단: 본문이 비어 있는 '새 쪽지' 창이 아닙니다.");
            return 3;
        }
        if (AnyModifierDown())
        {
            Console.WriteLine("중단: Ctrl/Alt/Shift/Win 키가 눌려 있습니다.");
            return 3;
        }
        var point = new POINT { X = field.X + field.W / 2, Y = field.Y + field.H / 2 };
        if (!IsTarget(window, WindowFromPoint(point)))
        {
            // 다른 창에 가려져 있으면 활성화 없이 맨 위로만 올린다.
            SetWindowPos(window.Hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Thread.Sleep(300);
        }
        if (!IsTarget(window, WindowFromPoint(point)))
        {
            Console.WriteLine("중단: 입력창이 다른 창에 가려져 있습니다.");
            return 3;
        }

        GetCursorPos(out var cursor);
        SetCursorPos(point.X, point.Y);
        Mouse(MOUSEEVENTF_LEFTDOWN);
        Mouse(MOUSEEVENTF_LEFTUP);
        Thread.Sleep(400);
        SetCursorPos(cursor.X, cursor.Y);
        if (GetForegroundWindow() != window.Hwnd)
        {
            Console.WriteLine("중단: 클릭 후에도 대상 창이 앞에 오지 않았습니다. 입력 주입이 막혔을 수 있습니다.");
            return 4;
        }
        Console.WriteLine("클릭으로 대상 창 활성화: 성공");

        var saved = Clipboard.GetDataObject() is { } data && data.GetDataPresent(DataFormats.UnicodeText)
            ? Clipboard.GetText()
            : null;
        var hadOther = saved is null && Clipboard.GetDataObject()?.GetFormats().Length > 0;
        try
        {
            Console.WriteLine($"{"사례",-14} {"UTF-8",5} {"CP949",5}  온톡 표시        본문 일치");
            foreach (var (label, text) in Cases)
            {
                Clipboard.SetText(text);
                if (!Chord(window, VK_A) || !Chord(window, VK_V))
                {
                    Console.WriteLine("중단: 대상 창이 앞에 있지 않습니다.");
                    return 4;
                }
                Thread.Sleep(700);
                var after = read();
                var value = after.FirstOrDefault(n => n.Role == "text")?.Value ?? "";
                var same = value.Replace("\r\n", "\n") == text;
                Console.WriteLine(
                    $"{label,-14} {Encoding.UTF8.GetByteCount(text),5} {cp949.GetByteCount(text),5}  {Counter(after),-14} {(same ? "일치" : $"불일치({value.Length}자)")}");
            }
        }
        finally
        {
            if (GetForegroundWindow() == window.Hwnd && !AnyModifierDown())
            {
                Chord(window, VK_A);
                Key(VK_DELETE, false);
                Key(VK_DELETE, true);
                Thread.Sleep(500);
            }
            if (saved is not null)
            {
                Clipboard.SetText(saved);
            }
            else
            {
                Clipboard.Clear();
            }
            Console.WriteLine($"정리 후 표시: {Counter(read())}" + (hadOther ? " (클립보드에 글자가 아닌 내용이 있었고 복원하지 못했습니다)" : ""));
        }
        return 0;
    }

    private static bool IsTarget(Win window, IntPtr hwnd) => hwnd == window.Hwnd || window.Renders.Contains(hwnd);

    private static string Counter(List<Node> nodes) =>
        nodes.FirstOrDefault(n => n.Role == "statictext" && n.Name.EndsWith(" Bytes", StringComparison.Ordinal))?.Name ?? "(없음)";

    // 대상 창이 앞에 있을 때만 Ctrl+키를 보낸다.
    private static bool Chord(Win window, ushort key)
    {
        if (GetForegroundWindow() != window.Hwnd)
        {
            return false;
        }
        Key(VK_CONTROL, false);
        Key(key, false);
        Key(key, true);
        Key(VK_CONTROL, true);
        Thread.Sleep(150);
        return true;
    }

    private static bool AnyModifierDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    private static void Key(ushort vk, bool up)
    {
        var input = new INPUT { Type = 1 };
        input.Data.Keyboard = new KEYBDINPUT { Vk = vk, Flags = up ? KEYEVENTF_KEYUP : 0 };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static void Mouse(uint flags)
    {
        var input = new INPUT { Type = 0 };
        input.Data.Mouse = new MOUSEINPUT { Flags = flags };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private const ushort VK_CONTROL = 0x11, VK_A = 0x41, VK_V = 0x56, VK_DELETE = 0x2E;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    private const uint KEYEVENTF_KEYUP = 0x0002, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
