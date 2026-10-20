using System.IO;
using System.Text.Json;

namespace OnTalk.Sidecar.App;

/// <summary>사용자가 연결을 확인한 온톡 계정 목록. 쪽지 내용은 여기에 저장하지 않는다.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnTalk.sAIde", "settings.json");

    public List<string> ConfirmedAccounts { get; set; } = [];

    /// <summary>패널을 접어 두었는지. 다음 실행 때 같은 모양으로 시작한다.</summary>
    public bool Collapsed { get; set; }

    /// <summary>패널 접기/펴기 단축키(<c>Ctrl+Shift+Space</c> 꼴). 비어 있으면 기본 조합을 쓴다.</summary>
    public string HotkeyText { get; set; } = "";

    /// <summary>요약·할 일 후보·AI 초안에 쓰는 Ollama 모델. 비어 있으면 기본 모델을 쓴다.</summary>
    public string AnalysisModel { get; set; } = "";

    /// <summary>뜻 검색에 쓰는 Ollama 임베딩 모델. 비어 있으면 기본 모델을 쓴다.</summary>
    public string EmbeddingModel { get; set; } = "";

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            // 설정이 깨졌으면 계정 확인을 다시 받는다. 쪽지 저장소에는 영향이 없다.
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, FilePath, overwrite: true);
    }
}
