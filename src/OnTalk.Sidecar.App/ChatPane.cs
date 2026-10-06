using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;
using OnTalk.Sidecar.Automation;

namespace OnTalk.Sidecar.App;

/// <summary>선택한 텍스트 범위를 고정한다. 입력은 명시적 비교 후에만 바꾸며 전송 동작은 없다.</summary>
public sealed class ChatPane : UserControl, IDisposable
{
    private readonly ProfileStore _store;
    private readonly OllamaAnalyzer _analyzer;
    private readonly OllamaWriter _writer;
    private readonly Guid _session;
    private readonly Action _changed;
    private readonly IChatInput? _input;
    private Task<ChatInputState?>? _pendingInputRead;
    private TimeSpan _inputReadWait = TimeSpan.FromSeconds(10);
    private readonly StackPanel _inputReview = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _inputBefore = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _inputAfter = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly CheckBox _inputConfirmed = new() { Content = "온톡의 실제 상대와 위 두 글을 확인했습니다" };
    private ChatInputState? _inputTicket;
    private DraftRevision _inputRevision;
    private readonly string _slot;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly HashSet<string> _selected = [];
    private ChatSnapshot _displayed;
    private ChatSnapshot _latest;
    private readonly TextBlock _coverage = new(), _notice = new(), _status = new(), _summary = new();
    private readonly StackPanel _rows = new(), _points = new();
    private readonly ComboBox _homeFriend = new();
    private readonly Button _saveHome = new() { Content = "이 친구의 Home에 정리 연결", IsEnabled = false };
    private ChatAnalysis? _homeAnalysis;
    private ChatSnapshot? _homeSource;
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _instruction = new() { Tag = "답변 목적과 확인한 사실을 적어 주세요" };
    private readonly TextBox _proposal = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100 };
    private readonly TextBox _taskTitle = new() { Tag = "내가 맡을 업무 제목" }, _evidence = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
    private readonly ComboBox _taskMessage = new() { DisplayMemberPath = "Label" };
    private readonly DatePicker _due = new();
    private readonly Button _accept = new() { Content = "제안을 초안에 수락", IsEnabled = false };
    private readonly Button _undo = new() { Content = "되돌리기", IsEnabled = false };
    private readonly Stack<string> _history = new();
    private CancellationTokenSource? _cancel;
    private long _version;
    private DraftRevision _proposedAt;
    private bool _active, _disposed, _busy;

    public string Draft => _editor.Text;
    public IReadOnlyList<ChatMessage> Chosen => _displayed.Messages.Where(m => _selected.Contains(m.Key)).ToList();
    public ChatSnapshot Source => _displayed;

    public ChatPane(ProfileStore store, OllamaAnalyzer analyzer, OllamaWriter writer, Guid session, ChatSnapshot snapshot, Action changed, IChatInput? input = null)
    {
        _store = store; _analyzer = analyzer; _writer = writer; _session = session; _changed = changed;
        _input = input;
        _slot = $"chat:{session:N}"; _displayed = _latest = snapshot;
        var root = new DockPanel { Margin = new Thickness(12), Background = (Brush)FindResource("PageBg") };
        TextBlock.SetForeground(root, (Brush)FindResource("TextStrong"));
        var top = new StackPanel(); top.Children.Add(Label(snapshot.RoomHint, "SectionTitle"));
        top.Children.Add(_coverage); top.Children.Add(_notice); top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        var tabs = new TabControl { Style = (Style)FindResource("PillTabs") }; root.Children.Add(tabs);
        var source = Body();
        source.Children.Add(Label("정리에 쓸 원문을 직접 고르세요. 읽힌 텍스트만 표시하며 첨부·이미지·시스템 알림은 분석하지 않습니다.", "Small"));
        source.Children.Add(Button("최근 10건 선택", () => SelectRecent()));
        source.Children.Add(Button("선택 해제", () => { _selected.Clear(); ClearHomeAnalysis(); Invalidate(); RenderMessages(); }));
        source.Children.Add(Button("현재 읽힌 범위 다시 불러오기", Reload));
        source.Children.Add(_rows); Tab(tabs, "원문 선택", source);
        var analysis = Body(); analysis.Children.Add(Button("선택한 원문 흐름 정리", async () => await AnalyzeAsync()));
        analysis.Children.Add(Label("AI 요약과 결정·질문·요청 후보입니다. 확정 여부·내 담당 여부는 원문과 이후 대화를 확인하세요.", "Caption"));
        analysis.Children.Add(_summary); analysis.Children.Add(_points);
        analysis.Children.Add(Label("다음 연락 때 이어볼 맥락 · Home", "SectionTitle"));
        analysis.Children.Add(Label("Home에서 관찰한 친구 중 실제 상대를 고르세요. 연결하면 선택 원문과 AI 정리를 이 계정에 보관합니다.", "Caption"));
        var friends = _store.GetHomePeople()?.People.GroupBy(p => p.DisplayIdentity).Where(g => g.Count() == 1).Select(g => g.Single()).ToList() ?? [];
        _homeFriend.ItemsSource = friends;
        analysis.Children.Add(_homeFriend); analysis.Children.Add(_saveHome);
        if (friends.Count == 0) analysis.Children.Add(Label("먼저 온톡 Home에서 친구 그룹을 펼쳐 주세요.", "Small"));
        _homeFriend.SelectionChanged += (_, _) => _saveHome.IsEnabled = _homeAnalysis?.Summary.Trim().Length > 0 && _homeFriend.SelectedItem is ContactPerson;
        _saveHome.Click += (_, _) => SaveToHome();
        Tab(tabs, "정리", analysis);
        var reply = Body(); reply.Children.Add(Label("짧은 답변 초안", "SectionTitle")); reply.Children.Add(_editor); reply.Children.Add(_instruction);
        reply.Children.Add(Button("선택한 원문으로 답변 제안", async () => await SuggestAsync()));
        reply.Children.Add(Label("제안 · 원문/현재 글과 비교한 뒤 수락", "Caption")); reply.Children.Add(_proposal); reply.Children.Add(_accept); reply.Children.Add(_undo);
        reply.Children.Add(Button("초안 복사", Copy));
        if (input is not null)
        {
            reply.Children.Add(Button("온톡 입력 글 가져오기", async () => await ImportInputAsync()));
            reply.Children.Add(Button("온톡 입력 반영 비교", async () => await ReviewInputAsync()));
            _inputReview.Children.Add(Label("온톡에서 읽은 현재 글 · 아래 초안으로 교체합니다", "Caption"));
            _inputReview.Children.Add(_inputBefore);
            _inputReview.Children.Add(Label("반영할 초안", "Caption")); _inputReview.Children.Add(_inputAfter);
            _inputReview.Children.Add(_inputConfirmed);
            _inputReview.Children.Add(Button("확인한 초안을 입력창에 반영", async () => await ApplyInputAsync()));
            reply.Children.Add(_inputReview);
        }
        reply.Children.Add(Label(input is null ? "초안을 복사해 온톡에서 실제 상대와 내용을 확인한 뒤 사용하세요. 전송은 온톡에서 직접 합니다." :
            "온톡 입력창을 확인할 수 있을 때 500자 이하의 짧은 글을 반영합니다. 실제 상대와 현재 글을 확인하세요. 전송은 온톡에서 직접 하세요. 입력창을 확인하지 못하면 초안 복사를 사용하세요.", "Caption")); Tab(tabs, "답변", reply);
        var task = Body(); task.Children.Add(Label("원문을 고르고 내 업무로 직접 등록", "SectionTitle")); task.Children.Add(_taskMessage);
        task.Children.Add(Label("등록하면 선택한 텍스트의 읽힌 원문·인용 근거·수집 시각을 이 계정에 보관합니다.", "Caption"));
        task.Children.Add(_taskTitle); task.Children.Add(Label("근거 · 선택한 본문에서 그대로 인용", "Caption")); task.Children.Add(_evidence);
        task.Children.Add(Label("기한 직접 선택 · 날짜 미확인 메시지에서 상대 날짜를 자동 추정하지 않습니다.", "Caption")); task.Children.Add(_due);
        task.Children.Add(Button("원문 근거와 함께 업무 등록", Register)); Tab(tabs, "업무", task);
        Content = root;
        foreach (var label in new[] { _coverage, _notice, _status, _summary }) { label.Style = (Style)FindResource("Small"); label.TextWrapping = TextWrapping.Wrap; label.Margin = new Thickness(0, 4, 0, 6); }
        _editor.TextChanged += (_, _) => { Invalidate(); _save.Stop(); if (!_disposed) _save.Start(); };
        _instruction.TextChanged += (_, _) => Invalidate();
        _save.Tick += (_, _) => { _save.Stop(); Save(); };
        _accept.Click += (_, _) => AcceptProposal();
        _undo.Click += (_, _) => { if (_disposed || !_active || _history.Count == 0) return; _editor.Text = _history.Pop(); _undo.IsEnabled = _history.Count > 0; };
        _taskMessage.SelectionChanged += (_, _) => { _evidence.Text = (_taskMessage.SelectedItem as ChatMessage)?.Body ?? ""; _taskTitle.Clear(); _due.SelectedDate = null; };
        RenderMessages();
    }

    private TextBlock Label(string text, string style) => new() { Text = text, Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private static StackPanel Body() => new() { Margin = new Thickness(2, 8, 2, 12) };
    private void Tab(TabControl tabs, string name, StackPanel body) => tabs.Items.Add(new TabItem { Header = name,
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Foreground = (Brush)FindResource("TextStrong"), FontWeight = FontWeights.Normal } });
    private Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 4, 0, 8) };
        button.Click += (_, _) => { if (_active && !_disposed && !_busy) action(); }; return button;
    }
    private DraftRevision Revision => new(_session, _version);
    private void Invalidate() { _version++; _accept.IsEnabled = false; _inputTicket = null; _inputReview.Visibility = Visibility.Collapsed; _inputConfirmed.IsChecked = false; }
    public void SetActive(bool active)
    {
        if (_active != active) { Invalidate(); _cancel?.Cancel(); }
        _active = active; IsEnabled = active && !_disposed && !_busy;
    }
    public void Refresh(ChatSnapshot snapshot)
    {
        _latest = snapshot;
        _notice.Text = snapshot.Fingerprint != _displayed.Fingerprint ? "새 메시지 또는 읽기 범위 변화가 있습니다. 선택한 원문과 초안은 유지됩니다. 원문 선택 탭에서 다시 불러오세요." : "";
    }
    private void Reload()
    {
        ClearHomeAnalysis();
        _displayed = _latest; _selected.Clear(); _summary.Text = ""; _points.Children.Clear();
        _proposal.Clear(); Invalidate(); RenderMessages();
        _status.Text = "원문 범위를 갱신했습니다. 사용할 메시지를 다시 선택하세요.";
    }
    private void SelectRecent()
    {
        ClearHomeAnalysis();
        _selected.Clear(); foreach (var message in _displayed.Messages.TakeLast(10)) _selected.Add(message.Key);
        Invalidate(); RenderMessages();
    }
    private void RenderMessages()
    {
        _coverage.Text = _displayed.Coverage + $"\n정리/답변에 선택한 원문 {Chosen.Count}건";
        _rows.Children.Clear();
        foreach (var message in _displayed.Messages)
        {
            var checkbox = new CheckBox { Content = Label(message.Label, "Body"), IsChecked = _selected.Contains(message.Key) };
            checkbox.Click += (_, _) => { if (checkbox.IsChecked == true) _selected.Add(message.Key); else _selected.Remove(message.Key); ClearHomeAnalysis(); Invalidate(); _coverage.Text = _displayed.Coverage + $"\n선택한 원문 {Chosen.Count}건"; };
            _rows.Children.Add(checkbox);
        }
        if (_displayed.Messages.Count == 0) _rows.Children.Add(Label("메시지 구조를 읽지 못했습니다. 온톡에서 날짜와 텍스트 메시지를 표시한 뒤 다시 확인하세요.", "Small"));
        _taskMessage.ItemsSource = _displayed.Messages;
        _taskMessage.SelectedIndex = -1;
    }
    private CancellationToken Begin()
    {
        _cancel?.Dispose(); _cancel = new CancellationTokenSource(); _busy = true; _status.Text = "선택한 자료로 로컬 AI 처리 중…"; return _cancel.Token;
    }
    private async Task AnalyzeAsync()
    {
        var messages = Chosen.ToList(); if (!CheckScope(messages)) return;
        ClearHomeAnalysis();
        var token = Begin(); var revision = Revision;
        try
        {
            var result = await _analyzer.AnalyzeChatAsync(messages, token);
            if (!CanAccept(revision)) { if (!_disposed) _status.Text = "선택 범위가 바뀌어 이전 정리 결과를 표시하지 않았습니다."; return; }
            _summary.Text = $"분석한 원문: {messages.Count}건 · {_displayed.ObservedAt.ToLocalTime():HH:mm:ss}에 읽은 범위\n" + result.Summary; _points.Children.Clear();
            foreach (var point in result.Points)
            {
                var kind = point.Kind switch { "decision" => "결정 후보", "question" => "질문 후보", _ => "요청/업무 후보" };
                _points.Children.Add(Label($"{kind}: {point.Title}\n{point.Message.Sender} · {point.Message.Date?.ToString("yyyy-MM-dd") ?? "날짜 미확인"} {point.Message.Time}\n근거: {point.Evidence}", "Body"));
            }
            _status.Text = $"선택한 {messages.Count}건 정리 · 원문 근거 불일치 {result.Dropped}건 제외";
            _homeAnalysis = result; _homeSource = _displayed with { Messages = messages };
            _saveHome.IsEnabled = _homeFriend.SelectedItem is ContactPerson && result.Summary.Trim().Length > 0;
        }
        catch (OperationCanceledException) { }
        catch (AnalysisUnavailableException ex) { if (!_disposed) _status.Text = ex.Message; }
        finally { _busy = false; }
    }
    private void ClearHomeAnalysis()
    {
        _homeAnalysis = null; _homeSource = null; _saveHome.IsEnabled = false;
    }
    private void SaveToHome()
    {
        if (_disposed || !_active || _busy || _homeAnalysis is null || _homeSource is null || _homeFriend.SelectedItem is not ContactPerson person) return;
        _store.SaveHomeChat(person, _homeSource, _homeAnalysis, DateTimeOffset.Now);
        _status.Text = $"{person.Name}의 Home에 선택 원문 {_homeSource.Messages.Count}건과 AI 정리를 연결했습니다.";
        _changed();
    }
    private async Task SuggestAsync()
    {
        var messages = Chosen.ToList(); if (!CheckScope(messages)) return;
        var token = Begin(); var revision = Revision;
        try
        {
            var text = await _writer.SuggestChatAsync(messages, _editor.Text, _instruction.Text, token);
            if (!CanAccept(revision)) { if (!_disposed) _status.Text = "글 또는 선택 범위가 바뀌어 이전 답변 제안을 버렸습니다."; return; }
            _proposal.Text = text; _proposedAt = revision; _accept.IsEnabled = true; _status.Text = "제안과 현재 글을 비교한 뒤 수락하세요.";
        }
        catch (OperationCanceledException) { }
        catch (AnalysisUnavailableException ex) { if (!_disposed) _status.Text = ex.Message; }
        finally { _busy = false; }
    }
    private bool CheckScope(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0) { _status.Text = "원문 선택 탭에서 사용할 메시지를 먼저 고르세요."; return false; }
        if (messages.Sum(m => m.Body.Length) > 12000 || messages.Count > 80) { _status.Text = "한 번에 최대 80건·12,000자 범위로 좁혀 주세요."; return false; }
        return true;
    }
    private bool CanAccept(DraftRevision revision) => !_disposed && _active && revision.Matches(Revision);
    private void AcceptProposal()
    {
        if (!CanAccept(_proposedAt) || _proposal.Text.Length == 0) return;
        _history.Push(_editor.Text); _editor.Text = _proposal.Text; _undo.IsEnabled = true;
    }
    private void Copy()
    {
        if (_editor.Text.Trim().Length == 0) return;
        try { Clipboard.SetText(_editor.Text); _status.Text = "초안을 복사했습니다. 온톡에서 대상과 내용을 확인하세요."; }
        catch (System.Runtime.InteropServices.COMException) { _status.Text = "클립보드를 사용하지 못했습니다. 글을 직접 선택해 복사하세요."; }
    }
    private async Task ImportInputAsync()
    {
        if (_input is null || !_active || _disposed || _busy) return;
        var revision = Revision;
        _busy = true;
        try
        {
            var current = await ReadInputAsync();
            if (!CanAccept(revision)) return;
            if (current is null) { _status.Text = "현재 대화 입력창을 확인하지 못했습니다. 온톡 창을 확인하세요."; return; }
            if (_editor.Text.Length > 0 && Dialogs.Show(Window.GetWindow(this), "현재 초안을 온톡 입력 글로 바꿀까요? 되돌리기로 복원할 수 있습니다.", "입력 글 가져오기", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            if (!CanAccept(revision)) return;
            _history.Push(_editor.Text); _editor.Text = current.Body; _undo.IsEnabled = true;
            _status.Text = "온톡의 입력 글을 가져왔습니다. 전송된 메시지와는 별도로 편집합니다.";
        }
        finally { _busy = false; }
    }
    private async Task ReviewInputAsync()
    {
        if (_input is null || !_active || _disposed || _busy) return;
        _inputTicket = null; _inputReview.Visibility = Visibility.Collapsed; _inputConfirmed.IsChecked = false;
        var revision = Revision; var draft = ChatInputWriter.Normalize(_editor.Text);
        if (string.IsNullOrWhiteSpace(draft) || draft.Length > 500 || draft.Any(c => char.IsControl(c) && c != '\n'))
        { _status.Text = "줄바꿈을 포함한 500자 이하의 짧은 글을 작성하세요."; return; }
        _busy = true;
        try
        {
            var current = await ReadInputAsync();
            if (!CanAccept(revision)) return;
            if (current is null) { _status.Text = "현재 대화 입력창을 확인하지 못했습니다. 초안 복사를 사용하세요."; return; }
            _inputTicket = current; _inputRevision = revision; _inputBefore.Text = current.Body;
            _inputAfter.Text = draft; _inputConfirmed.IsChecked = false; _inputReview.Visibility = Visibility.Visible;
            _inputReview.UpdateLayout(); _inputReview.BringIntoView();
            _status.Text = "온톡의 실제 상대와 현재 글을 확인한 뒤 반영하세요. 입력 글만 교체합니다.";
        }
        finally { _busy = false; }
    }
    private async Task<ChatInputState?> ReadInputAsync()
    {
        // 중단할 수 없는 접근성 호출이 늦어져도 UI를 풀고, 같은 읽기를 중복 시작하지 않는다.
        // 읽기 작업에는 쓰기가 없으므로 늦은 결과는 비교/초안에 적용하지 않고 버린다.
        if (_pendingInputRead is { IsCompleted: false }) return null;
        _pendingInputRead = Task.Run(_input!.Read);
        try { return await _pendingInputRead.WaitAsync(_inputReadWait); }
        catch (TimeoutException) { return null; }
    }

    private async Task ApplyInputAsync()
    {
        if (_input is null || _inputTicket is not { } ticket || !_active || _disposed || _busy || !CanAccept(_inputRevision)) return;
        if (_inputConfirmed.IsChecked != true) { _status.Text = "온톡의 실제 상대와 두 글을 확인해 체크하세요."; return; }
        var body = _inputAfter.Text;
        _inputTicket = null; _inputReview.Visibility = Visibility.Collapsed;
        _busy = true; IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _input.Apply(ticket, body));
            if (_disposed) return;
            _status.Text = result.Status switch
            {
                ChatInputStatus.Applied => "온톡 입력 글을 다시 읽어 초안과 일치함을 확인했습니다. 전송하지 않았습니다.",
                ChatInputStatus.Conflict => "온톡 입력 글이 바뀌어 반영하지 않았습니다. 다시 비교하세요.",
                ChatInputStatus.ContextChanged or ChatInputStatus.Unavailable => "계정·대상 창·읽기 범위가 바뀌었거나 입력창을 확인하지 못했습니다. 온톡을 확인하세요.",
                ChatInputStatus.InvalidText => "반영 가능한 짧은 글 범위를 벗어났습니다.",
                _ => "입력 반영 결과를 확인하지 못했습니다. 자동 재시도하지 않습니다. 온톡 입력창을 확인하고 필요하면 초안 복사를 사용하세요."
            };
        }
        finally { _busy = false; IsEnabled = _active && !_disposed; }
    }
    private void Register()
    {
        if (_taskMessage.SelectedItem is not ChatMessage message) { _status.Text = "업무의 근거 메시지를 직접 고르세요."; return; }
        var evidence = _evidence.Text.Trim(); var title = _taskTitle.Text.Trim();
        if (title.Length == 0 || evidence.Length < 2 || !message.Body.Contains(evidence, StringComparison.Ordinal)) { _status.Text = "업무 제목과 원문에 그대로 포함된 근거가 필요합니다."; return; }
        if (_store.GetTasks().Any(t => t.Title == title && _store.GetChatTaskSource(t.Id)?.MessageKey == message.Key)) { _status.Text = "이 메시지로 같은 제목의 업무를 이미 등록했습니다."; return; }
        var source = new ChatTaskSource(_displayed.RoomHint, message.Sender, message.Time, message.Date, message.Key, message.Body, evidence, _displayed.ObservedAt);
        _store.AddChatTask(title, _due.SelectedDate is { } due ? DateOnly.FromDateTime(due) : null, source, DateTimeOffset.Now);
        _changed(); _status.Text = "선택한 원문과 수집 시각을 함께 업무로 등록했습니다.";
    }
    private void Save()
    {
        if (_disposed || _editor.Text.Trim().Length == 0) return;
        _store.SaveDraft(_slot, _editor.Text, DateTimeOffset.Now);
    }
    public void Dispose()
    {
        if (_disposed) return; _save.Stop(); Save(); _disposed = true; Invalidate(); _cancel?.Cancel(); IsEnabled = false;
        // 초안은 복구 목록에 남기되, 고유 방 ID가 없어 다음 창에 자동 복원하지 않는다.
    }
}
