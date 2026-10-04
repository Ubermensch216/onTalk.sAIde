using System.Diagnostics;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

public sealed record NoteWindow(IntPtr Hwnd, NoteWindowContent Content)
{
    public Guid SessionId { get; init; }
}

public sealed record WindowContext(IntPtr Hwnd, Guid SessionId, SidecarContext Context)
{
    public PeopleSnapshot? People { get; init; }
    public ChatSnapshot? Chat { get; init; }
    public IReadOnlyList<ConversationItem> Conversations { get; init; } = [];
}

/// <param name="OnTalkRunning">온톡 프로세스가 있는지.</param>
/// <param name="Notes">지금 화면에 보이는 쪽지 창.</param>
public sealed record WatchSnapshot(bool OnTalkRunning, IReadOnlyList<NoteWindow> Notes)
{
    public string? AccountCandidate { get; init; }
    public IReadOnlyList<WindowContext> Contexts { get; init; } = [];
    public WindowContext? ForTarget(IntPtr hwnd) => Contexts.FirstOrDefault(c => c.Hwnd == hwnd);
}

/// <summary>
/// 온톡의 보이는 쪽지 창을 주기적으로 읽는다. 온톡을 조작하지 않는다.
/// 싱글창 모드에서는 같은 창의 내용이 바뀌므로 창 생성 이벤트가 아니라 내용을 다시 읽어 판단한다.
/// </summary>
public sealed class NoteWatcher : IDisposable
{
    private const string ProcessName = "온톡";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(800);

    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly HashSet<IntPtr> _activated = [];
    private readonly WindowSessionTracker _sessions = new();
    private string? _sessionAccount;
    private IntPtr _target;
    private string? _permittedAccount;

    public void FollowTarget(IntPtr hwnd) => Interlocked.Exchange(ref _target, hwnd);
    public void PermitAccount(string? account) => Volatile.Write(ref _permittedAccount, account);

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
            if (_stop.IsCancellationRequested) break;
            Updated?.Invoke(snapshot);
            _stop.Token.WaitHandle.WaitOne(Interval);
        }
    }

    private WatchSnapshot Scan()
    {
        var pids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process) pids.Add((uint)process.Id);
        }
        if (pids.Count == 0)
        {
            _activated.Clear();
            _sessions.Clear();
            return new WatchSnapshot(false, []);
        }

        var account = OnTalkAccount.DetectCandidate();
        if (_sessionAccount != account) { _sessions.Clear(); _sessionAccount = account; }
        var permitted = Volatile.Read(ref _permittedAccount);
        var readContent = permitted is not null && account == permitted;
        var target = Interlocked.CompareExchange(ref _target, IntPtr.Zero, IntPtr.Zero);
        var candidates = new List<(IntPtr Hwnd, IntPtr Render, uint Pid)>();
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid) && Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd) &&
                Win32.ClassName(hwnd).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) &&
                Win32.FindRenderChild(hwnd) is { } render)
            {
                candidates.Add((hwnd, render, pid));
            }
            return true;
        }, IntPtr.Zero);

        _sessions.RemoveMissing(Win32.IsWindow);
        _activated.RemoveWhere(h => !Win32.IsWindow(h));

        var notes = new List<NoteWindow>();
        var contexts = new List<WindowContext>();
        var now = DateTimeOffset.Now;
        foreach (var (hwnd, render, pid) in candidates.OrderBy(c => c.Hwnd == target ? 0 : 1))
        {
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
                if (hwnd == target)
                {
                    // 위치 감시(100ms)와 분리한다. 메인 트리는 활성 대상에서만 읽는다.
                    var tree = readContent ? MsaaReader.ReadTree(render, readEditableValues: false, readContentText: true) : null;
                    var context = WindowContextParser.Parse(tree ?? root);
                    var chat = tree is not null ? ChatWindowParser.Parse(tree, now) : null;
                    var session = _sessions.Observe(hwnd, render, pid, context, chat: chat);
                    contexts.Add(new WindowContext(hwnd, session, context)
                    {
                        People = tree is not null && context.Surface == OnTalkSurface.Main && context.Menu is Core.MainMenu.Friends or Core.MainMenu.Organization
                            ? MainPeopleParser.Parse(tree, context.Menu, now) : null,
                        Chat = chat,
                        Conversations = tree is not null ? ChatWindowParser.ParseList(tree) : []
                    });
                    if (context.Surface == OnTalkSurface.Login) break;
                }
                continue;
            }
            if (readContent && MsaaReader.ReadTree(render) is { } noteTree)
            {
                var content = NoteWindowParser.Parse(noteTree, now);
                var context = WindowContextParser.Parse(root, content);
                var session = _sessions.Observe(hwnd, render, pid, context, content);
                notes.Add(new NoteWindow(hwnd, content) { SessionId = session });
                contexts.Add(new WindowContext(hwnd, session, context));
            }
        }
        // 읽는 사이 계정 후보가 바뀌었으면 그 자료를 새 계정에 넘기지 않는다.
        var afterAccount = OnTalkAccount.DetectCandidate();
        return new WatchSnapshot(true, account == afterAccount ? notes : [])
        {
            AccountCandidate = afterAccount,
            Contexts = account == afterAccount ? contexts : []
        };
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_thread.IsAlive)
        {
            // COM 읽기가 늦어지면 아직 토큰을 쓰는 감시 스레드의 CTS를 먼저 폐기하지 않는다.
            if (!_thread.Join(TimeSpan.FromSeconds(2))) return;
        }
        _stop.Dispose();
    }
}
