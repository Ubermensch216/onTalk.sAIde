namespace OnTalk.Sidecar.Core;

/// <summary>저장소에 보관된 쪽지 사본.</summary>
public sealed record StoredNote(long Id, ReceivedNote Note, DateTimeOffset FirstSeenAt);

/// <param name="Terms">모두 들어 있어야 하는 낱말. 비어 있으면 조건에 맞는 쪽지를 최근 순으로 본다.</param>
/// <param name="Since">이 시각 이후에 보낸 쪽지만.</param>
public sealed record NoteQuery(IReadOnlyList<string> Terms, DateTimeOffset? Since = null, bool WithAttachmentOnly = false)
{
    public static IReadOnlyList<string> SplitTerms(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <param name="Count">보관한 쪽지 수.</param>
/// <param name="Since">처음 수집한 시각. 한 건도 없으면 null.</param>
public sealed record CollectionRange(int Count, DateTimeOffset? Since);

public static class Snippet
{
    /// <summary>본문에서 검색어가 처음 나오는 곳 주변을 한 줄로 잘라 낸다. 검색어가 본문에 없으면 앞부분을 쓴다.</summary>
    public static string Make(string body, IReadOnlyList<string> terms, int radius = 45)
    {
        var flat = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var hit = terms.Select(t => flat.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0)
            .DefaultIfEmpty(0).Min();
        var start = Math.Max(0, hit - radius);
        var end = Math.Min(flat.Length, hit + radius * 2);
        // 서로게이트 쌍(이모지)의 가운데를 자르지 않는다.
        if (start > 0 && char.IsLowSurrogate(flat[start]))
        {
            start--;
        }
        if (end < flat.Length && char.IsLowSurrogate(flat[end]))
        {
            end--;
        }
        return (start > 0 ? "…" : "") + flat[start..end] + (end < flat.Length ? "…" : "");
    }
}

public static class Similarity
{
    /// <summary>길이가 1인 두 벡터의 코사인 유사도.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }
        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    /// <summary>질의와 가장 비슷한 항목을 점수 높은 순으로 고른다. <paramref name="minScore"/> 미만은 버린다.</summary>
    public static List<(long Id, float Score)> Top(
        float[] query, IEnumerable<(long Id, float[] Vector)> items, int count, float minScore) =>
        items.Select(item => (item.Id, Score: Dot(query, item.Vector)))
            .Where(x => x.Score >= minScore)
            .OrderByDescending(x => x.Score)
            .Take(count)
            .ToList();

    public static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(Dot(v, v));
        return norm == 0 ? v : Array.ConvertAll(v, x => x / norm);
    }
}
