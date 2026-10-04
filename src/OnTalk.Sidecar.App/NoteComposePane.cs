using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>현재 작성창 하나에 고정된 편집기. 메뉴를 옮겨도 초안은 유지한다.</summary>
public sealed class NoteComposePane : UserControl, IDisposable
{
    private readonly ProfileStore _store;
    private readonly OllamaWriter _writer;
    private readonly IntPtr _hwnd;
    private readonly Guid _session;
    private readonly NoteComposeKind _kind;
    private readonly string _slot;
    private readonly CancellationTokenSource _closing = new();
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly TextBox _editor = new() { AcceptsReturn = true, MinHeight = 145, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _instruction = new();
    private readonly TextBox _proposal = new() { IsReadOnly = true, MinHeight = 110, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _review = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _target = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _bytes = new();
    private readonly CheckBox _includeQuote = new() { IsChecked = true };
    private readonly Button _generate = new() { Content = "AI로 제안" };
    private readonly Button _accept = new() { Content = "제안을 초안에 적용" };
    private readonly Button _undo = new() { Content = "되돌리기", IsEnabled = false };
    private readonly Button _apply = new() { Content = "현재 온톡 창에 반영" };
    private readonly Button _reload = new() { Content = "온톡의 최신 글 가져오기" };
    private readonly Stack<(string Message, string Quote, bool IncludeQuote)> _history = [];
    private ComposeState _state;
    private string _expectedBody;
    private string _quote;
    private long _revision;
    private DraftRevision _proposedRevision;
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
        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(Label(SidecarPolicy.ComposePurpose(kind), "Body"));
        body.Children.Add(_target);
        body.Children.Add(Label("현재 초안", "SectionTitle"));
        body.Children.Add(_editor);
        body.Children.Add(_includeQuote);
        body.Children.Add(Label("전송·예약·보관 초안 불러오기는 머리줄의 작성 도우미 버튼에서 사용할 수 있습니다.", "Caption"));
        var templates = new ComboBox { ItemsSource = store.GetTemplates(), DisplayMemberPath = "Name", Margin = new Thickness(0, 10, 0, 6) };
        var insert = new Button { Content = "선택한 문구 넣기" };
        insert.Click += (_, _) => { if (templates.SelectedItem is Template t) { Remember(); _editor.SelectedText = t.Body; } };
        body.Children.Add(templates);
        body.Children.Add(insert);
        body.Children.Add(Label("생성·에디팅 요청", "SectionTitle"));
        _instruction.ToolTip = "예: 제출 방법을 문의하는 답장 / 현재 글을 간결하고 정중하게";
        body.Children.Add(_instruction);
        var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        tools.Children.Add(_generate);
        tools.Children.Add(_undo);
        body.Children.Add(tools);
        _review.Children.Add(Label("제안된 글 · 수락 전에는 현재 글을 바꾸지 않습니다", "Small"));
        _review.Children.Add(_proposal);
        _review.Children.Add(_accept);
        body.Children.Add(_review);
        body.Children.Add(_bytes);
        body.Children.Add(_apply);
        body.Children.Add(_reload);
        body.Children.Add(_status);
        body.Children.Add(Label("수신자·첨부·예약·SMS를 온톡에서 확인하세요. 전송/예약 상세는 확장 작성 도우미에서 검토합니다.", "Caption"));
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var element in new FrameworkElement[] { _target, _bytes, _status, _includeQuote, _apply, _reload, _accept }) element.Margin = new Thickness(0, 8, 0, 0);
        _editor.TextChanged += (_, _) => { _revision++; _accept.IsEnabled = false; UpdateBytes(); _autosave.Stop(); if (!_disposed) _autosave.Start(); };
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
        Enable();
    }

    private void Enable()
    {
        var ready = _active && !_disposed && !_busy && !_state.DialogOpen;
        _apply.IsEnabled = ready;
        _reload.IsEnabled = ready;
        _generate.IsEnabled = ready;
        _accept.IsEnabled = ready && _review.Visibility == Visibility.Visible && _proposedRevision.Matches(Revision);
    }

    private void Remember() { _history.Push((_editor.Text, _quote, _includeQuote.IsChecked == true)); _undo.IsEnabled = true; }
    private void UpdateBytes() => _bytes.Text = $"{NoteBytes.Count(Final)}/{_state.LimitBytes} Bytes · 인용 포함 최종 본문";
    private void Save() { _autosave.Stop(); if (!_disposed) _store.SaveDraft(_slot, _editor.Text, DateTimeOffset.Now); }

    private async Task Suggest()
    {
        if (_instruction.Text.Trim().Length == 0) { _status.Text = "작성하거나 고칠 내용을 요청 칸에 적어 주세요."; return; }
        var before = Revision;
        _busy = true; Enable(); _status.Text = "로컬 AI가 제안을 만드는 중입니다.";
        try
        {
            var text = await _writer.SuggestAsync(_kind, _editor.Text, _quote, _instruction.Text.Trim(), _closing.Token);
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
