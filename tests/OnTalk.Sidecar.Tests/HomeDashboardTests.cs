using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.Tests;

public sealed class HomeDashboardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ontalk-saide-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, KoreanNoteTime.SeoulOffset);
    private static readonly ContactPerson A = new("a", "김민경", "주무관", ["총무과"]);
    private static readonly ContactPerson B = new("b", "김민경", "주무관", ["회계과"]);
    private static readonly ChatMessage Message = new("m1", "김민경", "오전 10:00", new(2026, 10, 6), "점검 자료를 공유해 주세요.");
    private static ChatSnapshot Chat => new("점검 협의", [Message], Now.AddHours(-5));
    private static ChatAnalysis Analysis => new("점검 자료 공유를 요청했습니다.", [new("action", "자료 공유", Message, "점검 자료를 공유")], 0);
    private static ReceivedNote Note(string body = "점검 결과를 확인해 주세요.") => new(A.Name, "총무과", "오늘 오전 10:00", null, 1, body, []);

    [Fact]
    public void 이름만_같은_쪽지는_확인후보이며_동명이인의_통계로_집계하지_않는다()
    {
        using var store = ProfileStore.Open(_root, "a");
        var id = store.SaveReceived(Note(), Now.AddHours(-2)).NoteId;
        var before = store.GetHomeDashboard([A, B], Now, 7);
        Assert.Empty(before.Notes); Assert.Single(before.Candidates);
        store.LinkContactNote(A, id, Now);
        var after = store.GetHomeDashboard([A, B], Now, 7);
        Assert.Single(after.Notes); Assert.Empty(after.Candidates);
        Assert.Equal(1, after.Contacts.Single(c => c.Person == A).Notes);
        Assert.Equal(0, after.Contacts.Single(c => c.Person == B).Notes);
        var other = store.GetHomeDashboard([B], Now, 7);
        Assert.Empty(other.Notes); Assert.Single(other.Candidates);
    }

    [Fact]
    public void 그룹중복과_겹치는_대화범위는_원문과_쪽지를_중복집계하지_않는다()
    {
        using var store = ProfileStore.Open(_root, "a");
        var id = store.SaveReceived(Note(), Now).NoteId;
        store.LinkContactNote(A, id, Now); store.LinkContactNote(B, id, Now);
        var digest = store.SaveHomeChat(A, Chat, Analysis, Now);
        Assert.Equal(digest, store.SaveHomeChat(B, Chat, Analysis, Now));
        var second = Message with { Key = "m2", Body = "내일까지 공유하겠습니다." };
        store.SaveHomeChat(A, Chat with { Messages = [Message, second] }, Analysis, Now);
        var data = store.GetHomeDashboard([A, B, A with { Key = "a-other-group" }], Now, 7);
        Assert.Single(data.Notes); Assert.Equal(2, data.MessageCount); Assert.Equal(2, data.Contacts.Count);
        Assert.Equal(2, data.Activity.Last().Messages);
    }

    [Fact]
    public void 최근7일은_서울의_날짜경계를_사용하고_오래된_열린업무를_숨기지_않는다()
    {
        using var store = ProfileStore.Open(_root, "a");
        var first = new DateTimeOffset(2026, 9, 30, 0, 0, 0, KoreanNoteTime.SeoulOffset);
        var recent = store.SaveReceived(Note("최근 연락"), first.ToUniversalTime()).NoteId;
        var old = store.SaveReceived(Note("오래된 연락"), first.AddTicks(-1)).NoteId;
        var future = store.SaveReceived(Note("미래 자료"), Now.AddHours(1)).NoteId;
        foreach (var id in new[] { recent, old, future }) store.LinkContactNote(A, id, Now);
        var task = store.AddTask(old, "오래된 미완료 업무", "근거", null, new(2026, 10, 5), first);
        var unrelated = store.AddTask(null, "관련 없는 업무", null, null, null, Now);
        var data = store.GetHomeDashboard([A], Now, 7);
        Assert.Equal(recent, Assert.Single(data.Notes).Source.Id);
        Assert.Equal(task.Id, Assert.Single(data.OpenTasks).Id);
        Assert.DoesNotContain(data.OpenTasks, t => t.Id == unrelated.Id);
        store.SetTaskDone(task.Id, true, Now); Assert.Empty(store.GetHomeDashboard([A], Now, 7).OpenTasks);
        Assert.Equal(2, store.GetHomeDashboard([A], Now, null).Notes.Count);
    }

    [Fact]
    public void 대화업무는_친구에_연결한_같은_방과_원문만_포함한다()
    {
        using var store = ProfileStore.Open(_root, "a"); store.SaveHomeChat(A, Chat, Analysis, Now);
        var source = new ChatTaskSource(Chat.RoomHint, Message.Sender, Message.Time, Message.Date, Message.Key, Message.Body, "점검 자료", Now);
        var mine = store.AddChatTask("내 업무", null, source, Now);
        store.AddChatTask("다른 방 업무", null, source with { RoomHint = "다른 방" }, Now);
        Assert.Equal(mine.Id, Assert.Single(store.GetHomeDashboard([A], Now, 7).OpenTasks).Id);
        Assert.Empty(store.GetHomeDashboard([B], Now, 7).OpenTasks);
    }

    [Fact]
    public void 대화근거를_검증하고_재시작과_계정격리와_연결해제를_지원한다()
    {
        long id;
        using (var store = ProfileStore.Open(_root, "a"))
        {
            store.SaveHomePeople(new(MainMenu.Friends, [A, B], [], [], Now));
            id = store.SaveHomeChat(A, Chat, Analysis, Now);
            Assert.Throws<ArgumentException>(() => store.SaveHomeChat(A, Chat, Analysis with { Points = [new("action", "잘못된 근거", Message, "원문에 없는 말")] }, Now));
            Assert.Throws<ArgumentException>(() => store.SaveHomeChat(A, Chat with { Messages = [] }, Analysis, Now));
        }
        using var reopened = ProfileStore.Open(_root, "a");
        Assert.Equal(2, reopened.GetHomePeople()!.People.Count); Assert.Equal(Message, Assert.Single(reopened.GetHomeChats()).Source.Messages.Single());
        using var other = ProfileStore.Open(_root, "b"); Assert.Empty(other.GetHomeChats()); Assert.Null(other.GetHomePeople());
        reopened.UnlinkHomeChat(A, id); Assert.Empty(reopened.GetHomeChats());
    }

    [Fact]
    public void 버전8에서_백업하고_Home자료를_추가하며_암호화백업에서도_복원한다()
    {
        var backup = Path.Combine(_root, "backup.zip");
        using (var store = ProfileStore.Open(_root, "a"))
        {
            store.SaveReceived(Note(), Now);
            var db = (Microsoft.Data.Sqlite.SqliteConnection)typeof(ProfileStore).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE personalized_work; DROP TABLE home_chat_contact; DROP TABLE home_chat; DROP TABLE home_people; PRAGMA user_version=8;"; cmd.ExecuteNonQuery();
        }
        using (var migrated = ProfileStore.Open(_root, "a"))
        {
            Assert.True(File.Exists(Path.Combine(_root, "a", "notes.db.v8.bak"))); Assert.Equal(1, migrated.CountNotes());
            migrated.SaveHomeChat(A, Chat, Analysis, Now);
            migrated.SaveHomePeople(new(MainMenu.Friends, [A], [], [], Now));
            ProfileBackup.Create(migrated, backup, "home-test-password");
        }
        ProfileBackup.Restore(Path.Combine(_root, "restored"), "a", backup, "home-test-password");
        using var restored = ProfileStore.Open(Path.Combine(_root, "restored"), "a");
        Assert.Single(restored.GetHomePeople()!.People); Assert.Equal(1, restored.GetHomeDashboard([A], Now, 7).MessageCount);
    }

    [Fact]
    public void 그룹_발송이력은_구성원_이름이_포함된_기록만_기간내에서_최신순으로_보여준다()
    {
        using var store = ProfileStore.Open(_root, "a");
        var c = new ContactPerson("c", "박서준", "주무관", ["기획과"]);
        var old = store.BeginDispatch(["김민경"], "오래된 쪽지", null, Now.AddDays(-20));
        var first = store.BeginDispatch(["김민경", "박서준"], "첫 쪽지", null, Now.AddDays(-2));
        var second = store.BeginDispatch(["박서준", "외부인"], "둘째 쪽지", new DateTime(2026, 10, 7, 9, 0, 0), Now.AddHours(-1));
        var other = store.BeginDispatch(["외부인"], "무관한 쪽지", null, Now.AddHours(-1));
        var blocked = store.BeginDispatch(["김민경"], "막힌 쪽지", null, Now.AddHours(-3));
        store.FinishDispatch(first, DispatchStatus.WindowClosed, ""); store.FinishDispatch(blocked, DispatchStatus.NotSent, "");
        var week = GroupHistory.Build([A, c], store.GetDispatches(), Now, 7);
        Assert.Equal([second, first], week.Dispatches.Select(d => d.Record.Id));
        Assert.Equal(["박서준"], week.Dispatches[0].MatchedNames); Assert.Equal(2, week.MemberCount);
        Assert.Equal(1, week.ConfirmedCount); Assert.False(week.HasHomonyms);
        Assert.Equal(3, GroupHistory.Build([A, c], store.GetDispatches(), Now, null).Dispatches.Count);
        Assert.Equal([first], GroupHistory.Build([A], store.GetDispatches(), Now, 7).Dispatches.Select(d => d.Record.Id));
        Assert.True(GroupHistory.Build([A, B], store.GetDispatches(), Now, 7).HasHomonyms);
        Assert.Empty(GroupHistory.Build([], store.GetDispatches(), Now, null).Dispatches);
    }
    [Fact]
    public void 최근_쪽지는_받은_쪽지와_보낸_쪽지를_최신순으로_합친다()
    {
        using var store = ProfileStore.Open(_root, "a");
        var linked = store.SaveReceived(Note("연결한 쪽지"), Now.AddHours(-4)).NoteId;
        store.LinkContactNote(A, linked, Now);
        store.SaveReceived(Note("이름만 같은 쪽지"), Now.AddHours(-1));
        var sent = store.BeginDispatch([A.Name], "보낸 쪽지", null, Now.AddHours(-2));
        store.FinishDispatch(sent, DispatchStatus.WindowClosed, "");
        var feed = RecentNote.Build(store.GetHomeDashboard([A], Now, 7), GroupHistory.Build([A], store.GetDispatches(), Now, 7));
        Assert.Equal(["이름만 같은 쪽지", "보낸 쪽지", "연결한 쪽지"], feed.Select(n => n.Body));
        Assert.Equal([RecentNoteDirection.Received, RecentNoteDirection.Sent, RecentNoteDirection.Received], feed.Select(n => n.Direction));
        Assert.False(feed[0].Linked); Assert.True(feed[2].Linked); Assert.Equal(sent, feed[1].Sent!.Record.Id);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
