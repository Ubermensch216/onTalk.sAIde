namespace OnTalk.Sidecar.Core;

/// <summary>사용자가 친구에게 연결한 선택 원문과 AI 정리. 방 고유 ID나 전체 이력이 아니다.</summary>
public sealed record HomeChatDigest(long Id, ChatSnapshot Source, ChatAnalysis Analysis,
    DateTimeOffset SavedAt, IReadOnlyList<string> ContactIdentities);
public sealed record HomeNote(StoredNote Source, NoteAnalysis? Analysis, IReadOnlyList<string> ContactIdentities);
public sealed record HomeActivity(DateOnly Day, int Notes, int Messages);
public sealed record HomeContactActivity(ContactPerson Person, int Notes, int Messages);
public sealed record HomeDashboard(IReadOnlyList<HomeNote> Notes, IReadOnlyList<StoredNote> Candidates,
    IReadOnlyList<HomeChatDigest> Chats, IReadOnlyList<TaskItem> OpenTasks,
    IReadOnlyList<HomeActivity> Activity, IReadOnlyList<HomeContactActivity> Contacts, int MessageCount)
{
    /// <summary>기간은 최초 쪽지 보관일·대화 원문 관찰일 기준. 열린 업무는 기간과 무관하다.</summary>
    public static HomeDashboard Build(IReadOnlyList<ContactPerson> people, IReadOnlyList<HomeNote> linkedNotes,
        IReadOnlyList<StoredNote> candidates, IReadOnlyList<HomeChatDigest> digests,
        IReadOnlyList<TaskItem> tasks, IReadOnlyDictionary<long, ChatTaskSource> chatTasks,
        DateTimeOffset now, int? days)
    {
        var identities = people.Select(p => p.DisplayIdentity).ToHashSet();
        var today = Day(now);
        var firstDay = days is { } count ? today.AddDays(1 - count) : DateOnly.MinValue;
        bool InRange(DateTimeOffset at) => Day(at) >= firstDay && at <= now;
        var allNotes = linkedNotes.Where(n => n.ContactIdentities.Any(identities.Contains)).DistinctBy(n => n.Source.Id).ToList();
        var allChats = digests.Where(c => c.ContactIdentities.Any(identities.Contains)).DistinctBy(c => c.Id).ToList();
        var notes = allNotes.Where(n => InRange(n.Source.FirstSeenAt)).OrderByDescending(n => n.Source.FirstSeenAt).ToList();
        var chats = allChats.Where(c => InRange(c.Source.ObservedAt)).OrderByDescending(c => c.Source.ObservedAt).ToList();
        var linkedIds = allNotes.Select(n => n.Source.Id).ToHashSet();
        // 방 힌트와 읽힌 메시지 키로 겹치는 선택 범위를 한 번 센다.
        var messages = chats.SelectMany(c => c.Source.Messages.Select(m => (Key: c.Source.RoomHint + "\n" + m.Key,
            At: c.Source.ObservedAt))).GroupBy(m => m.Key).Select(g => g.OrderBy(m => m.At).First()).ToList();
        var open = tasks.Where(t => !t.Done && (t.NoteId is { } id && linkedIds.Contains(id) ||
            t.NoteId is null && chatTasks.TryGetValue(t.Id, out var source) && allChats.Any(c =>
                c.Source.RoomHint == source.RoomHint && c.Source.Messages.Any(m => m.Key == source.MessageKey && m.Body == source.Body))))
            .OrderBy(t => t.Due is null).ThenBy(t => t.Due).ThenBy(t => t.Id).ToList();
        var activity = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).Select(day => new HomeActivity(day,
            notes.Count(n => Day(n.Source.FirstSeenAt) == day), messages.Count(m => Day(m.At) == day))).ToList();
        var contacts = people.DistinctBy(p => p.DisplayIdentity).Select(p => new HomeContactActivity(p,
            notes.Count(n => n.ContactIdentities.Contains(p.DisplayIdentity)),
            chats.Where(c => c.ContactIdentities.Contains(p.DisplayIdentity)).SelectMany(c =>
                c.Source.Messages.Select(m => c.Source.RoomHint + "\n" + m.Key)).Distinct().Count()))
            .OrderByDescending(c => c.Notes + c.Messages).ThenBy(c => c.Person.Name).ToList();
        var unlinked = candidates.Where(n => !linkedIds.Contains(n.Id) && InRange(n.FirstSeenAt) &&
            people.Any(p => p.Name == n.Note.SenderName)).DistinctBy(n => n.Id).OrderByDescending(n => n.FirstSeenAt).ToList();
        return new(notes, unlinked, chats, open, activity, contacts, messages.Count);
    }

    public static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(KoreanNoteTime.SeoulOffset).DateTime);
}
