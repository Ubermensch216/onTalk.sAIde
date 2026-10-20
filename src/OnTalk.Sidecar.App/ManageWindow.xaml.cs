using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.App;

/// <summary>발송 기록 한 줄.</summary>
public sealed class DispatchRow(DispatchRecord record)
{
    public string Headline { get; } =
        $"{record.CreatedAt.ToLocalTime():MM-dd HH:mm} · " +
        (record.ReserveAt is { } at ? $"{at:MM-dd HH:mm} 예약" : "즉시") +
        $" · {string.Join(", ", record.RecipientNames)} · " + record.Status switch
        {
            DispatchStatus.WindowClosed => "전송 단추를 눌렀고 온톡 창이 닫힘",
            DispatchStatus.StillOpen => "눌렀지만 온톡 창이 닫히지 않음 (결과 모름)",
            DispatchStatus.NotSent => $"누르지 않음: {record.Detail}",
            _ => "누르기 직전 기록만 있음 (결과 모름)",
        };

    public string Preview { get; } = record.Body.ReplaceLineEndings(" ");
}

/// <summary>백업·되살리기·삭제, 로컬 AI 모델 선택과 발송 기록. 계정이 바뀌면 패널이 이 창을 닫는다.</summary>
public partial class ManageWindow : Window
{
    private readonly ProfileStore _store;
    private readonly AppSettings _settings;
    private readonly Action<string, string> _restore;
    private readonly Action _deleteAll;
    private readonly HotkeyRegistration _hotkey;

    public ManageWindow(ProfileStore store, AppSettings settings, string? onTalkVersion, Action<string, string> restore, Action deleteAll,
        HotkeyRegistration hotkey)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        AnalysisModelBox.Text = settings.AnalysisModel is { Length: > 0 } analysis ? analysis : OllamaModels.DefaultAnalysis;
        EmbeddingModelBox.Text = settings.EmbeddingModel is { Length: > 0 } embedding ? embedding : OllamaModels.DefaultEmbedding;
        _restore = restore;
        _deleteAll = deleteAll;
        _hotkey = hotkey;
        HotkeyBox.PreviewKeyDown += OnHotkeyKeyDown;
        HotkeyBox.PreviewKeyUp += OnHotkeyKeyUp;
        HotkeyBox.GotKeyboardFocus += (_, _) => BeginHotkeyInput();
        HotkeyBox.LostKeyboardFocus += (_, _) => EndHotkeyInput();
        Closed += (_, _) => { if (_capturing) EndHotkeyInput(); };
        ShowCurrentHotkey();
        var range = store.GetCollectionRange();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        InfoText.Text =
            $"계정 {store.LoginId} · 쪽지 {range.Count}건" +
            (range.Since is { } since ? $" ({since.ToLocalTime():yyyy-MM-dd}부터)" : "") + "\n" +
            $"위치: {Path.Combine(ProfileStore.DefaultRoot, store.LoginId)}\n" +
            $"onTalk.sAIde {version?.ToString(3)} · 온톡 {onTalkVersion ?? "버전 확인 못함"}";
        var records = store.GetDispatches().Select(r => new DispatchRow(r)).ToList();
        DispatchList.ItemsSource = records;
        NoDispatchText.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _capturing;

    private void ShowCurrentHotkey() =>
        HotkeyBox.Text = _hotkey.Active ? _hotkey.Current.Format() : _hotkey.Current.Format() + " (사용 중이 아님)";

    private void HotkeyMessage(string text, bool ok)
    {
        HotkeyStatus.Text = text;
        HotkeyStatus.Foreground = ok ? UiBrushes.AccentText : UiBrushes.Danger;
    }

    /// <summary>입력칸에 들어오면 지금 쓰는 단축키도 입력칸으로 들어오도록 전역 등록을 풀고 안내를 보인다.</summary>
    private void BeginHotkeyInput()
    {
        if (_capturing) return;
        _capturing = true;
        _hotkey.Suspend();
        HotkeyBox.Text = "키 조합을 누르세요…";
        HotkeyMessage("", ok: true);
    }

    private void EndHotkeyInput()
    {
        if (!_capturing) return;
        _capturing = false;
        _hotkey.Resume();
        ShowCurrentHotkey();
    }

    private static uint HeldModifiers()
    {
        var held = Keyboard.Modifiers;
        return (held.HasFlag(ModifierKeys.Control) ? Hotkey.Control : 0) | (held.HasFlag(ModifierKeys.Alt) ? Hotkey.Alt : 0) |
            (held.HasFlag(ModifierKeys.Shift) ? Hotkey.Shift : 0) | (held.HasFlag(ModifierKeys.Windows) ? Hotkey.Win : 0);
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>눌린 수정키를 입력칸에 그대로 보인다. 아직 마지막 키가 아니므로 뒤에 …을 붙인다.</summary>
    private void ShowHeld()
    {
        var held = Hotkey.FormatModifiers(HeldModifiers());
        HotkeyBox.Text = held.Length == 0 ? "키 조합을 누르세요…" : held + "+…";
    }

    private void LeaveHotkeyBox()
    {
        Keyboard.ClearFocus();
        Focus();
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        // Alt 조합은 SystemKey로, IME가 켜져 있으면 ImeProcessedKey로 들어온다.
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        var modifiers = HeldModifiers();
        if (key == Key.Tab && (modifiers & (Hotkey.Control | Hotkey.Alt | Hotkey.Win)) == 0) return; // 칸 이동은 그대로 둔다.
        e.Handled = true;
        if (key == Key.Escape && modifiers == 0)
        {
            HotkeyMessage("바꾸지 않았습니다.", ok: true);
            LeaveHotkeyBox();
            return;
        }
        if (IsModifierKey(key)) { ShowHeld(); return; }
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var gesture = new Hotkey(modifiers, virtualKey);
        // 눌린 모양을 키가 들어오는 즉시 보인다.
        var held = Hotkey.FormatModifiers(modifiers);
        var name = Hotkey.KeyName(virtualKey) ?? "?";
        HotkeyBox.Text = held.Length > 0 ? held + "+" + name : name;
        if (gesture.Problem is { } problem)
        {
            // 쓸 수 없는 조합이면 눌린 모양과 이유를 보이고 다시 받는다.
            HotkeyMessage(problem, ok: false);
            return;
        }
        // 마지막 키까지 입력되었으므로 바로 적용한다. TryApply가 이미 전역 등록을 마치므로 Resume을 거치지 않는다.
        var error = _hotkey.TryApply(gesture);
        _capturing = false;
        ShowCurrentHotkey();
        HotkeyMessage(error ?? $"{gesture.Format()}(으)로 설정했습니다. 이제 이 키를 누를 때마다 패널이 접히고 펴집니다.", error is null);
        LeaveHotkeyBox();
    }

    private void OnHotkeyKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (IsModifierKey(key)) ShowHeld(); // 마지막 키 없이 수정키를 뗀 경우도 그대로 보인다.
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        var error = _hotkey.TryApply(Hotkey.Default);
        ShowCurrentHotkey();
        HotkeyMessage(error ?? $"기본값 {Hotkey.Default.Format()}(으)로 되돌렸습니다.", error is null);
    }

    private void Status(string text, bool ok)
    {
        BackupStatus.Text = text;
        BackupStatus.Foreground = ok ? UiBrushes.AccentText : UiBrushes.Danger;
    }

    private async void OnListModels(object sender, RoutedEventArgs e)
    {
        ListModelsButton.IsEnabled = false;
        try
        {
            var models = await OllamaModels.ListAsync(CancellationToken.None);
            // 목록을 바꾸면 적어 둔 값이 지워지므로 다시 넣는다.
            var (analysis, embedding) = (AnalysisModelBox.Text, EmbeddingModelBox.Text);
            AnalysisModelBox.ItemsSource = models;
            EmbeddingModelBox.ItemsSource = models;
            (AnalysisModelBox.Text, EmbeddingModelBox.Text) = (analysis, embedding);
            ModelStatus.Foreground = UiBrushes.Muted;
            ModelStatus.Text = models.Count == 0
                ? "Ollama에 설치된 모델이 없습니다."
                : $"설치된 모델 {models.Count}개를 목록에 넣었습니다.";
        }
        catch (AnalysisUnavailableException ex)
        {
            ModelStatus.Foreground = UiBrushes.Danger;
            ModelStatus.Text = ex.Message;
        }
        finally
        {
            ListModelsButton.IsEnabled = true;
        }
    }

    private void OnSaveModels(object sender, RoutedEventArgs e)
    {
        var (analysis, embedding) = (AnalysisModelBox.Text.Trim(), EmbeddingModelBox.Text.Trim());
        if (analysis.Length == 0 || embedding.Length == 0)
        {
            ModelStatus.Foreground = UiBrushes.Danger;
            ModelStatus.Text = "두 모델 이름을 모두 적어 주세요.";
            return;
        }
        _settings.AnalysisModel = analysis;
        _settings.EmbeddingModel = embedding;
        try
        {
            _settings.Save();
            ModelStatus.Foreground = UiBrushes.AccentText;
            ModelStatus.Text = "저장했습니다. 프로그램을 다시 켜면 이 모델을 씁니다.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ModelStatus.Foreground = UiBrushes.Danger;
            ModelStatus.Text = $"저장하지 못했습니다. {ex.Message}";
        }
    }

    private void OnBackup(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Password.Length < ProfileBackup.MinPasswordLength)
        {
            Status($"백업 암호는 {ProfileBackup.MinPasswordLength}자 이상이어야 합니다.", ok: false);
            return;
        }
        if (PasswordBox.Password != PasswordAgain.Password)
        {
            Status("두 번 적은 암호가 서로 다릅니다.", ok: false);
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "백업 파일 만들기",
            FileName = $"onTalk.sAIde-{_store.LoginId}-{DateTime.Now:yyyyMMdd}.zip",
            Filter = "백업 파일 (*.zip)|*.zip",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            ProfileBackup.Create(_store, dialog.FileName, PasswordBox.Password);
            Status($"백업 파일을 만들었습니다: {dialog.FileName}", ok: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status($"백업하지 못했습니다. {ex.Message}", ok: false);
        }
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Password.Length == 0)
        {
            Status("백업을 만들 때 정한 암호를 '백업 암호' 칸에 적어 주세요.", ok: false);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "되살릴 백업 파일 고르기", Filter = "백업 파일 (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        if (Dialogs.Show(this,
                "지금 보관 자료를 백업의 내용으로 바꿉니다. 지금 자료는 지우지 않고 옆 폴더로 옮겨 둡니다.\n\n되살릴까요?",
                "백업에서 되살리기", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK)
        {
            // 패널이 저장소를 닫으면서 이 창도 닫는다.
            _restore(dialog.FileName, PasswordBox.Password);
        }
    }

    private void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        var count = _store.GetCollectionRange().Count;
        if (Dialogs.Show(this,
                $"계정 '{_store.LoginId}'로 보관한 쪽지 {count}건과 업무·초안·파일을 모두 지웁니다. 되돌릴 수 없습니다.\n\n" +
                "필요하면 먼저 백업 파일을 만드세요. 정말 지울까요?",
                "모두 지우기", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            _deleteAll();
        }
    }
}
