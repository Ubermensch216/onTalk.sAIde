namespace OnTalk.Sidecar.Automation;

public static class OnTalkAccount
{
    /// <summary>화면 해석과 조작을 실제로 확인한 온톡 버전.</summary>
    public const string VerifiedVersion = "5.4.9";
    private static readonly string[] VerifiedVersions = ["5.4.3", VerifiedVersion];

    /// <summary>파일 버전은 "5.4.3.0"처럼 자리가 하나 더 붙어 나온다.</summary>
    public static bool IsVerified(string version) =>
        VerifiedVersions.Any(verified =>
            version == verified || version.StartsWith(verified + ".", StringComparison.Ordinal));

    /// <summary>실행 중인 온톡의 버전. 읽지 못하면 null.</summary>
    public static string? RunningVersion()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("온톡"))
        {
            try
            {
                if (process.MainModule?.FileVersionInfo.ProductVersion is { Length: > 0 } version)
                {
                    return version;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // 권한이 다르거나 그 사이 종료된 프로세스. 다음 프로세스를 본다.
            }
        }
        return null;
    }

    /// <summary>
    /// 지금 로그인한 계정의 후보. 온톡은 계정별 폴더(%APPDATA%\OnTalk\storage\로그인ID)에 저장하므로
    /// 가장 최근에 갱신된 폴더를 후보로 본다. 폴더 이름과 수정 시각만 보고 내용은 열지 않는다.
    /// 추정이므로 사용자가 확인하기 전에는 계정으로 확정하지 않는다.
    /// </summary>
    public static string? DetectCandidate()
    {
        var storage = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OnTalk", "storage");
        if (!Directory.Exists(storage))
        {
            return null;
        }
        return Directory.EnumerateDirectories(storage)
            .Select(dir => new FileInfo(Path.Combine(dir, "user.db")))
            .Where(db => db.Exists)
            .OrderByDescending(db => db.LastWriteTimeUtc)
            .Select(db => db.Directory!.Name)
            .FirstOrDefault();
    }
}
