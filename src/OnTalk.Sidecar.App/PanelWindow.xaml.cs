using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private const double PanelWidthDip = 360;
    private const double PanelHeightDip = 640;
    private const double PanelMinHeightDip = 380;
    private const double RailWidthDip = 64;
    private const double RailHeightDip = 180;
    // 접힌 도구를 온톡 창 위에 겹쳐 놓을 때 온톡의 창 단추를 가리지 않도록 내려 놓는 거리.
    private const double RailOverlapInsetDip = 96;
    private const int HotKeyId = 1;

    private readonly NoteWatcher _watcher = new();
    private readonly OnTalkDock _dock = new();
    private readonly OllamaAnalyzer _analyzer;
    private readonly OllamaEmbedder _embedder;
    private readonly OllamaWriter _writer;
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
    private IntPtr _self;
    private HwndSource? _source;
    private TrayIcon? _tray;
    private IntPtr _dockedTo;
    private bool _collapsed;
    // 자리가 없어도 사용자가 직접 펼쳤으면 온톡 창 위에 겹쳐서라도 펼친다.
    private bool _forceExpanded;
    // 사용자가 직접 열었으면 온톡을 다시 쓰기 전까지는 다른 앱을 써도 숨기지 않는다.
    private bool _pinnedOpen = true;
    private bool? _railShown;
    private string? _shownKey;
    private ReceivedNote? _shownNote;
    private long? _shownNoteId;
    private CancellationTokenSource? _analysisCancel;
    private long? _analyzingNoteId;
    private TasksWindow? _tasksWindow;
    private SearchWindow? _searchWindow;
    private NoteHostWindow? _noteWindow;
    private ManageWindow? _manageWindow;
    private string? _onTalkVersion;
    private bool _embedding;
    private WatchSnapshot? _latestSnapshot;
    private WindowContext? _context;
    private MainMenu _manualMenu;
    private IntPtr _manualMenuWindow;
    private readonly Dictionary<string, NoteComposePane> _composePanes = [];
    private NoteComposePane? _activePane;
    private string? _contextViewKey;

    public PanelWindow()
    {
        // 모델은 관리 창에서 바꾸고 다음 실행부터 쓴다. 보관한 분석과 검색용 벡터는 모델별로 따로 남는다.
        var analysisModel = _settings.AnalysisModel is { Length: > 0 } chosen ? chosen : OllamaModels.DefaultAnalysis;
        _analyzer = new OllamaAnalyzer(analysisModel);
        _writer = new OllamaWriter(analysisModel);
        _embedder = new OllamaEmbedder(_settings.EmbeddingModel is { Length: > 0 } embedding ? embedding : OllamaModels.DefaultEmbedding);
        InitializeComponent();
        _collapsed = _settings.Collapsed;
        var work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left, work.Right - Width - 12);
        Top = work.Top + 80;
        SourceInitialized += (_, _) =>
        {
            _self = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_self);
            _source?.AddHook(OnWindowMessage);
            RegisterHotKey(_self, HotKeyId, ModControl | ModShift | ModNoRepeat, VkSpace);
            _tray = new TrayIcon(_self);
            // 알림 영역을 쓸 수 없으면 패널을 숨기지 않으므로 작업 표시줄에서 찾을 수 있게 한다.
            ShowInTaskbar = !_tray.IsAvailable;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Collapse();
                e.Handled = true;
            }
        };
        Loaded += (_, _) =>
        {
            _watcher.Updated += OnWatcherUpdated;
            _watcher.Start();
            _follow.Tick += (_, _) => FollowDock();
            _follow.Start();
            // 온톡이 이미 떠 있으면 처음부터 그 옆에서 시작한다.
            _dock.Adopt();
            FollowDock();
            _reminders.Tick += (_, _) => CheckReminders();
            _reminders.Start();
        };
        Closed += (_, _) =>
        {
            _follow.Stop();
            _reminders.Stop();
            if (_self != IntPtr.Zero)
            {
                UnregisterHotKey(_self, HotKeyId);
            }
            _source?.RemoveHook(OnWindowMessage);
            _tray?.Dispose();
            _analysisCancel?.Cancel();
            foreach (var pane in _composePanes.Values) pane.Dispose();
            _chatPane?.Dispose();
            _peoplePane?.Dispose();
            _homePane?.Dispose();
            _notesPane?.Dispose();
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
        var candidate = snapshot.AccountCandidate;
        // 온톡이 스스로 업데이트하면 화면 구성이 바뀔 수 있다. 실행 중인 버전을 한 번 읽어 둔다.
        var version = snapshot.OnTalkRunning ? _onTalkVersion ?? OnTalkAccount.RunningVersion() : null;
        Dispatcher.BeginInvoke(() =>
        {
            _onTalkVersion = version;
            Apply(snapshot, candidate);
        });
    }

    private void Apply(WatchSnapshot snapshot, string? candidate)
    {
        if (snapshot.OnTalkRunning && candidate is null && _store is not null &&
            !snapshot.Contexts.Any(c => c.Context.Surface == OnTalkSurface.Login))
        {
            // 저장소 파일을 잠깐 못 본 것만으로 기존 계정과 화면을 해제하지 않는다.
            // 다른 계정, 로그인 화면, 프로세스 종료는 아래에서 즉시 반영한다.
            return;
        }
        _latestSnapshot = snapshot;
        UpdateAccount(candidate);

        if (!snapshot.OnTalkRunning)
        {
            SetStatus("온톡이 실행되지 않았습니다", "#9CA3AF");
            ShowEmpty("온톡을 실행하면 자동으로 연결합니다.");
            return;
        }
        if (_store is null)
        {
            SetStatus(candidate is null ? "온톡 계정을 확인하지 못했습니다" : "계정 연결 확인 필요", "#F59E0B");
            ShowEmpty(candidate is null
                ? "온톡에 로그인하면 계정을 확인합니다."
                : "위에서 계정 연결을 확인하면 쪽지 수집을 시작합니다.");
            return;
        }

        if (_onTalkVersion is { } v && !OnTalkAccount.IsVerified(v))
        {
            SetStatus($"연결됨 · {_store.LoginId} · 온톡 {v}은(는) 확인하지 않은 버전", "#F59E0B");
        }
        else
        {
            SetStatus($"연결됨 · {_store.LoginId}", "#10B981");
        }
        var live = snapshot.Notes.Select(n => n.Hwnd).ToHashSet();
        foreach (var gone in _lastSeenKey.Keys.Where(h => !live.Contains(h)).ToList())
        {
            _lastSeenKey.Remove(gone);
            _saved.Remove(gone);
        }

        var sessions = snapshot.Notes.Select(n => n.SessionId).ToHashSet();
        foreach (var key in _composePanes.Where(p => !sessions.Contains(p.Value.SessionId)).Select(p => p.Key).ToList())
        {
            var pane = _composePanes[key];
            if (ReferenceEquals(_activePane, pane)) { _activePane = null; ComposeHost.Content = null; }
            pane.Dispose();
            _composePanes.Remove(key);
        }

        var received = snapshot.Notes.Where(n => n.Content.Kind == NoteWindowKind.Received).ToList();
        foreach (var window in received)
        {
            Collect(window);
        }

        UpdateFooter();
        ApplyCurrentContext(snapshot);
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
        CloseStore();
        _candidateAccount = candidate;

        if (candidate is not null && _settings.ConfirmedAccounts.Contains(candidate))
        {
            OpenStore(candidate);
        }
        AccountBanner.Visibility = candidate is not null && _store is null ? Visibility.Visible : Visibility.Collapsed;
        if (AccountBanner.Visibility == Visibility.Visible)
        {
            // 접혀 있으면 연결 확인을 볼 수 없다.
            _collapsed = false;
            _forceExpanded = true;
        }
        AccountText.Text = $"온톡 계정 '{candidate}'(으)로 로그인한 것으로 보입니다.";
    }

    /// <summary>열려 있는 저장소와 그 저장소를 쓰는 창을 모두 닫는다. 다음 감시 주기에 계정을 다시 확인해 연다.</summary>
    private void CloseStore()
    {
        _watcher.PermitAccount(null);
        _candidateAccount = null;
        _analysisCancel?.Cancel();
        _tasksWindow?.Close();
        _searchWindow?.Close();
        _noteWindow?.Close();
        _noteWindow = null;
        foreach (var pane in _composePanes.Values) pane.Dispose();
        _composePanes.Clear();
        _activePane = null;
        _peoplePane?.Dispose(); _peoplePane = null;
        _homePane?.Dispose(); _homePane = null;
        _notesPane?.Dispose(); _notesPane = null;
        _peoplePaneKey = null;
        PeopleHost.Content = null;
        _chatPane?.Dispose(); _chatPane = null; _chatPaneKey = null; _conversationsPane = null;
        ChatHost.Content = null;
        _context = null;
        _contextViewKey = null;
        _manageWindow?.Close();
        _store?.Dispose();
        _store = null;
        _lastSeenKey.Clear();
        _saved.Clear();
        _analysisErrors.Clear();
        ShowEmpty("");
        SetStoreButtons(false);
        FooterText.Text = "";
    }

    private void OnOpenManage(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            return;
        }
        if (_manageWindow is null)
        {
            _manageWindow = new ManageWindow(_store, _settings, _onTalkVersion, RestoreBackup, DeleteAllData) { Owner = this };
            _manageWindow.Closed += (_, _) => _manageWindow = null;
            ShowWorkWindow(_manageWindow);
        }
        _manageWindow.Activate();
    }

    private void RestoreBackup(string zipPath, string password)
    {
        if (_store is not { LoginId: var account })
        {
            return;
        }
        // 되살리는 동안 저장소를 닫아 둔다. 끝나면 다음 감시 주기에 다시 열린다.
        CloseStore();
        try
        {
            var result = ProfileBackup.Restore(ProfileStore.DefaultRoot, account, zipPath, password);
            Dialogs.Show(this,
                $"백업을 되살렸습니다. 쪽지 {result.Notes}건." +
                (result.PreviousDataMovedTo is { } moved ? $"\n\n되살리기 전의 자료는 지우지 않고 아래 폴더로 옮겼습니다.\n{moved}" : ""),
                "onTalk.sAIde");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            Dialogs.Show(this, $"되살리지 못했습니다. 기존 자료는 그대로입니다.\n{ex.Message}", "onTalk.sAIde");
        }
    }

    private void DeleteAllData()
    {
        if (_store is not { LoginId: var account })
        {
            return;
        }
        CloseStore();
        try
        {
            ProfileStore.DeleteProfile(ProfileStore.DefaultRoot, account);
            // 다시 수집하려면 계정 연결을 새로 확인받는다.
            _settings.ConfirmedAccounts.Remove(account);
            _settings.Save();
            Dialogs.Show(this, $"계정 '{account}'의 보관 자료를 모두 지웠습니다. 온톡의 쪽지에는 영향이 없습니다.", "onTalk.sAIde");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Show(this, $"지우지 못했습니다.\n{ex.Message}", "onTalk.sAIde");
        }
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
            Dialogs.Show(this, $"저장소를 열지 못했습니다.\n{ex.Message}", "onTalk.sAIde");
            return;
        }
        SetStoreButtons(true);
        _watcher.PermitAccount(account);
        EmbedPending();
        // 앱이 꺼져 있던 동안 지나간 알림을 모아서 보여 준다.
        Dispatcher.BeginInvoke(CheckReminders, DispatcherPriority.ApplicationIdle);
    }

    private void SetStoreButtons(bool enabled)
    {
        foreach (var button in new[]
                 {
                     TasksButton, SearchButton, ComposeButton, ManageButton,
                     RailTasksButton, RailSearchButton, RailManageButton,
                 })
        {
            button.IsEnabled = enabled;
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
        StatusChip.Background = (Brush)FindResource(color switch
        {
            "#10B981" => "SuccessSoft",
            "#F59E0B" => "WarnSoft",
            _ => "PageBg",
        });
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
        _activePane?.SetActive(false);
        _peoplePane?.SetActive(false);
        _homePane?.SetActive(false);
        _notesPane?.SetActive(false);
        ComposeHost.Visibility = Visibility.Collapsed;
        PeopleHost.Visibility = Visibility.Collapsed;
        _chatPane?.SetActive(false);
        ChatHost.Visibility = Visibility.Collapsed;
        ContextView.Visibility = Visibility.Collapsed;
        ContextHeader.Visibility = Visibility.Collapsed;
        WindowSubtitle.Text = "온톡 작업 도우미";
        _shownKey = null;
        _shownNote = null;
        _shownNoteId = null;
        // 보고 있던 쪽지가 사라졌으니 그 쪽지의 분석은 더 필요 없다.
        _analysisCancel?.Cancel();
        EmptyText.Text = message;
        EmptyState.Visibility = Visibility.Visible;
        NoteView.Visibility = Visibility.Collapsed;
    }

    private void ShowNote(ReceivedNote note, SaveResult? saved)
    {
        EmptyState.Visibility = Visibility.Collapsed;
        NoteView.Visibility = Visibility.Visible;
        if (_shownKey != note.Key)
        {
            _analysisCancel?.Cancel();
            SummarySection.Visibility = CandidateSection.Visibility = UncertainSection.Visibility = Visibility.Collapsed;
            CandidateList.ItemsSource = null;
            NoteTaskList.ItemsSource = null;
            AnalysisStatusPanel.Visibility = Visibility.Visible;
            AnalysisStatusText.Text = "현재 쪽지를 확인하는 중입니다.";
            // 같은 쪽지를 다시 그리면 사용자가 본문에서 선택·스크롤한 상태가 사라지므로 바뀔 때만 그린다.
            _shownKey = note.Key;
            _shownNote = note;
            _shownNoteId = null;
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
            ShowCandidates(analysis, tasks);
            UncertainList.ItemsSource = analysis.Uncertainties;
        }
        else if (_analysisErrors.TryGetValue(noteId, out var error))
        {
            // AI가 없어도 원문 문장 그대로를 후보로 보여 준다. 저장하지 않으므로 AI가 되면 그 결과로 바뀐다.
            AnalysisStatusText.Text =
                $"{error} 아래 후보는 AI 없이 원문에서 추린 문장입니다. 할 일이 맞는지 확인한 뒤 등록하세요.";
            RetryButton.Visibility = Visibility.Visible;
            CandidateSection.Visibility = Visibility.Visible;
            ShowCandidates(SentenceCandidates.Find(note.Body, SentDate(note)), tasks);
        }
        else if (_analyzingNoteId != noteId)
        {
            StartAnalysis(note, noteId);
        }
    }

    private void ShowCandidates(NoteAnalysis analysis, IReadOnlyList<TaskItem> tasks)
    {
        var rows = analysis.Actions
            .Select(a => new CandidateRow(a, tasks.Any(t => t.Title == a.Title)) { IsChecked = false })
            .ToList();
        CandidateList.ItemsSource = rows;
        NoCandidateText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RegisterButton.Visibility = rows.Any(r => r.CanRegister) ? Visibility.Visible : Visibility.Collapsed;
        DroppedText.Visibility = analysis.DroppedActions > 0 ? Visibility.Visible : Visibility.Collapsed;
        DroppedText.Text = $"원문에서 근거를 찾지 못한 후보 {analysis.DroppedActions}개는 표시하지 않았습니다.";
    }

    private static DateOnly SentDate(ReceivedNote note) => DateOnly.FromDateTime(
        (note.SentAt?.Value ?? DateTimeOffset.Now).ToOffset(KoreanNoteTime.SeoulOffset).DateTime);

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
            var analysis = await _analyzer.AnalyzeAsync(note, SentDate(note), cancel.Token);
            // 분석하는 사이 계정이 바뀌었으면 다른 계정 저장소에 쓰지 않는다.
            if (store is not null && ReferenceEquals(store, _store) && !cancel.IsCancellationRequested)
            {
                store.SaveAnalysis(noteId, _analyzer.Model, OllamaAnalyzer.PromptVersion, analysis, DateTimeOffset.Now);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (AnalysisUnavailableException ex)
        {
            if (ReferenceEquals(store, _store) && !cancel.IsCancellationRequested) _analysisErrors[noteId] = ex.Message;
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
        if (ReferenceEquals(store, _store) && _shownNoteId == noteId)
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
                Dialogs.Show(this, "200MB를 넘는 파일은 보관하지 않습니다.", "onTalk.sAIde");
                return;
            }
            if (!string.Equals(file.Name, attachment.Name, StringComparison.OrdinalIgnoreCase) &&
                Dialogs.Show(this, $"고른 파일 이름이 첨부 이름과 다릅니다.\n\n첨부: {attachment.Name}\n고른 파일: {file.Name}\n\n이 파일로 보관할까요?",
                    "이름이 다릅니다", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }
            var data = File.ReadAllBytes(file.FullName);
            if (attachment.Sha256 is { } old &&
                old != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)) &&
                Dialogs.Show(this, "이미 보관한 파일과 내용이 다릅니다. 새 파일로 바꿀까요?", "내용이 다릅니다",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }
            _store.StoreAttachment(id, data, DateTimeOffset.Now);
            RefreshWork();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Dialogs.Show(this, $"파일을 보관하지 못했습니다.\n{ex.Message}", "onTalk.sAIde");
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
            Dialogs.Show(this, $"파일을 내보내지 못했습니다.\n{ex.Message}", "onTalk.sAIde");
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
            _tasksWindow = new TasksWindow(_store, RefreshWork) { Owner = this };
            _tasksWindow.Closed += (_, _) => _tasksWindow = null;
            ShowWorkWindow(_tasksWindow);
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

    private void OpenCompose(StoredNote? source, ComposeTarget? target = null, ContactPreparation? preparation = null)
    {
        if (_store is null)
        {
            return;
        }
        // 새 쪽지 · 답장 · 선택 직원/친구에게 쪽지는 모두 같은 쪽지 창(‘쪽지’ · ‘개인별 쪽지’ 탭)을 쓴다.
        // 이미 열린 창이 있으면 먼저 닫아 새로 고른 직원이 두 탭에 그대로 들어가게 하고, 닫기를 취소하면(저장 안 한 개인별 작업 등) 그 창에서 계속한다.
        if (_noteWindow is { } open)
        {
            open.Close();
            if (ReferenceEquals(open, _noteWindow))
            {
                open.Show();
                open.Activate();
                return;
            }
        }
        var window = new NoteHostWindow(_store, _writer, preparation, source) { Owner = this };
        _noteWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(window, _noteWindow))
            {
                _noteWindow = null;
            }
        };
        ShowWorkWindow(window);
    }

    private void OnOpenSearch(object sender, RoutedEventArgs e)
    {
        if (_store is null)
        {
            return;
        }
        if (_searchWindow is null)
        {
            _searchWindow = new SearchWindow(_store, _embedder) { Owner = this };
            _searchWindow.Closed += (_, _) => _searchWindow = null;
            ShowWorkWindow(_searchWindow);
        }
        _searchWindow.Activate();
        EmbedPending();
    }

    private void ShowWorkWindow(Window window)
    {
        // 팝업이 열리는 순간에도 맨 위 패널 아래로 들어가지 않도록 먼저 내려 둔다.
        Topmost = false;
        window.Show();
    }

    private void UpdateTopmost(bool active)
    {
        // 작업 중인 팝업을 패널이 다시 덮지 않도록 감시 주기에서도 맨 위 표시를 쉰다.
        Topmost = active && !OwnedWindows.Cast<Window>().Any(window => window.IsVisible);
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

    /// <summary>
    /// 사용자가 쓰고 있는 온톡 창 옆에 붙는다. 온톡도 이 프로그램도 쓰지 않을 때는 숨는다.
    /// </summary>
    private void FollowDock()
    {
        if (_self == IntPtr.Zero)
        {
            return;
        }
        var target = _dock.Poll();
        if (_dock.Target != _dockedTo)
        {
            _chatPane?.Dispose(); _chatPane = null; _chatPaneKey = null; ChatHost.Content = null;
            _dockedTo = _dock.Target;
            _forceExpanded = false;
            _watcher.FollowTarget(_dockedTo);
            _context = null;
            _contextViewKey = null;
            _manualMenu = MainMenu.Unknown;
            _manualMenuWindow = IntPtr.Zero;
            MenuPicker.SelectedIndex = 0;
            ShowEmpty("현재 온톡 창의 작업을 확인하는 중입니다.");
            if (_latestSnapshot is { } latest && _store is not null) ApplyCurrentContext(latest);
        }
        if (target is { } onTalk && (_dock.OnTalkForeground || _dock.OwnForeground || _pinnedOpen))
        {
            if (_dock.OnTalkForeground)
            {
                _pinnedOpen = false;
            }
            MoveBeside(onTalk);
            if (!IsVisible)
            {
                Show();
            }
            // 온톡이나 패널을 쓰고 있을 때만 다른 창 위에 둔다.
            UpdateTopmost(_dock.OnTalkForeground || Win32.GetForegroundWindow() == _self);
            return;
        }

        UpdateTopmost(false);
        if (target is null)
        {
            Float();
        }
        if (!_pinnedOpen && !_dock.OwnForeground && _tray is { IsAvailable: true } && IsVisible)
        {
            Hide();
        }
    }

    private void MoveBeside(DockTarget onTalk)
    {
        var scale = onTalk.Scale;
        var full = DockPlanner.Place(onTalk.Window, onTalk.WorkArea, PanelWidthDip * scale,
            Math.Max(onTalk.Window.Height, PanelMinHeightDip * scale));
        // 펼칠 자리가 없으면 접힌 도구만 둔다.
        var rail = _collapsed || (full.Overlaps && !_forceExpanded);
        var bounds = rail
            ? DockPlanner.Place(onTalk.Window, onTalk.WorkArea, RailWidthDip * scale, RailHeightDip * scale,
                RailOverlapInsetDip * scale).Bounds
            : full.Bounds;
        ShowRail(rail);

        var (x, y) = ((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y));
        var (width, height) = ((int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
        if (Win32.GetWindowRect(_self, out var current) && current.Left == x && current.Top == y &&
            current.Width == width && current.Height == height)
        {
            return;
        }
        // 온톡 창을 소유자로 삼지 않는다. 쪽지 창이 닫힐 때 패널이 함께 닫히면 안 된다.
        SetWindowPos(_self, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>붙을 온톡 창이 없을 때의 크기. 있던 자리에 그대로 둔다.</summary>
    private void Float()
    {
        ShowRail(_collapsed);
        var (width, height) = _collapsed ? (RailWidthDip, RailHeightDip) : (PanelWidthDip, PanelHeightDip);
        if (Width != width || Height != height)
        {
            Width = width;
            Height = height;
        }
    }

    private void ShowRail(bool rail)
    {
        if (_railShown == rail)
        {
            return;
        }
        _railShown = rail;
        Rail.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
        Expanded.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Expand()
    {
        _collapsed = false;
        _forceExpanded = true;
        SaveCollapsed();
        FollowDock();
    }

    private void Collapse()
    {
        _collapsed = true;
        _forceExpanded = false;
        _pinnedOpen = false;
        SaveCollapsed();
        FollowDock();
    }

    /// <summary>단축키나 알림 영역 아이콘으로 불렀을 때. 숨어 있어도 펼쳐서 보여 준다.</summary>
    private void Reveal()
    {
        _pinnedOpen = true;
        _dock.Adopt();
        Expand();
        if (!IsVisible)
        {
            Show();
        }
        Activate();
    }

    private void SaveCollapsed()
    {
        if (_settings.Collapsed == _collapsed)
        {
            return;
        }
        _settings.Collapsed = _collapsed;
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 접힘 상태를 기억하지 못할 뿐이다.
        }
    }

    private void OnExpand(object sender, RoutedEventArgs e) => Expand();

    private void OnCollapse(object sender, RoutedEventArgs e) => Collapse();

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 온톡 창에 붙어 있지 않을 때만 끌어서 옮길 수 있다.
        if (_dock.Target == IntPtr.Zero && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            Reveal();
            handled = true;
        }
        else if (message == TrayIcon.CallbackMessage)
        {
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case WmLButtonUp:
                    Reveal();
                    handled = true;
                    break;
                case WmRButtonUp:
                    ShowTrayMenu();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu();
        void Add(string text, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("온톡 옆에서 열기", Reveal);
        Add("할 일 목록", OpenTasks, _store is not null);
        AddAutoStart(menu);
        Add("끝내기", Close);
        // 메뉴 밖을 눌렀을 때 닫히려면 이 프로그램이 앞에 있어야 한다.
        SetForegroundWindow(_self);
        menu.IsOpen = true;
    }

    /// <summary>체크 표시로 현재 등록 상태를 보여 주고, 누르면 등록과 해제를 바꾼다.</summary>
    private void AddAutoStart(ContextMenu menu)
    {
        var autoStart = AutoStart.ForCurrentProcess();
        var item = new MenuItem { Header = "Windows 시작 시 자동 실행", IsCheckable = true, IsEnabled = autoStart.CanRegister };
        try
        {
            item.IsChecked = autoStart.IsEnabled;
        }
        catch (System.Security.SecurityException)
        {
            item.IsEnabled = false;
        }
        item.Click += (_, _) =>
        {
            try
            {
                autoStart.SetEnabled(item.IsChecked);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                Dialogs.Show(this, $"자동 실행 설정을 바꾸지 못했습니다.{Environment.NewLine}{ex.Message}");
            }
        };
        menu.Items.Add(item);
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int WmHotKey = 0x0312;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
