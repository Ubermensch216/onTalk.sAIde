using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

public partial class PanelWindow : Window
{
    private const double PanelWidthDip = 380;

    private readonly NoteWatcher _watcher = new();
    private readonly OllamaAnalyzer _analyzer = new();
    private readonly OllamaEmbedder _embedder = new();
    private readonly OllamaWriter _writer = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _reminders = new() { Interval = TimeSpan.FromMinutes(1) };
    // 창별로 직전 주기에 읽은 쪽지 키. 화면이 그려지는 도중의 불완전한 내용을 저장하지 않으려고
    // 두 주기 연속 같은 내용일 때만 저장한다.
    private readonly Dictionary<IntPtr, string> _lastSeenKey = [];
    private readonly Dictionary<IntPtr, SaveResult> _saved = [];
    // 분석에 실패한 쪽지. 사용자가 다시 시도하기 전에는 주기마다 재시도하지 않는다.
    private readonly Dictionary<long, string> _analysisErrors = [];
    private readonly Stopwatch _analysisClock = new();

    private ProfileStore? _store;
    private string? _candidateAccount;
    private IntPtr _attached;
    private IntPtr _self;
    private string? _shownKey;
    private ReceivedNote? _shownNote;
    private long? _shownNoteId;
    private CancellationTokenSource? _analysisCancel;
    private long? _analyzingNoteId;
    private TasksWindow? _tasksWindow;
    private SearchWindow? _searchWindow;
    private ComposeWindow? _composeWindow;
    private bool _embedding;

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
            _reminders.Tick += (_, _) => CheckReminders();
            _reminders.Start();
        };
        Closed += (_, _) =>
        {
            _follow.Stop();
            _reminders.Stop();
            _analysisCancel?.Cancel();
            _closing.Cancel();
            _watcher.Dispose();
            _analyzer.Dispose();
            _embedder.Dispose();
            _writer.Dispose();
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

        _composeWindow?.UpdateTargets(snapshot.Notes
            .Where(n => n.Content.Compose is not null)
            .Select(n => new ComposeTarget(n.Hwnd, DescribeTarget(n.Content), n.Content.Compose!))
            .ToList());

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
        UpdateFooter();

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
            EmbedPending();
        }
    }

    private void UpdateAccount(string? candidate)
    {
        if (candidate == _candidateAccount && (_store is null || _store.LoginId == candidate))
        {
            return;
        }
        // 계정이 바뀌면 이전 계정의 저장소·분석·창을 모두 닫고, 새 계정이 확인되기 전에는 수집하지 않는다.
        _candidateAccount = candidate;
        _analysisCancel?.Cancel();
        _tasksWindow?.Close();
        _searchWindow?.Close();
        _composeWindow?.Close();
        _store?.Dispose();
        _store = null;
        _lastSeenKey.Clear();
        _saved.Clear();
        _analysisErrors.Clear();
        ShowEmpty("");
        TasksButton.IsEnabled = false;
        SearchButton.IsEnabled = false;
        ComposeButton.IsEnabled = false;
        FooterText.Text = "";

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
            return;
        }
        TasksButton.IsEnabled = true;
        SearchButton.IsEnabled = true;
        ComposeButton.IsEnabled = true;
        EmbedPending();
        // 앱이 꺼져 있던 동안 지나간 알림을 모아서 보여 준다.
        Dispatcher.BeginInvoke(CheckReminders, DispatcherPriority.ApplicationIdle);
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

    private void UpdateFooter()
    {
        if (_store is null)
        {
            return;
        }
        var open = _store.GetTasks().Count(t => !t.Done);
        FooterText.Text = $"쪽지 {_store.CountNotes()} · 할 일 {open}";
    }

    private void ShowEmpty(string message)
    {
        _shownKey = null;
        _shownNote = null;
        _shownNoteId = null;
        // 보고 있던 쪽지가 사라졌으니 그 쪽지의 분석은 더 필요 없다.
        _analysisCancel?.Cancel();
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
            _ => $"보관한 쪽지 · 처음 본 시각 {saved.FirstSeenAt.ToLocalTime():yyyy-MM-dd HH:mm}",
        };
        if (_shownKey != note.Key)
        {
            // 같은 쪽지를 다시 그리면 사용자가 본문에서 선택·스크롤한 상태가 사라지므로 바뀔 때만 그린다.
            _shownKey = note.Key;
            _shownNote = note;
            _shownNoteId = null;
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
            AttachmentList.ItemsSource = null;
            ManualTitle.Clear();
            ManualDue.SelectedDate = null;
        }
        if (saved is not null && _shownNoteId != saved.NoteId)
        {
            _shownNoteId = saved.NoteId;
            RefreshWork();
        }
        if (_analyzingNoteId is not null && _analyzingNoteId == _shownNoteId)
        {
            AnalysisStatusText.Text = $"로컬 AI가 쪽지를 정리하는 중입니다 · {_analysisClock.Elapsed.TotalSeconds:0}초";
        }
    }

    /// <summary>지금 보이는 쪽지의 분석·업무 영역을 저장소 기준으로 다시 그린다.</summary>
    private void RefreshWork()
    {
        if (_store is null || _shownNoteId is not { } noteId || _shownNote is not { } note)
        {
            return;
        }
        var now = DateTimeOffset.Now;
        var tasks = _store.GetTasks(noteId);
        NoteTaskList.ItemsSource = tasks.Select(t => new TaskRow(t, now)).ToList();
        NoTaskText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AttachmentList.ItemsSource = _store.GetAttachments(noteId).Select(a => new AttachmentRow(a)).ToList();
        UpdateFooter();

        var analysis = _store.GetAnalysis(noteId, _analyzer.Model, OllamaAnalyzer.PromptVersion);
        SummarySection.Visibility = analysis is null ? Visibility.Collapsed : Visibility.Visible;
        CandidateSection.Visibility = SummarySection.Visibility;
        UncertainSection.Visibility = analysis is { Uncertainties.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        AnalysisStatusPanel.Visibility = analysis is null ? Visibility.Visible : Visibility.Collapsed;

        if (analysis is not null)
        {
            SummaryText.Text = analysis.Summary.Length > 0 ? analysis.Summary : "(요약 없음)";
            var rows = analysis.Actions
                .Select(a => new CandidateRow(a, tasks.Any(t => t.Title == a.Title)) { IsChecked = false })
                .ToList();
            CandidateList.ItemsSource = rows;
            NoCandidateText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RegisterButton.Visibility = rows.Any(r => r.CanRegister) ? Visibility.Visible : Visibility.Collapsed;
            DroppedText.Visibility = analysis.DroppedActions > 0 ? Visibility.Visible : Visibility.Collapsed;
            DroppedText.Text = $"원문에서 근거를 찾지 못한 후보 {analysis.DroppedActions}개는 표시하지 않았습니다.";
            UncertainList.ItemsSource = analysis.Uncertainties;
        }
        else if (_analysisErrors.TryGetValue(noteId, out var error))
        {
            AnalysisStatusText.Text = $"{error} 요약 없이도 아래에서 업무를 직접 추가할 수 있습니다.";
            RetryButton.Visibility = Visibility.Visible;
        }
        else if (_analyzingNoteId != noteId)
        {
            StartAnalysis(note, noteId);
        }
    }

    private async void StartAnalysis(ReceivedNote note, long noteId)
    {
        // 한 번에 한 쪽지만 분석한다. 다른 쪽지로 넘어가면 앞선 분석은 취소한다.
        _analysisCancel?.Cancel();
        var cancel = _analysisCancel = new CancellationTokenSource();
        _analyzingNoteId = noteId;
        _analysisClock.Restart();
        AnalysisStatusText.Text = "로컬 AI가 쪽지를 정리하는 중입니다";
        var store = _store;
        try
        {
            var sent = DateOnly.FromDateTime(
                (note.SentAt?.Value ?? DateTimeOffset.Now).ToOffset(KoreanNoteTime.SeoulOffset).DateTime);
            var analysis = await _analyzer.AnalyzeAsync(note, sent, cancel.Token);
            // 분석하는 사이 계정이 바뀌었으면 다른 계정 저장소에 쓰지 않는다.
            if (store is not null && ReferenceEquals(store, _store))
            {
                store.SaveAnalysis(noteId, _analyzer.Model, OllamaAnalyzer.PromptVersion, analysis, DateTimeOffset.Now);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (AnalysisUnavailableException ex)
        {
            _analysisErrors[noteId] = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_analysisCancel, cancel))
            {
                _analysisCancel = null;
                _analyzingNoteId = null;
            }
            cancel.Dispose();
        }
        if (_shownNoteId == noteId)
        {
            RefreshWork();
        }
    }

    private void OnRetryAnalysis(object sender, RoutedEventArgs e)
    {
        if (_shownNoteId is { } noteId)
        {
            _analysisErrors.Remove(noteId);
            RefreshWork();
        }
    }

    private void OnRegisterCandidates(object sender, RoutedEventArgs e)
    {
        if (_store is null || _shownNoteId is not { } noteId || CandidateList.ItemsSource is not List<CandidateRow> rows)
        {
            return;
        }
        foreach (var row in rows.Where(r => r.IsChecked && r.CanRegister))
        {
            var c = row.Candidate;
            _store.AddTask(noteId, c.Title, c.Evidence, c.DueText, c.Due, DateTimeOffset.Now);
        }
        RefreshWork();
        _tasksWindow?.Reload();
    }

    private void OnAddManualTask(object sender, RoutedEventArgs e)
    {
        var title = ManualTitle.Text.Trim();
        if (_store is null || _shownNoteId is not { } noteId || title.Length == 0)
        {
            return;
        }
        DateOnly? due = ManualDue.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null;
        _store.AddTask(noteId, title, null, null, due, DateTimeOffset.Now);
        ManualTitle.Clear();
        ManualDue.SelectedDate = null;
        RefreshWork();
        _tasksWindow?.Reload();
    }

    private void OnToggleTask(object sender, RoutedEventArgs e)
    {
        if (_store is not null && sender is CheckBox { Tag: long id } box)
        {
            _store.SetTaskDone(id, box.IsChecked == true, DateTimeOffset.Now);
            RefreshWork();
            _tasksWindow?.Reload();
        }
    }

    private void OnImportAttachment(object sender, RoutedEventArgs e)
    {
        if (_store is null || _shownNoteId is not { } noteId || sender is not Button { Tag: long id } ||
            _store.GetAttachments(noteId).FirstOrDefault(a => a.Id == id) is not { } attachment)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"'{attachment.Name}' 파일 고르기", FileName = attachment.Name };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            var file = new FileInfo(dialog.FileName);
            if (file.Length > ProfileStore.MaxFileBytes)
            {
                MessageBox.Show(this, "200MB를 넘는 파일은 보관하지 않습니다.", "onTalk.sAIde");
                return;
            }
            if (!string.Equals(file.Name, attachment.Name, StringComparison.OrdinalIgnoreCase) &&
                MessageBox.Show(this, $"고른 파일 이름이 첨부 이름과 다릅니다.\n\n첨부: {attachment.Name}\n고른 파일: {file.Name}\n\n이 파일로 보관할까요?",
                    "이름이 다릅니다", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }
            var data = File.ReadAllBytes(file.FullName);
            if (attachment.Sha256 is { } old &&
                old != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)) &&
                MessageBox.Show(this, "이미 보관한 파일과 내용이 다릅니다. 새 파일로 바꿀까요?", "내용이 다릅니다",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }
            _store.StoreAttachment(id, data, DateTimeOffset.Now);
            RefreshWork();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, $"파일을 보관하지 못했습니다.\n{ex.Message}", "onTalk.sAIde");
        }
    }

    private void OnExportAttachment(object sender, RoutedEventArgs e)
    {
        if (_store is null || _shownNoteId is not { } noteId || sender is not Button { Tag: long id } ||
            _store.GetAttachments(noteId).FirstOrDefault(a => a.Id == id) is not { } attachment)
        {
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "보관한 파일 내보내기", FileName = attachment.Name };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            File.WriteAllBytes(dialog.FileName, _store.ReadAttachment(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // InvalidDataException(보관 파일 손상)도 IOException이다.
            MessageBox.Show(this, $"파일을 내보내지 못했습니다.\n{ex.Message}", "onTalk.sAIde");
        }
    }

    private void OnOpenTasks(object sender, RoutedEventArgs e) => OpenTasks();

    private void OpenTasks()
    {
        if (_store is null)
        {
            return;
        }
        if (_tasksWindow is null)
        {
            _tasksWindow = new TasksWindow(_store, RefreshWork);
            _tasksWindow.Closed += (_, _) => _tasksWindow = null;
            _tasksWindow.Show();
        }
        _tasksWindow.Activate();
    }

    private static string DescribeTarget(NoteWindowContent content)
    {
        var compose = content.Compose!;
        var names = compose.Recipients.Count switch
        {
            0 => "수신 없음",
            <= 2 => $"수신 {string.Join(", ", compose.Recipients)}",
            _ => $"수신 {compose.Recipients[0]} 외 {compose.Recipients.Count - 1}명",
        };
        return $"{content.Title} · {names} · {compose.UsedBytes}/{compose.LimitBytes}";
    }

    private void OnReplyDraft(object sender, RoutedEventArgs e)
    {
        if (_store is not null && _shownNoteId is { } noteId && _store.GetNote(noteId) is { } stored)
        {
            OpenCompose(stored);
        }
    }

    private void OnNewDraft(object sender, RoutedEventArgs e) => OpenCompose(null);

    private void OpenCompose(StoredNote? source)
    {
        if (_store is null)
        {
            return;
        }
        // 초안 창은 하나만 둔다. 닫을 때 쓰던 초안이 저장된다.
        _composeWindow?.Close();
        _composeWindow = new ComposeWindow(_store, _writer, source);
        _composeWindow.Closed += (s, _) =>
        {
            if (ReferenceEquals(s, _composeWindow))
            {
                _composeWindow = null;
            }
        };
        _composeWindow.Show();
    }

    private void OnOpenSearch(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            return;
        }
        if (_searchWindow is null)
        {
            _searchWindow = new SearchWindow(_store, _embedder);
            _searchWindow.Closed += (_, _) => _searchWindow = null;
            _searchWindow.Show();
        }
        _searchWindow.Activate();
        EmbedPending();
    }

    /// <summary>뜻 검색용 벡터가 없는 쪽지를 뒤에서 하나씩 채운다. 로컬 AI가 꺼져 있으면 조용히 그만둔다.</summary>
    private async void EmbedPending()
    {
        if (_embedding || _store is not { } store)
        {
            return;
        }
        _embedding = true;
        try
        {
            while (ReferenceEquals(store, _store) && store.GetNotesWithoutEmbedding(_embedder.Model, 1) is [var next])
            {
                var vector = await _embedder.EmbedAsync(OllamaEmbedder.TextOf(next.Note), _closing.Token);
                // 기다리는 사이 계정이 바뀌었으면 닫힌 저장소에 쓰지 않는다.
                if (!ReferenceEquals(store, _store))
                {
                    break;
                }
                store.SaveEmbedding(next.Id, _embedder.Model, vector);
            }
        }
        catch (Exception ex) when (ex is AnalysisUnavailableException or OperationCanceledException)
        {
            // 다음에 쪽지를 보관하거나 검색 창을 열 때 다시 시도한다.
        }
        finally
        {
            _embedding = false;
        }
    }

    private void CheckReminders()
    {
        if (_store is null)
        {
            return;
        }
        var now = DateTimeOffset.Now;
        var due = _store.GetTasks().Where(t => ReminderRule.IsDue(t, now)).ToList();
        if (due.Count == 0)
        {
            return;
        }
        // 창을 띄우기 전에 기록한다. 띄운 뒤 앱이 죽어도 같은 알림이 반복되지 않는다.
        foreach (var task in due)
        {
            _store.MarkReminded(task.Id, ReminderRule.StageReached(task.Due!.Value, now));
        }
        new ReminderWindow(due.Select(t => new TaskRow(t, now)).ToList(), OpenTasks).Show();
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
        if (Win32.GetWindowRect(_self, out var current) && current.Left == x && current.Top == note.Top &&
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
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
