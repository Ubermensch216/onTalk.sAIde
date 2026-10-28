namespace OnTalk.Sidecar.Core;

public enum ExchangeKind { Received, Sent, Chat }

/// <summary>쪽지를 쓰는 상대와 이전에 오간 쪽지·대화 한 건. 받은 쪽지·보낸 쪽지(사이드카 발송 기록)·연결해 둔 대화 정리를 같은 줄에 담는다.</summary>
public sealed record RecentExchange(ExchangeKind Kind, DateTimeOffset At, string Counterpart, string Body)
{
    /// <summary>
    /// 받는 사람 이름과 맞는 기록을 최신순으로 <paramref name="limit"/>건 고른다.
    /// 온톡 작성 창은 받는 사람을 이름으로만 보여 주므로 쪽지는 이름으로 맞추고, 대화 정리는 이름이 같은 친구에게 연결된 것만 쓴다.
    /// </summary>
    public static IReadOnlyList<RecentExchange> Build(IReadOnlyList<string> names, IReadOnlyList<StoredNote> received,
        IReadOnlyList<DispatchRecord> dispatches, IReadOnlyList<HomeChatDigest> chats, IReadOnlyList<ContactPerson> people, int limit = 3)
    {
        var wanted = names.Where(n => n.Length > 0).ToHashSet();
        if (wanted.Count == 0) return [];
        var identities = people.Where(p => wanted.Contains(p.Name)).Select(p => p.DisplayIdentity).ToHashSet();
        var all = new List<RecentExchange>();
        all.AddRange(received.Where(n => wanted.Contains(n.Note.SenderName)).Select(n => new RecentExchange(
            ExchangeKind.Received, n.Note.SentAt?.Value ?? n.FirstSeenAt, n.Note.SenderName, n.Note.Body)));
        all.AddRange(dispatches.Where(d => d.Status != DispatchStatus.NotSent).Select(d => (Record: d,
                Matched: d.RecipientNames.Where(wanted.Contains).Distinct().ToList()))
            .Where(d => d.Matched.Count > 0)
            .Select(d => new RecentExchange(ExchangeKind.Sent, d.Record.CreatedAt, string.Join(", ", d.Matched), d.Record.Body)));
        all.AddRange(chats.Where(c => c.ContactIdentities.Any(identities.Contains)).Select(c => new RecentExchange(
            ExchangeKind.Chat, c.Source.ObservedAt, string.Join(", ", names.Where(wanted.Contains).Distinct()), c.Analysis.Summary)));
        return all.Where(e => e.Body.Trim().Length > 0).OrderByDescending(e => e.At).Take(limit).ToList();
    }
}
