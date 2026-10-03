namespace OnTalk.Sidecar.Automation;

public static class OnTalkAccount
{
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
