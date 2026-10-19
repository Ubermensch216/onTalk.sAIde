namespace OnTalk.Sidecar.Core;

/// <summary>쪽지에 붙일 파일의 사전 점검. 온톡 작성 창 안내(최대 20MB·10개·exe 불가)를 따른다.</summary>
public static class NoteAttachments
{
    public const int MaxCount = 10;
    public const long MaxFileBytes = 20L * 1024 * 1024;

    // 바로가기는 파일 선택창이 가리키는 원본으로 바뀌어 붙으므로 같은 파일로 확인할 수 없다.
    private static readonly string[] Blocked = [".exe", ".lnk"];

    /// <summary>붙일 수 없는 사유를 한 문장으로 돌려준다. 문제가 없으면 null.</summary>
    public static string? Validate(IReadOnlyList<string> paths)
    {
        if (paths.Count > MaxCount) return $"첨부 파일은 최대 {MaxCount}개까지 붙일 수 있습니다.";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (name.Length == 0 || !Path.IsPathFullyQualified(path)) return $"‘{path}’은(는) 파일 경로가 아닙니다.";
            if (Blocked.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                return $"‘{name}’은(는) 온톡에서 첨부할 수 없는 형식입니다.";
            if (!names.Add(name)) return $"이름이 같은 파일이 있습니다: {name}. 온톡에서 구분되지 않으므로 하나만 붙이세요.";
            var info = new FileInfo(path);
            if (!info.Exists) return $"‘{name}’ 파일을 찾을 수 없습니다.";
            if (info.Length == 0) return $"‘{name}’은(는) 빈 파일입니다.";
            if (info.Length > MaxFileBytes) return $"‘{name}’은(는) 20MB를 넘어 붙일 수 없습니다.";
        }
        return null;
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
