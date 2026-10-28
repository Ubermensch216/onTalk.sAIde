using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>
/// 현재 작성창 하나에 고정된 편집기. 메뉴를 옮겨도 초안은 유지한다.
/// 저장된 서식 불러오기·저장, AI 문장 다듬기(블럭 지정 구간 또는 전체), 요청문 기반 AI 제안을 갖추고,
/// 아래쪽에는 받는 사람과 최근 오간 쪽지·대화 3건을 보여 준다.
/// </summary>
public sealed class NoteComposePane : UserControl, IDisposable
{
    private const int RecentLimit = 3;
    private static readonly TimeSpan RecentRefreshAfter = TimeSpan.FromSeconds(20);

    private readonly ProfileStore _store;
    private readonly OllamaWriter _writer;
    private readonly IntPtr _hwnd;
    private readonly Guid _session;
    private readonly NoteComposeKind _kind;
    private readonly string _slot;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly TextBox _editor = new() { MinHeight = 145, IsInactiveSelectionHighlightEnabled = true };
    private readonly TextBox _instruction = new();
    private readonly TextBox _proposal = new() { IsReadOnly = true, MinHeight = 110, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _review = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _target = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _bytes = new();
    private readonly CheckBox _includeQuote = new() { IsChecked = true };
    private readonly CheckBox _useRecent = new() { IsChecked = true, Content = "최근 쪽지·대화를 AI 참고에 포함", Visibility = Visibility.Collapsed };
    private readonly Button _generate = new() { Content = "AI로 제안" };
    private readonly Button _accept = new() { Content = "제안을 초안에 적용" };
    private readonly Button _undo = new() { Content = "되돌리기", IsEnabled = false };
    private readonly Button _apply = new() { Content = "현재 온톡 창에 반영" };
    private readonly Button _reload = new() { Content = "온톡의 최신 글 가져오기" };
    private readonly ComboBox _templates = new() { Margin = new Thickness(0, 0, 6, 5) };
    private readonly TextBox _templateName = new() { MaxLength = 60, Margin = new Thickness(0, 0, 6, 5) };
    private readonly List<Button> _aiButtons = [];
    private readonly TextBlock _aiScope = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(2, 5, 2, 0) };
    private readonly StackPanel _recentList = new();
    private readonly TextBlock _recentHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Stack<(string Message, string Quote, bool IncludeQuote)> _history = [];
    private IReadOnlyList<RecentExchange> _recent = [];
    private string _recentKey = "";
    private DateTime _recentLoadedAt = DateTime.MinValue;
    private ComposeState _state;
    private string _expectedBody;
    private string _quote;
    private long _revision;
    private DraftRevision _proposedRevision;
    private bool _applyingTemplate;
    private bool _busy;
    private bool _disposed;
    private bool _active;

    public NoteComposePane(ProfileStore store, OllamaWriter writer, IntPtr hwnd, Guid session,
        NoteComposeKind kind, ComposeState state)
    {
        (_store, _writer, _hwnd, _session, _kind, _state) = (store, writer, hwnd, session, kind, state);
        _slot = ContextDrafts.Slot(session, kind);
        _expectedBody = state.Body;
        var parts = DraftText.SplitQuote(state.Body);
        _quote = parts.Quote;
        _editor.Text = store.GetDraft(_slot) ?? parts.Message;
        StyleControls();

        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(Label(SidecarPolicy.ComposePurpose(kind), "Body"));
        body.Children.Add(_target);

        // 본문 · 저장된 서식
        body.Children.Add(Label("현재 초안", "SectionTitle"));
        body.Children.Add(BuildTemplateGrid());
        body.Children.Add(_editor);
        body.Children.Add(_includeQuote);
        body.Children.Add(_bytes);

        // AI 문장 다듬기: 블럭 지정한 구간만, 없으면 전체
        body.Children.Add(BuildAiPanel());

        // 요청문으로 새로 쓰거나 고치기
        body.Children.Add(Label("생성·에디팅 요청", "SectionTitle"));
        _instruction.ToolTip = "예: 제출 방법을 문의하는 답장 / 현재 글을 간결하고 정중하게";
        body.Children.Add(_instruction);
        _useRecent.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(_useRecent);
        body.Children.Add(_generate);
        _generate.Margin = new Thickness(0, 8, 0, 0);
        _review.Children.Add(Label("제안된 글 · 수락 전에는 현재 글을 바꾸지 않습니다", "Small"));
        _review.Children.Add(_proposal);
        _review.Children.Add(_accept);
        body.Children.Add(_review);

        // 온톡 창에 반영
        body.Children.Add(_apply);
        body.Children.Add(_reload);
        body.Children.Add(_status);
        body.Children.Add(Label("전송·예약·보관 초안 불러오기는 머리줄의 작성 도우미 버튼에서 사용할 수 있습니다. 수신자·첨부·예약·SMS는 온톡에서 확인하세요.", "Caption"));

        // 받는 사람과 최근 오간 쪽지·대화
        body.Children.Add(Label("받는 사람과 최근 쪽지·대화", "SectionTitle"));
        _recentHint.Style = (Style)FindResource("Caption");
        body.Children.Add(_recentHint);
        body.Children.Add(_recentList);

        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var element in new FrameworkElement[] { _target, _bytes, _status, _includeQuote, _apply, _reload, _accept }) element.Margin = new Thickness(0, 8, 0, 0);
        _editor.TextChanged += (_, _) => { _revision++; _accept.IsEnabled = false; UpdateBytes(); UpdateAiScope(); _autosave.Stop(); if (!_disposed) _autosave.Start(); };
        _editor.SelectionChanged += (_, _) => UpdateAiScope();
        _includeQuote.Click += (_, _) => { _revision++; _accept.IsEnabled = false; UpdateBytes(); };
        _autosave.Tick += (_, _) => Save();
        _generate.Click += async (_, _) => await Suggest();
        _accept.Click += (_, _) => Accept();
        _undo.Click += (_, _) =>
        {
            if (_history.TryPop(out var previous))
            {
                _revision++; _accept.IsEnabled = false;
                _quote = previous.Quote; _includeQuote.IsChecked = previous.IncludeQuote; _editor.Text = previous.Message;
                Refresh(_state); UpdateBytes();
            }
            _undo.IsEnabled = _history.Count > 0;
        };
        _apply.Click += async (_, _) => await Apply();
        _reload.Click += (_, _) => ImportLatest();
        _templates.SelectionChanged += (_, _) => ApplySelectedTemplate();
        ReloadTemplates();
        UpdateAiScope();
        Refresh(state);
        UpdateBytes();
    }

    private TextBlock Label(string text, string style) => new() { Text = text, Style = (Style)FindResource(style), Margin = new Thickness(0, 12, 0, 6) };
    private string Final => DraftText.JoinQuote(_editor.Text, _includeQuote.IsChecked == true ? _quote : "");
    private DraftRevision Revision => new(_session, _revision);

    public string Draft => _editor.Text;
    public Guid SessionId => _session;
    public void RestoreDraft(string draft, string? quote = null, bool? includeQuote = null)
    {
        if (_disposed) return;
        _revision++; _accept.IsEnabled = false;
        Remember();
        if (quote is not null) _quote = quote;
        if (includeQuote is not null) _includeQuote.IsChecked = includeQuote;
        _editor.Text = draft; Refresh(_state); UpdateBytes();
    }

    public void SetActive(bool active)
    {
        if (_active != active) { _revision++; _accept.IsEnabled = false; }
        _active = active;
        Enable();
    }

    public void Refresh(ComposeState state)
    {
        _state = state;
        _target.Text = $"수신 {state.Recipients.Count}명: {string.Join(", ", state.Recipients)}" +
            (state.Reservation is { } time ? $" · 예약 {time:MM-dd HH:mm}" : " · 즉시 전송") + (state.SmsChecked ? " · SMS 선택됨" : "");
        _includeQuote.Content = $"원문 인용 포함 ({NoteBytes.Count(_quote)} Bytes)";
        _includeQuote.Visibility = _quote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (state.Body != _expectedBody && !_busy) _status.Text = "온톡의 본문이 바뀌었습니다. 최신 글을 가져오거나 내용을 비교한 뒤 반영하세요.";
        RefreshRecent();
        Enable();
    }

    // ───────────────────────── 화면 조각 ─────────────────────────

    private void StyleControls()
    {
        _editor.Style = (Style)FindResource("MultilineBox");
        _editor.MinHeight = 145;
        _editor.Tag = "보낼 쪽지 내용을 입력하세요. 특정 문장만 다듬으려면 블럭으로 지정한 뒤 아래 AI 단추를 누르세요.";
        _editor.AutomationName("쪽지 내용");
        _instruction.Style = (Style)FindResource("FieldBox");
        _instruction.AutomationName("생성·에디팅 요청");
        _templateName.Style = (Style)FindResource("FieldBox");
        _templateName.Tag = "서식 제목";
        _templateName.AutomationName("서식 제목");
        _templates.AutomationName("저장된 서식");
        _templates.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"{Binding Name}\" TextTrimming=\"CharacterEllipsis\"/></DataTemplate>");
        _aiScope.Foreground = (Brush)FindResource("Muted");
    }

    private Grid BuildTemplateGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < 2; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row); Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
        TextBlock Caption(string text) => new() { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 5) };
        Button Small(string text, RoutedEventHandler handler, string tip)
        {
            var button = new Button { Content = text, Style = (Style)FindResource("SmallButton"), ToolTip = tip, Margin = new Thickness(0, 0, 0, 5) };
            button.Click += handler;
            return button;
        }
        Place(Caption("저장된 서식"), 0, 0);
        Place(_templates, 0, 1);
        Place(Small("서식 삭제", (_, _) => DeleteSelectedTemplate(), "고른 저장 서식을 목록에서 지웁니다."), 0, 2);
        Place(Caption("서식 제목"), 1, 0);
        Place(_templateName, 1, 1);
        Place(Small("서식 저장", (_, _) => SaveTemplate(), "지금 내용을 이 제목의 서식으로 저장합니다. 같은 제목이 있으면 바꿉니다."), 1, 2);
        return grid;
    }

    private Border BuildAiPanel()
    {
        var stack = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        _undo.Style = (Style)FindResource("SmallButton");
        _undo.Focusable = false; _undo.FontSize = 11; _undo.MinHeight = 20; _undo.Padding = new Thickness(6, 1, 6, 1);
        _undo.ToolTip = "직전 AI 변환·제안 적용·서식 불러오기 전 상태로 되돌립니다.";
        DockPanel.SetDock(_undo, Dock.Right);
        header.Children.Add(_undo);
        header.Children.Add(new TextBlock
        {
            Text = "✦ AI 문장 다듬기", FontSize = 11, Foreground = (Brush)FindResource("AccentText"), VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(header);

        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var preset in OllamaWriter.RefinePresets)
        {
            var button = new Button
            {
                Content = preset.Label, Style = (Style)FindResource("SmallButton"), Focusable = false, Margin = new Thickness(1.5),
                Padding = new Thickness(3, 0, 3, 0), MinHeight = 26, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = preset.Description,
            };
            button.AutomationName("AI " + preset.Label);
            button.Click += async (_, _) => await Refine(preset);
            _aiButtons.Add(button);
            grid.Children.Add(button);
        }
        stack.Children.Add(grid);
        stack.Children.Add(_aiScope);
        return new Border
        {
            Margin = new Thickness(0, 10, 0, 0), Background = (Brush)FindResource("AccentSoft"),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#CFDBFF")!, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(7), Child = stack,
        };
    }

    // ───────────────────────── 편집 ─────────────────────────

    private void Enable()
    {
        var ready = _active && !_disposed && !_busy && !_state.DialogOpen;
        _apply.IsEnabled = ready;
        _reload.IsEnabled = ready;
        _generate.IsEnabled = ready;
        foreach (var button in _aiButtons) button.IsEnabled = ready;
        _accept.IsEnabled = ready && _review.Visibility == Visibility.Visible && _proposedRevision.Matches(Revision);
    }

    private void Remember() { _history.Push((_editor.Text, _quote, _includeQuote.IsChecked == true)); _undo.IsEnabled = true; }
    private void UpdateBytes() => _bytes.Text = $"{NoteBytes.Count(Final)}/{_state.LimitBytes} Bytes · 인용 포함 최종 본문";
    private void Save() { _autosave.Stop(); if (!_disposed) _store.SaveDraft(_slot, _editor.Text, DateTimeOffset.Now); }

    private void UpdateAiScope()
    {
        if (DraftText.TryTrimSelection(_editor.Text, _editor.SelectionStart, _editor.SelectionLength, out _, out var length, out _))
        {
            _aiScope.Text = $"선택 구간만 변환 ({length}자)";
            _aiScope.Foreground = (Brush)FindResource("AccentText");
        }
        else
        {
            _aiScope.Text = "블럭 지정 시 그 구간만, 없으면 전체 변환";
            _aiScope.Foreground = (Brush)FindResource("Muted");
        }
    }

    // ───────────────────────── 저장된 서식 ─────────────────────────

    private void ReloadTemplates(string? select = null)
    {
        _applyingTemplate = true;
        try
        {
            var list = _store.GetTemplates();
            _templates.ItemsSource = list;
            _templates.SelectedItem = select is null ? null : list.FirstOrDefault(t => t.Name == select);
        }
        finally { _applyingTemplate = false; }
    }

    private void ApplySelectedTemplate()
    {
        if (_applyingTemplate || _templates.SelectedItem is not Template chosen) return;
        if (_editor.Text.Trim().Length > 0 && _editor.Text != chosen.Body &&
            Dialogs.Show(Window.GetWindow(this), $"지금 초안을 ‘{chosen.Name}’ 서식으로 바꿀까요? 되돌리기로 복원할 수 있습니다.",
                "서식 불러오기", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
        {
            ReloadTemplates(_templateName.Text.Trim());
            return;
        }
        // 사람마다 바뀌는 {변수}는 한 통의 쪽지에서는 채울 수 없으므로 개인별 서식에서 온 변수는 지운다.
        var text = chosen.Body;
        var variables = NotePersonalization.ExtractVariables(text);
        foreach (var variable in variables) text = NotePersonalization.RemoveVariable(text, variable);
        _revision++; _accept.IsEnabled = false;
        Remember();
        _templateName.Text = chosen.Name;
        _editor.Text = text;
        _status.Text = variables.Count == 0
            ? $"‘{chosen.Name}’ 서식을 불러왔습니다."
            : $"‘{chosen.Name}’ 서식을 불러왔습니다. 개인별 변수 {variables.Count}개는 뺐으니 본문을 확인하세요.";
    }

    private void SaveTemplate()
    {
        var name = _templateName.Text.Trim();
        if (name.Length == 0) { _status.Text = "서식 제목을 입력하세요."; _templateName.Focus(); return; }
        if (_editor.Text.Trim().Length == 0) { _status.Text = "초안이 비어 있어 서식으로 저장하지 않았습니다."; _editor.Focus(); return; }
        var existing = _store.GetTemplates().FirstOrDefault(t => t.Name == name);
        if (existing is not null && existing.Body != _editor.Text &&
            Dialogs.Show(Window.GetWindow(this), $"‘{name}’ 서식이 이미 있습니다. 지금 초안으로 바꿀까요?", "서식 덮어쓰기", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _store.SaveTemplate(name, _editor.Text);
        ReloadTemplates(name);
        _status.Text = existing is null ? $"‘{name}’ 서식을 저장했습니다." : $"‘{name}’ 서식을 바꿔 저장했습니다.";
    }

    private void DeleteSelectedTemplate()
    {
        if (_templates.SelectedItem is not Template chosen) { _status.Text = "삭제할 서식을 먼저 고르세요."; return; }
        if (Dialogs.Show(Window.GetWindow(this), $"‘{chosen.Name}’ 서식을 삭제할까요?", "서식 삭제", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _store.DeleteTemplate(chosen.Id);
        ReloadTemplates();
        _status.Text = $"‘{chosen.Name}’ 서식을 삭제했습니다.";
    }

    // ───────────────────────── AI ─────────────────────────

    /// <summary>블럭 지정한 구간만, 없으면 전체 초안을 선택한 방식으로 다듬어 바로 적용한다. 되돌리기로 복원할 수 있다.</summary>
    private async Task Refine(AiRefinePreset preset)
    {
        if (!_active || _disposed || _busy) return;
        var snapshot = _editor.Text;
        if (snapshot.Trim().Length == 0) { _status.Text = "다듬을 초안을 먼저 입력하세요."; _editor.Focus(); return; }
        var selectionStart = _editor.SelectionStart;
        var selectionLength = _editor.SelectionLength;
        var isBlock = DraftText.TryTrimSelection(snapshot, selectionStart, selectionLength, out _, out var blockLength, out var block);
        var before = Revision;
        _busy = true; Enable();
        _status.Text = isBlock
            ? $"로컬 AI가 선택한 구간({blockLength}자)을 ‘{preset.Label}’ 방식으로 다듬는 중입니다."
            : $"로컬 AI가 전체 초안을 ‘{preset.Label}’ 방식으로 다듬는 중입니다.";
        try
        {
            var refined = await _writer.RefineAsync(snapshot, isBlock ? block : null, preset.Instruction, _closing.Token);
            if (_disposed) return;
            if (!before.Matches(Revision) || !_active)
            {
                _status.Text = "초안이나 작업이 바뀌어 AI 변환 결과를 적용하지 않았습니다. 필요하면 다시 눌러 주세요.";
                return;
            }
            if (string.IsNullOrWhiteSpace(refined)) { _status.Text = "AI로부터 빈 응답을 받아 초안을 유지했습니다."; return; }
            var applied = DraftText.ApplyRefinement(snapshot, selectionStart, selectionLength, refined);
            _revision++; _accept.IsEnabled = false;
            Remember();
            _editor.Text = applied.Text;
            _editor.Focus();
            if (applied.IsBlock)
            {
                _editor.Select(applied.SelectionStart, applied.SelectionLength);
                _status.Text = $"선택한 문장을 ‘{preset.Label}’ 방식으로 바꿨습니다. 마음에 들지 않으면 ‘되돌리기’를 누르세요.";
            }
            else
            {
                _editor.CaretIndex = _editor.Text.Length;
                _status.Text = $"전체 초안을 ‘{preset.Label}’ 방식으로 다듬었습니다. 사실·날짜를 확인하세요.";
            }
        }
        catch (OperationCanceledException) { }
        catch (AnalysisUnavailableException ex) { if (!_disposed) _status.Text = ex.Message; }
        catch (Exception ex) { if (!_disposed) _status.Text = $"다듬지 못했습니다. {ex.Message}"; }
        finally { _busy = false; Enable(); }
    }

    private async Task Suggest()
    {
        if (_instruction.Text.Trim().Length == 0) { _status.Text = "작성하거나 고칠 내용을 요청 칸에 적어 주세요."; return; }
        var before = Revision;
        var recent = _useRecent.IsChecked == true && _recent.Count > 0 ? RecentForAi() : "";
        _busy = true; Enable(); _status.Text = "로컬 AI가 제안을 만드는 중입니다.";
        try
        {
            var text = await _writer.SuggestAsync(_kind, _editor.Text, _quote, _instruction.Text.Trim(), _closing.Token, recent);
            if (_disposed) return;
            if (!before.Matches(Revision) || !_active)
            {
                _status.Text = "초안이나 작업이 바뀌어 이전 요청의 제안을 적용하지 않았습니다. 필요하면 다시 요청하세요.";
                return;
            }
            _proposal.Text = text; _proposedRevision = before; _review.Visibility = Visibility.Visible; _accept.IsEnabled = true;
            _status.Text = "현재 초안과 제안을 비교해 필요한 경우 수락하세요. 사실·날짜·약속을 확인하세요.";
        }
        catch (OperationCanceledException) { }
        catch (AnalysisUnavailableException ex) { if (!_disposed) _status.Text = ex.Message; }
        catch (Exception ex) { if (!_disposed) _status.Text = $"제안을 만들지 못했습니다. {ex.Message}"; }
        finally { _busy = false; Enable(); }
    }

    private void Accept()
    {
        if (!_active || !_proposedRevision.Matches(Revision)) { _status.Text = "초안이나 작업이 바뀌어 이 제안을 적용하지 않았습니다."; return; }
        Remember(); _editor.Text = _proposal.Text; _review.Visibility = Visibility.Collapsed;
    }

    // ───────────────────────── 최근 쪽지·대화 ─────────────────────────

    private static string Flatten(string text, int max)
    {
        var flat = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private string RecentForAi() => string.Join("\n\n", _recent.Select(e =>
        $"[{KindLabel(e.Kind)} · {e.At.ToLocalTime():yyyy-MM-dd HH:mm} · {e.Counterpart}]\n{Flatten(e.Body, 500)}"));

    private static string KindLabel(ExchangeKind kind) => kind switch
    {
        ExchangeKind.Received => "받은 쪽지",
        ExchangeKind.Sent => "보낸 쪽지",
        _ => "대화 정리",
    };

    /// <summary>받는 사람이 바뀌었거나 한동안 읽지 않았을 때만 보관 자료에서 다시 읽는다.</summary>
    private void RefreshRecent()
    {
        var names = _state.Recipients.Where(n => n.Length > 0).Distinct().ToList();
        var key = string.Join("\n", names);
        if (key == _recentKey && DateTime.UtcNow - _recentLoadedAt < RecentRefreshAfter) return;
        _recentKey = key;
        _recentLoadedAt = DateTime.UtcNow;
        try
        {
            if (names.Count == 0) _recent = [];
            else
            {
                IReadOnlyList<ContactPerson> people = _store.GetHomePeople()?.People ?? [];
                IReadOnlyList<HomeChatDigest> chats = people.Any(p => names.Contains(p.Name)) ? _store.GetHomeChats() : [];
                _recent = RecentExchange.Build(names, _store.GetNotesFromSenders(names, RecentLimit),
                    _store.GetDispatches(200), chats, people, RecentLimit);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _recent = [];
            _recentHint.Text = $"최근 내역을 읽지 못했습니다. {ex.Message}";
            _recentList.Children.Clear();
            return;
        }
        BuildRecentList(names);
    }

    private void BuildRecentList(IReadOnlyList<string> names)
    {
        _recentList.Children.Clear();
        _useRecent.Visibility = _recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (names.Count == 0) { _recentHint.Text = "받는 사람이 정해지면 그 사람과 최근 오간 쪽지·대화 3건을 보여 줍니다."; return; }
        if (_recent.Count == 0)
        {
            _recentHint.Text = $"{string.Join(", ", names)} 님과 이 PC에 보관된 이전 쪽지·대화가 없습니다. 이 PC에서 읽어 보관한 받은 쪽지, 사이드카로 보낸 쪽지, 연결해 둔 대화 정리만 보여 줍니다.";
            return;
        }
        _recentHint.Text = $"{string.Join(", ", names)} 님과 최근 {_recent.Count}건 · 눌러서 전체 내용을 펼치거나 접습니다.";
        foreach (var exchange in _recent) _recentList.Children.Add(BuildRecentCard(exchange));
    }

    private Border BuildRecentCard(RecentExchange exchange)
    {
        var at = exchange.At.ToLocalTime();
        var stamp = at.Year == DateTime.Now.Year ? $"{at:MM-dd HH:mm}" : $"{at:yyyy-MM-dd HH:mm}";
        var accent = exchange.Kind == ExchangeKind.Sent;
        var badge = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 6, 0),
            Background = (Brush)FindResource(accent ? "AccentSoft" : "SurfaceAlt"),
            Child = new TextBlock { Text = KindLabel(exchange.Kind), FontSize = 11, Foreground = (Brush)FindResource(accent ? "AccentText" : "Muted") },
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(badge);
        head.Children.Add(new TextBlock { Text = $"{stamp} · {exchange.Counterpart}", FontSize = 11, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var preview = new TextBlock { Text = Flatten(exchange.Body, 160), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis };
        var expanded = false;
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(preview);
        var card = new Border
        {
            Child = stack, Padding = new Thickness(9, 6, 9, 7), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("Line"), Background = (Brush)FindResource("Surface"),
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "눌러서 전체 내용을 펼치거나 접습니다.",
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            expanded = !expanded;
            preview.Text = expanded ? exchange.Body.Replace("\r\n", "\n").Trim() : Flatten(exchange.Body, 160);
            preview.MaxHeight = expanded ? double.PositiveInfinity : 52;
        };
        return card;
    }

    // ───────────────────────── 온톡 창과 주고받기 ─────────────────────────

    private void ImportLatest()
    {
        if (!_active || _disposed || _busy) return;
        if (_editor.Text.Length > 0 && Dialogs.Show(Window.GetWindow(this), "현재 초안을 온톡의 최신 글로 바꿀까요? 되돌리기로 복원할 수 있습니다.",
                "최신 글 가져오기", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        // 최신 스냅샷의 값은 반영 시 다시 읽어 비교한다.
        _revision++; _accept.IsEnabled = false;
        Remember(); _expectedBody = _state.Body; var parts = DraftText.SplitQuote(_state.Body); _quote = parts.Quote; _editor.Text = parts.Message;
        Refresh(_state); UpdateBytes(); _status.Text = "온톡의 글을 가져왔습니다.";
    }

    private async Task Apply()
    {
        if (!_active || _disposed || _busy) return;
        var final = Final;
        if (DraftText.UnfilledPlaceholders(final).Count > 0) { _status.Text = "{ }로 남은 확인할 내용을 채운 뒤 반영하세요."; return; }
        if (NoteBytes.Count(final) > _state.LimitBytes) { _status.Text = "온톡의 본문 바이트 제한을 넘습니다."; return; }
        _busy = true; Enable();
        try
        {
            var result = await Task.Run(() => ComposeWriter.ApplyBody(_hwnd, final, _expectedBody));
            if (_disposed) return;
            if (result.Status == ApplyStatus.Applied)
            {
                _expectedBody = final; if (result.After is { } after) Refresh(after);
                _status.Text = "현재 작성창의 본문과 바이트 표시가 일치한 것을 확인했습니다. 전송하지 않았습니다.";
            }
            else _status.Text = result.Status == ApplyStatus.Conflict
                ? "온톡에서 본문이 변경되어 반영하지 않았습니다. 최신 글을 가져와 비교하세요."
                : $"반영하지 못했습니다 ({result.Status}). 온톡 창을 확인하세요.";
        }
        catch (Exception ex) { if (!_disposed) _status.Text = $"반영을 확인하지 못했습니다. 온톡 창을 확인하세요. {ex.Message}"; }
        finally { _busy = false; Enable(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Save(); _disposed = true; _active = false; _closing.Cancel(); _autosave.Stop();
    }
}
