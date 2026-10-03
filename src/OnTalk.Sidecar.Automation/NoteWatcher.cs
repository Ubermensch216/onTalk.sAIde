using System.Diagnostics;

namespace OnTalk.Sidecar.Automation;

public sealed record NoteWindow(IntPtr Hwnd, NoteWindowContent Content);

/// <param name="OnTalkRunning">온톡 프로세스가 있는지.</param>
/// <param name="Notes">지금 화면에 보이는 쪽지 창.</param>
public sealed record WatchSnapshot(bool OnTalkRunning, IReadOnlyList<NoteWindow> Notes);

/// <summary>
/// 온톡의 보이는 쪽지 창을 주기적으로 읽는다. 온톡을 조작하지 않는다.
/// 싱글창 모드에서는 같은 창의 내용이 바뀌므로 창 생성 이벤트가 아니라 내용을 다시 읽어 판단한다.
/// </summary>
public sealed class NoteWatcher : IDisposable
{
    private const string ProcessName = "온톡";
    private const string RenderClass = "Chrome_RenderWidgetHostHWND";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(800);

    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    // 쪽지 페이지가 아닌 창(메인 창은 요소가 400개가 넘는다)을 매번 다시 읽지 않기 위한 기록.
    private readonly HashSet<IntPtr> _notNotePages = [];
    private readonly HashSet<IntPtr> _activated = [];

    public event Action<WatchSnapshot>? Updated;

    public NoteWatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "OnTalk note watcher" };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            WatchSnapshot snapshot;
            try
            {
                snapshot = Scan();
            }
            catch (Exception)
            {
                // 읽는 도중 창이 닫히는 등의 일시 오류. 다음 주기에 다시 읽는다.
                snapshot = new WatchSnapshot(true, []);
            }
            Updated?.Invoke(snapshot);
            _stop.Token.WaitHandle.WaitOne(Interval);
        }
    }

    private WatchSnapshot Scan()
    {
        var pids = Process.GetProcessesByName(ProcessName).Select(p => (uint)p.Id).ToHashSet();
        if (pids.Count == 0)
        {
            _notNotePages.Clear();
            _activated.Clear();
            return new WatchSnapshot(false, []);
        }

        var candidates = new List<(IntPtr Hwnd, IntPtr Render)>();
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid) && Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd) &&
                Win32.ClassName(hwnd).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) &&
                FindRender(hwnd) is { } render)
            {
                candidates.Add((hwnd, render));
            }
            return true;
        }, IntPtr.Zero);

        _notNotePages.RemoveWhere(h => !Win32.IsWindow(h));
        _activated.RemoveWhere(h => !Win32.IsWindow(h));

        var notes = new List<NoteWindow>();
        var now = DateTimeOffset.Now;
        foreach (var (hwnd, render) in candidates)
        {
            if (_notNotePages.Contains(hwnd))
            {
                continue;
            }
            if (_activated.Add(render))
            {
                Win32.SendMessageTimeoutW(render, Win32.WM_GETOBJECT, IntPtr.Zero,
                    new IntPtr(Win32.ScreenReaderHoneyPotId), Win32.SMTO_ABORTIFHUNG, 1000, out _);
            }
            var root = MsaaReader.ReadRoot(render);
            if (root is null || root.Value.Length == 0)
            {
                // 아직 페이지가 뜨지 않았다. 다음 주기에 다시 본다.
                continue;
            }
            if (!NoteWindowParser.IsNotePage(root))
            {
                _notNotePages.Add(hwnd);
                continue;
            }
            if (MsaaReader.ReadTree(render) is { } tree)
            {
                notes.Add(new NoteWindow(hwnd, NoteWindowParser.Parse(tree, now)));
            }
        }
        return new WatchSnapshot(true, notes);
    }

    private static IntPtr? FindRender(IntPtr hwnd)
    {
        IntPtr? found = null;
        Win32.EnumChildWindows(hwnd, (child, _) =>
        {
            if (Win32.ClassName(child) != RenderClass)
            {
                return true;
            }
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _stop.Dispose();
    }
}
