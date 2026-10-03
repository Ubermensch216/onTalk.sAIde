using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

public partial class PanelWindow : Window
{
    private const double PanelWidthDip = 380;

    private readonly NoteWatcher _watcher = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(100) };
    // 창별로 직전 주기에 읽은 쪽지 키. 화면이 그려지는 도중의 불완전한 내용을 저장하지 않으려고
    // 두 주기 연속 같은 내용일 때만 저장한다.
    private readonly Dictionary<IntPtr, string> _lastSeenKey = [];
    private readonly Dictionary<IntPtr, SaveResult> _saved = [];

    private ProfileStore? _store;
    private string? _candidateAccount;
    private IntPtr _attached;
    private IntPtr _self;
    private string? _shownKey;

    public PanelWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => _self = new WindowInteropHelper(this).Handle;
        Loaded += (_, _) =>
        {
            _watcher.Updated += OnWatcherUpdated;
            _watcher.Start();
            _follow.Tick += (_, _) => FollowAttachedWindow();
            _follow.Start();
        };
        Closed += (_, _) =>
        {
            _follow.Stop();
            _watcher.Dispose();
            _store?.Dispose();
        };
    }

    // 감시 스레드에서 호출된다.
    private void OnWatcherUpdated(WatchSnapshot snapshot)
    {
        var candidate = snapshot.OnTalkRunning ? OnTalkAccount.DetectCandidate() : null;
        Dispatcher.BeginInvoke(() => Apply(snapshot, candidate));
    }

    private void Apply(WatchSnapshot snapshot, string? candidate)
    {
        UpdateAccount(candidate);

        if (!snapshot.OnTalkRunning)
        {
            SetStatus("온톡이 실행되지 않았습니다", "#9CA3AF");
            ShowEmpty("온톡을 실행하면 자동으로 연결합니다.");
            _attached = IntPtr.Zero;
            return;
        }
        if (_store is null)
        {
            SetStatus(candidate is null ? "온톡 계정을 확인하지 못했습니다" : "계정 연결 확인 필요", "#F59E0B");
            ShowEmpty(candidate is null
                ? "온톡에 로그인하면 계정을 확인합니다."
                : "위에서 계정 연결을 확인하면 쪽지 수집을 시작합니다.");
            _attached = IntPtr.Zero;
            return;
        }

        SetStatus($"연결됨 · {_store.LoginId}", "#10B981");
        var live = snapshot.Notes.Select(n => n.Hwnd).ToHashSet();
        foreach (var gone in _lastSeenKey.Keys.Where(h => !live.Contains(h)).ToList())
        {
            _lastSeenKey.Remove(gone);
            _saved.Remove(gone);
        }

        var received = snapshot.Notes.Where(n => n.Content.Kind == NoteWindowKind.Received).ToList();
        foreach (var window in received)
        {
            Collect(window);
        }

        var foreground = Win32.GetForegroundWindow();
        var target = received.FirstOrDefault(n => n.Hwnd == foreground)
                     ?? received.FirstOrDefault(n => n.Hwnd == _attached)
                     ?? received.FirstOrDefault();
        _attached = target?.Hwnd ?? IntPtr.Zero;
        FooterText.Text = $"보관한 쪽지 {_store.CountNotes()}건";

        if (target is null)
        {
            ShowEmpty("온톡에서 받은 쪽지를 열면 여기에 표시됩니다.");
        }
        else if (target.Content.Note is not { } note)
        {
            ShowEmpty("이 쪽지 창의 내용을 읽지 못했습니다. 온톡 화면 구성이 달라졌을 수 있습니다.");
        }
        else
        {
            ShowNote(note, _saved.GetValueOrDefault(target.Hwnd));
        }
    }

    private void Collect(NoteWindow window)
    {
        if (window.Content.Note is not { } note)
        {
            _lastSeenKey.Remove(window.Hwnd);
            return;
        }
        var key = note.Key;
        var stable = _lastSeenKey.TryGetValue(window.Hwnd, out var previous) && previous == key;
        _lastSeenKey[window.Hwnd] = key;
        if (!stable)
        {
            _saved.Remove(window.Hwnd);
            return;
        }
        if (!_saved.ContainsKey(window.Hwnd))
        {
            _saved[window.Hwnd] = _store!.SaveReceived(note, DateTimeOffset.Now);
        }
    }

    private void UpdateAccount(string? candidate)
    {
        if (candidate == _candidateAccount && (_store is null || _store.LoginId == candidate))
        {
            return;
        }
        // 계정이 바뀌면 이전 계정 저장소를 닫고, 새 계정이 확인되기 전에는 수집하지 않는다.
        _candidateAccount = candidate;
        _store?.Dispose();
        _store = null;
        _lastSeenKey.Clear();
        _saved.Clear();
        _shownKey = null;

        if (candidate is not null && _settings.ConfirmedAccounts.Contains(candidate))
        {
            OpenStore(candidate);
        }
        AccountBanner.Visibility = candidate is not null && _store is null ? Visibility.Visible : Visibility.Collapsed;
        AccountText.Text = $"온톡 계정 '{candidate}'(으)로 로그인한 것으로 보입니다.";
    }

    private void OpenStore(string account)
    {
        try
        {
            _store = ProfileStore.Open(ProfileStore.DefaultRoot, account);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException
                                       or Microsoft.Data.Sqlite.SqliteException
                                       or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(this, $"저장소를 열지 못했습니다.\n{ex.Message}", "onTalk.sAIde");
        }
    }

    private void OnConfirmAccount(object sender, RoutedEventArgs e)
    {
        if (_candidateAccount is not { } account)
        {
            return;
        }
        OpenStore(account);
        if (_store is null)
        {
            return;
        }
        if (!_settings.ConfirmedAccounts.Contains(account))
        {
            _settings.ConfirmedAccounts.Add(account);
            _settings.Save();
        }
        AccountBanner.Visibility = Visibility.Collapsed;
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void ShowEmpty(string message)
    {
        _shownKey = null;
        EmptyText.Text = message;
        EmptyText.Visibility = Visibility.Visible;
        NoteView.Visibility = Visibility.Collapsed;
    }

    private void ShowNote(ReceivedNote note, SaveResult? saved)
    {
        EmptyText.Visibility = Visibility.Collapsed;
        NoteView.Visibility = Visibility.Visible;
        CaptureText.Text = saved switch
        {
            null => "내용 확인 중",
            { IsNew: true } => "새로 보관했습니다",
            _ => $"이미 보관한 쪽지입니다 (처음 본 시각 {saved.FirstSeenAt.ToLocalTime():yyyy-MM-dd HH:mm})",
        };
        if (_shownKey == note.Key)
        {
            // 같은 쪽지를 다시 그리면 사용자가 본문에서 선택·스크롤한 상태가 사라진다.
            return;
        }
        _shownKey = note.Key;
        SenderText.Text = note.SenderName;
        SenderDetailText.Text = note.SenderDetail;
        TimeText.Text = note.SentAt switch
        {
            null => $"{note.SentAtText} (시각 해석 실패)",
            { YearInferred: true } t => $"{t.Value:yyyy-MM-dd HH:mm} (연도는 추정)",
            { } t => $"{t.Value:yyyy-MM-dd HH:mm}",
        } + (note.RecipientCount is { } count ? $" · 수신 {count}명" : "");
        BodyText.Text = note.Body;
        BodyText.ScrollToHome();
        AttachmentPanel.Visibility = note.AttachmentNames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachmentTitle.Text = $"첨부 파일 {note.AttachmentNames.Count}개";
        AttachmentList.ItemsSource = note.AttachmentNames;
    }

    private void FollowAttachedWindow()
    {
        if (_attached == IntPtr.Zero || _self == IntPtr.Zero || WindowState != WindowState.Normal)
        {
            Topmost = false;
            return;
        }
        if (!Win32.IsWindow(_attached) || !Win32.IsWindowVisible(_attached) || Win32.IsIconic(_attached) ||
            !Win32.GetWindowRect(_attached, out var note))
        {
            Topmost = false;
            return;
        }
        // 쪽지 창이나 패널을 쓰고 있을 때만 다른 창 위에 둔다.
        var foreground = Win32.GetForegroundWindow();
        Topmost = foreground == _attached || foreground == _self;

        var width = (int)Math.Round(PanelWidthDip * GetDpiForWindow(_attached) / 96.0);
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(_attached, MonitorDefaultToNearest), ref monitor);
        // 오른쪽에 자리가 없으면 왼쪽에 붙인다.
        var x = note.Right + width <= monitor.Work.Right || note.Left - width < monitor.Work.Left
            ? note.Right
            : note.Left - width;
        if (GetWindowRect(_self, out var current) && current.Left == x && current.Top == note.Top &&
            current.Width == width && current.Height == note.Height)
        {
            return;
        }
        SetWindowPos(_self, IntPtr.Zero, x, note.Top, width, note.Height, SwpNoZOrder | SwpNoActivate);
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Win32.Rect Monitor;
        public Win32.Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32.Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
