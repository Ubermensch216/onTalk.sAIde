namespace OnTalk.Sidecar.Core;

/// <param name="MatchedNames">이 그룹(또는 친구)의 구성원 이름과 일치한 수신자 이름.</param>
public sealed record GroupDispatch(DispatchRecord Record, IReadOnlyList<string> MatchedNames);

/// <summary>선택한 그룹·친구에게 사이드카로 보낸 쪽지 이력. 온톡 작성 창은 수신자를 이름으로만 보여 주므로 이름으로 맞춘다.</summary>
public sealed record GroupHistory(IReadOnlyList<GroupDispatch> Dispatches, int MemberCount, bool HasHomonyms, int ConfirmedCount)
{
    public DateTimeOffset? LastSentAt => Dispatches.Count == 0 ? null : Dispatches.Max(d => d.Record.CreatedAt);

    /// <summary>기간 안의 발송 기록. 전송 직전 재확인에서 막힌 기록(NotSent)은 보내지 않았으므로 제외한다.</summary>
    public static GroupHistory Build(IReadOnlyList<ContactPerson> people, IReadOnlyList<DispatchRecord> dispatches,
        DateTimeOffset now, int? days)
    {
        var distinct = people.DistinctBy(p => p.DisplayIdentity).ToList();
        var names = distinct.Select(p => p.Name).ToHashSet();
        var firstDay = days is { } count ? HomeDashboard.Day(now).AddDays(1 - count) : DateOnly.MinValue;
        var matched = dispatches
            .Where(d => d.Status != DispatchStatus.NotSent && HomeDashboard.Day(d.CreatedAt) >= firstDay && d.CreatedAt <= now)
            .Select(d => new GroupDispatch(d, d.RecipientNames.Where(names.Contains).Distinct().ToList()))
            .Where(d => d.MatchedNames.Count > 0)
            .OrderByDescending(d => d.Record.CreatedAt).ThenByDescending(d => d.Record.Id).ToList();
        return new(matched, distinct.Count, distinct.GroupBy(p => p.Name).Any(g => g.Count() > 1),
            matched.Count(d => d.Record.Status == DispatchStatus.WindowClosed));
    }
}

public enum RecentNoteDirection { Received, Sent }

/// <summary>Home의 최근 쪽지 한 줄. 받은 쪽지는 보관한 쪽지, 보낸 쪽지는 사이드카 발송 기록이다.</summary>
public sealed record RecentNote(RecentNoteDirection Direction, DateTimeOffset At, string Counterpart, string Body,
    HomeNote? Received = null, StoredNote? Candidate = null, GroupDispatch? Sent = null)
{
    /// <summary>받은 쪽지가 친구에게 연결된 것인지. false면 발신자 이름만 일치한 후보다.</summary>
    public bool Linked => Received is not null;

    /// <summary>받은 쪽지(연결·이름 일치 후보)와 보낸 쪽지를 최신순으로 합친다. 같은 쪽지는 한 번만 센다.</summary>
    public static IReadOnlyList<RecentNote> Build(HomeDashboard data, GroupHistory history)
    {
        var received = data.Notes.Select(n => new RecentNote(RecentNoteDirection.Received, n.Source.FirstSeenAt,
                n.Source.Note.SenderName, n.Source.Note.Body, Received: n))
            .Concat(data.Candidates.Where(c => data.Notes.All(n => n.Source.Id != c.Id)).Select(c => new RecentNote(
                RecentNoteDirection.Received, c.FirstSeenAt, c.Note.SenderName, c.Note.Body, Candidate: c)));
        var sent = history.Dispatches.Select(d => new RecentNote(RecentNoteDirection.Sent, d.Record.CreatedAt,
            string.Join(", ", d.MatchedNames), d.Record.Body, Sent: d));
        return received.Concat(sent).OrderByDescending(n => n.At).ToList();
    }
}
