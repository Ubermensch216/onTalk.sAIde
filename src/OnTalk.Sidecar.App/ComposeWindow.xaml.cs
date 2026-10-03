using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>반영 대상으로 고를 수 있는 온톡 작성 창.</summary>
public sealed record ComposeTarget(IntPtr Hwnd, string Label, ComposeState State);

/// <summary>
/// 초안을 쓰고 온톡 작성 창에 반영한다. 초안은 계정 저장소에 자동 저장된다.
/// 계정이 바뀌면 패널이 이 창을 닫는다.
/// </summary>
public partial class ComposeWindow : Window
{
    private readonly ProfileStore _store;
    private readonly OllamaWriter _writer;
    private readonly StoredNote? _source;
    private readonly string _slot;
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly CancellationTokenSource _closing = new();

    private IntPtr _target;
    // 마지막으로 온톡 창과 맞춘 본문. 온톡 본문이 이와 달라졌으면 사용자가 온톡에서 직접 고친 것이다.
    private string _synced = "";
    private string _quote = "";
    private bool _busy;
    private ComposeState? _state;
    private string _conflictNote = "";

    public ComposeWindow(ProfileStore store, OllamaWriter writer, StoredNote? source)
    {
        InitializeComponent();
        _store = store;
        _writer = writer;
        _source = source;
        _slot = source is null ? DraftText.NewSlot : DraftText.ReplySlot(source.Id);
        SourceText.Text = source is null
            ? "새 쪽지 초안"
            : $"답장 초안 · {source.Note.SenderName} ({source.Note.SentAt?.Value.ToString("yyyy-MM-dd") ?? source.Note.SentAtText})";
        Editor.Text = store.GetDraft(_slot) ?? "";
        ReloadTemplates();
        InitializeDispatchControls();
        UpdateTargets([]);
        UpdateBytes();
        _autosave.Tick += (_, _) => SaveDraft();
        Loaded += (_, _) => Editor.Focus();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            SaveDraft();
        };
    }

    private string Final => DraftText.JoinQuote(Editor.Text, IncludeQuote.IsChecked == true ? _quote : "");

    /// <summary>패널이 감시 주기마다 호출한다. 지금 열려 있는 온톡 작성 창 목록.</summary>
    public void UpdateTargets(IReadOnlyList<ComposeTarget> targets)
    {
        var current = TargetBox.ItemsSource as IReadOnlyList<ComposeTarget> ?? [];
        if (!current.Select(t => (t.Hwnd, t.Label)).SequenceEqual(targets.Select(t => (t.Hwnd, t.Label))))
        {
            TargetBox.ItemsSource = targets;
            TargetBox.SelectedItem = targets.FirstOrDefault(t => t.Hwnd == _target) ?? targets.FirstOrDefault();
        }
        if (targets.Count == 0)
        {
            _target = IntPtr.Zero;
            TargetHint.Text = _source is null
                ? "열려 있는 온톡 작성 창이 없습니다. 온톡에서 새 쪽지 창을 열면 여기에 나타납니다."
                : "열려 있는 온톡 작성 창이 없습니다. 온톡 받은 쪽지 창에서 답장을 누르면 여기에 나타납니다.";
        }
        _state = targets.FirstOrDefault(t => t.Hwnd == _target)?.State;
        RefreshHint();
        EnableControls();
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetBox.SelectedItem is not ComposeTarget target || target.Hwnd == _target)
        {
            return;
        }
        _target = target.Hwnd;
        _state = target.State;
        Adopt(target.State, importMessage: Editor.Text.Length == 0);
        EnableControls();
    }

    /// <summary>온톡 창의 현재 본문을 기준으로 삼는다. 온톡이 넣어 둔 원문 인용은 따로 보관한다.</summary>
    private void Adopt(ComposeState state, bool importMessage)
    {
        _synced = state.Body;
        var (message, quote) = DraftText.SplitQuote(state.Body);
        _quote = quote;
        IncludeQuote.Visibility = quote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        IncludeQuote.Content = $"온톡이 넣은 원문 인용 포함 ({NoteBytes.Count(quote)} Bytes)";
        if (importMessage && message.Length > 0)
        {
            Editor.Text = message;
        }
        _conflictNote = message.Length > 0 && !importMessage && message != Editor.Text
            ? " · 온톡 창에 이미 쓴 글이 있습니다. 반영하면 바꿀지 물어봅니다."
            : "";
        RefreshHint();
        if (message.Length > 0 && !importMessage && message != Editor.Text)
        {
            // 온톡 창의 글을 초안과 맞춘 적이 없다. 인용만 있는 상태를 기준으로 삼아 반영할 때 충돌로 걸리게 한다.
            _synced = DraftText.JoinQuote("", quote);
        }
        UpdateBytes();
    }

    private void OnQuoteToggled(object sender, RoutedEventArgs e) => UpdateBytes();

    private void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }
        UpdateBytes();
        _autosave.Stop();
        _autosave.Start();
    }

    private void SaveDraft()
    {
        _autosave.Stop();
        _store.SaveDraft(_slot, Editor.Text, DateTimeOffset.Now);
    }

    private void UpdateBytes()
    {
        var mine = NoteBytes.Count(Editor.Text);
        var total = NoteBytes.Count(Final);
        ByteText.Text = total == mine
            ? $"{total}/{NoteBytes.Limit} Bytes"
            : $"{total}/{NoteBytes.Limit} Bytes (내 글 {mine} + 인용 {total - mine})";
        ByteText.Foreground = total > NoteBytes.Limit ? Brushes.Firebrick : Brushes.Black;
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        var final = Final;
        if (DraftText.UnfilledPlaceholders(final) is { Count: > 0 } unfilled)
        {
            Result($"채우지 않은 자리가 있어 반영하지 않았습니다: {string.Join(" ", unfilled)}", ok: false);
            return;
        }
        if (NoteBytes.Count(final) > NoteBytes.Limit)
        {
            Result($"{NoteBytes.Limit}바이트를 넘어 반영하지 않았습니다. 글을 줄이거나 원문 인용을 빼세요.", ok: false);
            return;
        }
        await ApplyAsync(final, _synced, askOnConflict: true);
    }

    private async Task<bool> ApplyAsync(string final, string expected, bool askOnConflict)
    {
        var hwnd = _target;
        _busy = true;
        EnableControls();
        Result("온톡 창에 반영하는 중", ok: true);
        ApplyResult result;
        try
        {
            // 클릭·붙여넣기·다시 읽기에 1~2초 걸리므로 화면이 멈추지 않게 따로 돌린다.
            result = await Task.Run(() => ComposeWriter.ApplyBody(hwnd, final, expected));
        }
        finally
        {
            _busy = false;
            EnableControls();
        }

        if (result.Status == ApplyStatus.Conflict && result.After is { } current && askOnConflict)
        {
            var choice = MessageBox.Show(this,
                "온톡 창의 본문이 이 초안과 맞춘 적 없는 내용입니다. 온톡에서 직접 쓰거나 고친 글로 보입니다.\n\n" +
                "예: 온톡 창의 글을 편집기로 가져옵니다 (지금 편집기의 글은 사라집니다).\n" +
                "아니오: 편집기의 글로 온톡 창을 덮어씁니다.\n취소: 아무것도 하지 않습니다.",
                "내용이 다릅니다", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Yes)
            {
                Editor.Clear();
                Adopt(current, importMessage: true);
                Result("온톡 창의 글을 편집기로 가져왔습니다.", ok: true);
                return false;
            }
            else if (choice == MessageBoxResult.No)
            {
                return await ApplyAsync(final, current.Body, askOnConflict: false);
            }
            else
            {
                Result("반영하지 않았습니다.", ok: false);
            }
            return false;
        }

        var clipboard = result.ClipboardRestored ? "" : " 클립보드는 그 사이 바뀌어 되돌리지 않았습니다.";
        switch (result.Status)
        {
            case ApplyStatus.Applied:
                _synced = final;
                Result($"반영 확인: 온톡 창을 다시 읽어 본문이 일치했습니다 · {result.After!.UsedBytes}/{result.After.LimitBytes} Bytes · " +
                       $"수신 {(result.After.Recipients.Count > 0 ? string.Join(", ", result.After.Recipients) : "없음")}.{clipboard}", ok: true);
                return true;
            case ApplyStatus.Mismatch:
                Result("붙여넣은 뒤 다시 읽은 내용이 초안과 다릅니다. 다시 시도하지 않았습니다. 온톡 창을 직접 확인하세요." + clipboard, ok: false);
                break;
            default:
                Result(result.Status switch
                {
                    ApplyStatus.NotCompose => "온톡 작성 창을 찾지 못했습니다. 창이 닫혔을 수 있습니다.",
                    ApplyStatus.DialogOpen => "온톡 창에 예약 설정 화면이 떠 있습니다. 닫은 뒤 다시 반영하세요.",
                    ApplyStatus.Conflict => "온톡 창의 내용이 그 사이 다시 바뀌어 반영하지 않았습니다.",
                    ApplyStatus.TooLong => "온톡의 글자 수 제한을 넘습니다.",
                    ApplyStatus.Covered => "온톡 입력창이 다른 창에 가려져 있어 클릭하지 않았습니다.",
                    ApplyStatus.FocusFailed => "온톡 창이 앞으로 오지 않아 입력하지 않았습니다. 다시 시도하세요.",
                    ApplyStatus.ModifierDown => "Ctrl·Alt·Shift·Win 키가 눌려 있어 입력하지 않았습니다.",
                    ApplyStatus.ClipboardBusy => "다른 프로그램이 클립보드를 쓰고 있어 붙여넣지 못했습니다.",
                    _ => "반영하지 못했습니다.",
                } + clipboard, ok: false);
                break;
        }
        return false;
    }

    private void Result(string text, bool ok)
    {
        ResultText.Text = text;
        ResultText.Foreground = ok ? (Brush)Application.Current.Resources["Accent"] : Brushes.Firebrick;
    }

    private async void OnAiWrite(object sender, RoutedEventArgs e)
    {
        if (_source is null && Editor.Text.Trim().Length == 0 && AiInstruction.Text.Trim().Length == 0)
        {
            AiStatus.Text = "무엇을 쓸지 요청을 적어 주세요.";
            return;
        }
        AiButton.IsEnabled = false;
        AiStatus.Text = "로컬 AI가 쓰는 중입니다. 수십 초 걸릴 수 있습니다.";
        var before = Editor.Text;
        try
        {
            var text = await _writer.WriteAsync(_source?.Note, before, AiInstruction.Text, _closing.Token);
            if (Editor.Text != before)
            {
                // 기다리는 동안 사용자가 고친 글을 지우지 않는다.
                AiStatus.Text = "쓰는 동안 편집기 내용이 바뀌어 AI 초안을 넣지 않았습니다. 다시 눌러 주세요.";
                return;
            }
            Editor.Text = text;
            AiStatus.Text = "AI가 쓴 초안입니다. 사실과 맞는지 확인하고 고쳐 쓰세요. { } 표시는 AI가 몰라서 비워 둔 자리입니다.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (AnalysisUnavailableException ex)
        {
            AiStatus.Text = $"{ex.Message} 직접 쓰는 데는 지장이 없습니다.";
        }
        finally
        {
            AiButton.IsEnabled = true;
        }
    }

    private void ReloadTemplates() => TemplateBox.ItemsSource = _store.GetTemplates();

    private void OnInsertTemplate(object sender, RoutedEventArgs e)
    {
        if (TemplateBox.SelectedItem is Template template)
        {
            var at = Editor.SelectionStart;
            Editor.Text = Editor.Text.Insert(at, template.Body);
            Editor.SelectionStart = at + template.Body.Length;
            Editor.Focus();
        }
    }

    private void OnSaveTemplate(object sender, RoutedEventArgs e)
    {
        var name = TemplateBox.Text.Trim();
        if (name.Length == 0 || Editor.Text.Trim().Length == 0)
        {
            Result("문구 칸에 이름을 적고, 편집기에 저장할 글이 있어야 합니다.", ok: false);
            return;
        }
        _store.SaveTemplate(name, Editor.Text);
        ReloadTemplates();
        TemplateBox.Text = name;
        Result($"문구 '{name}'을(를) 저장했습니다. 바꿔 넣을 자리는 {{이름}}처럼 중괄호로 적어 두면 반영 전에 확인합니다.", ok: true);
    }

    private void OnDeleteTemplate(object sender, RoutedEventArgs e)
    {
        if (TemplateBox.SelectedItem is Template template &&
            MessageBox.Show(this, $"문구 '{template.Name}'을(를) 삭제할까요?", "문구 삭제", MessageBoxButton.OKCancel) ==
            MessageBoxResult.OK)
        {
            _store.DeleteTemplate(template.Id);
            ReloadTemplates();
        }
    }
}
