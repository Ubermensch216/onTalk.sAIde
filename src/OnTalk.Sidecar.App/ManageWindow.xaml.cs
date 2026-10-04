using System.IO;
using System.Reflection;
using System.Windows;
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

    public ManageWindow(ProfileStore store, AppSettings settings, string? onTalkVersion, Action<string, string> restore, Action deleteAll)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        AnalysisModelBox.Text = settings.AnalysisModel is { Length: > 0 } analysis ? analysis : OllamaModels.DefaultAnalysis;
        EmbeddingModelBox.Text = settings.EmbeddingModel is { Length: > 0 } embedding ? embedding : OllamaModels.DefaultEmbedding;
        _restore = restore;
        _deleteAll = deleteAll;
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
