namespace OnTalk.Sidecar.Core;

/// <summary>온톡 ‘대화’ 메뉴에서 마지막으로 읽은 대화방 목록. 방 고유 ID가 없는 목록 미리보기일 뿐이다.</summary>
public sealed record ObservedConversations(IReadOnlyList<ConversationItem> Items, DateTimeOffset ObservedAt);

/// <summary>Home 친구 필터와 방 이름이 일치한 대화 목록 항목. 친구에 연결한 자료가 아니므로 요약하지 않는다.</summary>
public sealed record HomeConversation(ConversationItem Item, IReadOnlyList<ContactPerson> People, DateTimeOffset ObservedAt, bool SameName);

public static class HomeConversations
{
    private static string Squash(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// 방 이름에 친구 이름이 들어 있는 항목만 목록 순서(온톡의 최근 순서)대로 고른다. 이름 두 글자 미만은 맞추지 않는다.
    /// 기간은 대화 목록을 읽은 날(서울 기준)로 가른다. 열린 방의 메시지 시각은 목록에 없다.
    /// </summary>
    public static IReadOnlyList<HomeConversation> Match(IReadOnlyList<ContactPerson> people, ObservedConversations? seen,
        DateTimeOffset now, int? days)
    {
        if (seen is null || people.Count == 0) return [];
        var firstDay = days is { } count ? HomeDashboard.Day(now).AddDays(1 - count) : DateOnly.MinValue;
        if (HomeDashboard.Day(seen.ObservedAt) < firstDay || seen.ObservedAt > now) return [];
        var distinct = people.DistinctBy(p => p.DisplayIdentity).Where(p => Squash(p.Name).Length >= 2).ToList();
        var result = new List<HomeConversation>();
        foreach (var item in seen.Items.DistinctBy(i => (i.Name, i.Detail)))
        {
            var room = Squash(item.Name);
            var matched = distinct.Where(p => room.Contains(Squash(p.Name), StringComparison.Ordinal)).ToList();
            if (matched.Count == 0) continue;
            result.Add(new(item, matched, seen.ObservedAt, matched.Any(m => distinct.Count(p => p.Name == m.Name) > 1)));
        }
        return result;
    }
}
