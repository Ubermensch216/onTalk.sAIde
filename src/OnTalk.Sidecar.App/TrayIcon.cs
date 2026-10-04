using System.Runtime.InteropServices;

namespace OnTalk.Sidecar.App;

/// <summary>
/// Windows 알림 영역의 아이콘. 패널이 숨어 있을 때 다시 여는 길이다.
/// 아이콘을 누르면 패널 창으로 <see cref="CallbackMessage"/>가 온다.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public const int CallbackMessage = 0x8000 + 7;

    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private static readonly IntPtr IdiApplication = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint action, ref NotifyData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    private NotifyData _data;

    /// <summary>아이콘을 올리지 못했으면 패널을 숨기지 않아야 한다.</summary>
    public bool IsAvailable { get; private set; }

    public TrayIcon(IntPtr window)
    {
        _data = new NotifyData
        {
            Size = (uint)Marshal.SizeOf<NotifyData>(),
            Window = window,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            Callback = CallbackMessage,
            Icon = LoadIconW(IntPtr.Zero, IdiApplication),
            Tip = "onTalk.sAIde · 온톡 옆의 비서",
            Info = "",
            InfoTitle = "",
        };
        IsAvailable = Shell_NotifyIconW(NimAdd, ref _data);
    }

    public void Dispose()
    {
        if (IsAvailable)
        {
            Shell_NotifyIconW(NimDelete, ref _data);
            IsAvailable = false;
        }
    }
}
