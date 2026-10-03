using System.IO;
using System.Text.Json;

namespace OnTalk.Sidecar.App;

/// <summary>사용자가 연결을 확인한 온톡 계정 목록. 쪽지 내용은 여기에 저장하지 않는다.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnTalk.sAIde", "settings.json");

    public List<string> ConfirmedAccounts { get; set; } = [];

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
