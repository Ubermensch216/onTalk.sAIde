using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.Tests;

public sealed class ContextLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ontalk-saide-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;
    private static ContactPerson Person => new("screen:1", "예시 담당자", "주무관", ["총무과"]);
    private static ChatTaskSource Source => new("예시 대화", "발신자 미확인", "오전 10:00", null, "message:1", "자료를 확인해 주세요.", "자료를 확인", Now);
    private static ReceivedNote Note => new(Person.Name, "총무과", "오늘", null, 1, "자료 안내", []);

    [Fact]
    public void 대화_업무는_쪽지로_위장하지_않고_근거를_재시작_후_복원한다()
    {
        long id;
        using (var store = ProfileStore.Open(_root, "A"))
        {
            id = store.AddChatTask("자료 확인", null, Source, Now).Id;
            Assert.Equal(0, store.CountNotes()); Assert.Null(store.GetTasks().Single().NoteId);
            Assert.Throws<ArgumentException>(() => store.AddChatTask("업무", null, Source with { Evidence = "없는 문장" }, Now));
        }
        using var reopened = ProfileStore.Open(_root, "A"); Assert.Equal(Source, reopened.GetChatTaskSource(id));
        using var other = ProfileStore.Open(_root, "B"); Assert.Empty(other.GetTasks()); Assert.Null(other.GetChatTaskSource(id));
        reopened.DeleteTask(id); Assert.Null(reopened.GetChatTaskSource(id));
    }

    [Fact]
    public void 사람_자료는_직접_연결한_경우만_연결로_기록하고_동명이인의_소속을_구분한다()
    {
        using var store = ProfileStore.Open(_root, "A"); var noteId = store.SaveReceived(Note, Now).NoteId;
        Assert.Null(store.ContactNoteLinkedAt(Person, noteId));
        store.LinkContactNote(Person, noteId, Now);
        Assert.Equal(Now, store.ContactNoteLinkedAt(Person, noteId)); Assert.Equal(noteId, Assert.Single(store.GetContactLinkedNotes(Person)).Id);
        Assert.Null(store.ContactNoteLinkedAt(Person with { Organization = ["회계과"] }, noteId));
        Assert.Equal(Now, store.ContactNoteLinkedAt(Person with { Key = "newscreen:4" }, noteId));
        store.UnlinkContactNote(Person, noteId); Assert.Empty(store.GetContactLinkedNotes(Person));
    }

    [Fact]
    public void 버전7을_자동_백업한_후_기존_자료와_새_연결을_보존한다()
    {
        using (var store = ProfileStore.Open(_root, "A"))
        {
            store.SaveReceived(Note, Now); store.SaveContactGroup("준비 그룹", new("문의", [Person], "합성", Now), Now);
            var db = (Microsoft.Data.Sqlite.SqliteConnection)typeof(ProfileStore).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE home_chat_contact; DROP TABLE home_chat; DROP TABLE home_people; DROP TABLE task_chat_source; DROP TABLE contact_note_link; PRAGMA user_version=7;"; cmd.ExecuteNonQuery();
        }
        using var migrated = ProfileStore.Open(_root, "A"); Assert.True(File.Exists(Path.Combine(_root, "A", "notes.db.v7.bak")));
        Assert.Equal(1, migrated.CountNotes()); Assert.Single(migrated.GetContactGroups());
        var task = migrated.AddChatTask("새 업무", null, Source, Now); Assert.Equal(Source, migrated.GetChatTaskSource(task.Id));
    }

    [Fact]
    public void 암호화_백업은_대화_근거와_명시적_자료_연결도_복원한다()
    {
        var backup = Path.Combine(_root, "context.zip");
        using (var store = ProfileStore.Open(Path.Combine(_root, "first"), "A"))
        {
            var note = store.SaveReceived(Note, Now); store.LinkContactNote(Person, note.NoteId, Now);
            store.AddChatTask("새 업무", null, Source, Now); ProfileBackup.Create(store, backup, "synthetic-test-password");
        }
        ProfileBackup.Restore(Path.Combine(_root, "second"), "A", backup, "synthetic-test-password");
        using var restored = ProfileStore.Open(Path.Combine(_root, "second"), "A");
        Assert.Equal(Source, restored.GetChatTaskSource(restored.GetTasks().Single().Id)); Assert.Single(restored.GetContactLinkedNotes(Person));
        Assert.False(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(backup)).Contains(Source.Body, StringComparison.Ordinal));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
