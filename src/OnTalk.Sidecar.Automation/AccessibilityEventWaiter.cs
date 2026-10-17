using System.Runtime.InteropServices;

namespace OnTalk.Sidecar.Automation;

/// <summary>
/// 온톡이 접근성 이벤트(선택·포커스·표시·이름 변경 등)를 내보내면 감시 주기를 기다리지 않고 바로 깨운다.
/// 이벤트가 오지 않아도 제한 시간이 지나면 깨어나므로, 이벤트를 받지 못하는 환경에서도 기존처럼 주기적으로 읽는다.
/// 후크는 만든 스레드의 메시지 큐로 전달되므로 <see cref="Track"/>과 <see cref="Wait"/>은 같은 스레드에서 부른다.
/// </summary>
internal sealed class AccessibilityEventWaiter : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectValueChange = 0x800E;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;
    private const uint PmRemove = 0x0001;
    private const uint WaitObject0 = 0;
    private const uint WaitObject1 = 1;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private readonly Dictionary<uint, IntPtr> _hooks = [];
    // 후크가 살아 있는 동안 대리자가 수거되면 안 된다.
    private readonly WinEventProc _callback;
    private int _signaled;

    public AccessibilityEventWaiter()
    {
        _callback = OnEvent;
    }

    /// <summary>이벤트를 들을 온톡 프로세스를 맞춘다. 사라진 프로세스의 후크는 걷는다.</summary>
    public void Track(HashSet<uint> pids)
    {
        foreach (var pid in _hooks.Keys.Where(p => !pids.Contains(p)).ToList())
        {
            UnhookWinEvent(_hooks[pid]);
            _hooks.Remove(pid);
        }
        foreach (var pid in pids)
        {
            if (_hooks.ContainsKey(pid))
            {
                continue;
            }
            var hook = SetWinEventHook(EventObjectShow, EventObjectValueChange, IntPtr.Zero, _callback, pid, 0,
                WineventOutOfContext | WineventSkipOwnProcess);
            if (hook != IntPtr.Zero)
            {
                _hooks[pid] = hook;
            }
        }
    }

    /// <summary>
    /// 이벤트가 오거나 <paramref name="timeout"/>이 지나거나 <paramref name="stop"/>이 신호를 받을 때까지 기다린다.
    /// 이벤트가 쏟아져도 <paramref name="minDelay"/>보다 자주 깨지 않는다. 멈추라는 신호면 false.
    /// </summary>
    public bool Wait(WaitHandle stop, TimeSpan timeout, TimeSpan minDelay)
    {
        var started = Environment.TickCount64;
        var handles = new[] { stop.SafeWaitHandle.DangerousGetHandle() };
        while (true)
        {
            var elapsed = Environment.TickCount64 - started;
            var remaining = (long)timeout.TotalMilliseconds - elapsed;
            if (remaining <= 0)
            {
                return !stop.WaitOne(0);
            }
            var wait = Volatile.Read(ref _signaled) != 0
                ? Math.Max(0, (long)minDelay.TotalMilliseconds - elapsed)
                : remaining;
            if (Volatile.Read(ref _signaled) != 0 && wait == 0)
            {
                Volatile.Write(ref _signaled, 0);
                return !stop.WaitOne(0);
            }
            var result = MsgWaitForMultipleObjectsEx(1, handles, (uint)Math.Min(wait, remaining), QsAllInput, MwmoInputAvailable);
            if (result == WaitObject0)
            {
                return false;
            }
            if (result == WaitObject1)
            {
                while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, PmRemove))
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
        }
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // 커서·캐럿 이동은 화면 내용이 바뀐 것이 아니다.
        if (eventType != EventObjectLocationChange)
        {
            Volatile.Write(ref _signaled, 1);
        }
    }

    public void Dispose()
    {
        foreach (var hook in _hooks.Values)
        {
            UnhookWinEvent(hook);
        }
        _hooks.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(out Msg message, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref Msg message);
}
