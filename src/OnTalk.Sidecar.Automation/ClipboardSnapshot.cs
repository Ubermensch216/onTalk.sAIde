using System.Runtime.InteropServices;

namespace OnTalk.Sidecar.Automation;

/// <summary>
/// 붙여넣기에 클립보드를 잠시 빌려 쓰고 원래 내용을 되돌린다. 글자뿐 아니라 이미지·파일 목록 등
/// 메모리로 복사할 수 있는 모든 형식을 보존한다.
/// </summary>
internal sealed class ClipboardSnapshot
{
    private const uint CfUnicodeText = 13;
    private const long MaxBytes = 256L * 1024 * 1024;

    // 메모리 블록이 아니라 GDI 핸들로 전달되는 형식. 복사할 수 없지만 같은 내용이 CF_DIB 등으로 함께
    // 올라와 있으면 Windows가 되살려 준다.
    private static readonly HashSet<uint> HandleFormats = [2, 3, 9, 14];

    private readonly List<(uint Format, byte[] Data)> _items = [];
    private uint _sequenceAfterBorrow;

    /// <summary>보존하지 못한 형식이 있는지.</summary>
    public bool Incomplete { get; private set; }

    /// <summary>진단용. 형식 번호와 크기만 내놓고 내용은 내놓지 않는다.</summary>
    public IEnumerable<(uint Format, int Bytes)> Describe() => _items.Select(i => (i.Format, i.Data.Length));

    public bool SameContentAs(ClipboardSnapshot other) =>
        _items.Count == other._items.Count &&
        _items.All(a => other._items.Any(b => b.Format == a.Format && b.Data.AsSpan().SequenceEqual(a.Data)));

    /// <summary>현재 클립보드 내용을 떠 둔다. 클립보드를 열 수 없으면 null.</summary>
    public static ClipboardSnapshot? Capture()
    {
        if (!Open())
        {
            return null;
        }
        var snapshot = new ClipboardSnapshot();
        try
        {
            long total = 0;
            for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
            {
                if (HandleFormats.Contains(format) || format is >= 0x80 and <= 0x8E or >= 0x200 and <= 0x3FF)
                {
                    continue;
                }
                var handle = GetClipboardData(format);
                var size = handle == IntPtr.Zero ? 0 : (long)GlobalSize(handle);
                var pointer = size > 0 ? GlobalLock(handle) : IntPtr.Zero;
                if (pointer == IntPtr.Zero || (total += size) > MaxBytes)
                {
                    if (pointer != IntPtr.Zero)
                    {
                        GlobalUnlock(handle);
                    }
                    snapshot.Incomplete = true;
                    continue;
                }
                var data = new byte[size];
                Marshal.Copy(pointer, data, 0, data.Length);
                GlobalUnlock(handle);
                snapshot._items.Add((format, data));
            }
        }
        finally
        {
            CloseClipboard();
        }
        return snapshot;
    }

    /// <summary>붙여넣을 글자를 클립보드에 올린다. Windows 클립보드 기록과 클라우드 동기화에는 남기지 않는다.</summary>
    public bool Borrow(string text)
    {
        if (!Open())
        {
            return false;
        }
        try
        {
            EmptyClipboard();
            var bytes = new byte[(text.Length + 1) * 2];
            System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
            var ok = Put(CfUnicodeText, bytes);
            foreach (var name in (string[])["ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"])
            {
                Put(RegisterClipboardFormatW(name), new byte[4]);
            }
            return ok;
        }
        finally
        {
            CloseClipboard();
            _sequenceAfterBorrow = GetClipboardSequenceNumber();
        }
    }

    /// <summary>
    /// 떠 둔 내용을 되돌린다. 그 사이 사용자가 다른 것을 복사했다면 그 내용을 덮어쓰지 않고 false를 돌려준다.
    /// </summary>
    public bool Restore()
    {
        if (GetClipboardSequenceNumber() != _sequenceAfterBorrow || !Open())
        {
            return false;
        }
        try
        {
            EmptyClipboard();
            var ok = true;
            foreach (var (format, data) in _items)
            {
                ok &= Put(format, data);
            }
            return ok && !Incomplete;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool Put(uint format, byte[] data)
    {
        const uint movable = 0x0002;
        var handle = GlobalAlloc(movable, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        Marshal.Copy(data, 0, GlobalLock(handle), data.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) != IntPtr.Zero)
        {
            // 성공하면 메모리 소유권이 클립보드로 넘어간다.
            return true;
        }
        GlobalFree(handle);
        return false;
    }

    // 다른 프로그램이 클립보드를 잡고 있으면 잠깐 기다렸다 다시 연다.
    private static bool Open()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }
            Thread.Sleep(30);
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);
}
