using System.IO.Compression;
using System.Text;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ontalk-saide-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = new(2026, 10, 3, 14, 0, 0, KoreanNoteTime.SeoulOffset);
    private const string Password = "백업암호-1234";

    private string Root(string name) => Path.Combine(_base, name);
    private string Zip => Path.Combine(_base, "backup.zip");

    private static ReceivedNote Note(string body) => new("홍길동", "주무관", "10월 2일 오전 09:00",
        new NoteTime(new DateTimeOffset(2026, 10, 2, 9, 0, 0, KoreanNoteTime.SeoulOffset), true), 1, body, ["안내.txt"]);

    private void MakeBackup()
    {
        Directory.CreateDirectory(_base);
        using var store = ProfileStore.Open(Root("pc1"), "user1");
        var noteId = store.SaveReceived(Note("BACKUP-MARKER-555 본문"), At).NoteId;
        store.StoreAttachment(store.GetAttachments(noteId)[0].Id, Encoding.UTF8.GetBytes("첨부 내용 대강당"), At);
        store.AddTask(noteId, "업무", null, null, new DateOnly(2026, 10, 9), At);
        ProfileBackup.Create(store, Zip, Password);
    }

    [Fact]
    public void 백업을_다른_곳에서_되살리면_쪽지_업무_파일이_그대로다()
    {
        MakeBackup();

        var result = ProfileBackup.Restore(Root("pc2"), "user1", Zip, Password);

        Assert.Equal(1, result.Notes);
        Assert.Null(result.PreviousDataMovedTo);
        using var restored = ProfileStore.Open(Root("pc2"), "user1");
        var note = Assert.Single(restored.SearchNotes(new NoteQuery(["BACKUP-MARKER-555"])));
        Assert.Equal("업무", Assert.Single(restored.GetTasks()).Title);
        var attachment = Assert.Single(restored.GetAttachments(note.Id));
        Assert.Equal("첨부 내용 대강당", Encoding.UTF8.GetString(restored.ReadAttachment(attachment.Id)));
    }

    [Fact]
    public void 백업_파일에는_본문과_키가_평문으로_없다()
    {
        MakeBackup();

        var raw = File.ReadAllBytes(Zip);

        Assert.True(raw.AsSpan().IndexOf("BACKUP-MARKER-555"u8) < 0);
        Assert.True(raw.AsSpan().IndexOf("SQLite format 3"u8) < 0);
        Assert.True(raw.AsSpan().IndexOf("대강당"u8) < 0);
    }

    [Fact]
    public void 암호가_틀리면_되살리지_않고_기존_자료를_건드리지_않는다()
    {
        MakeBackup();
        using (var existing = ProfileStore.Open(Root("pc2"), "user1"))
        {
            existing.SaveReceived(Note("원래 있던 쪽지"), At);
        }

        var error = Assert.Throws<InvalidOperationException>(() => ProfileBackup.Restore(Root("pc2"), "user1", Zip, "틀린암호-0000"));

        Assert.Contains("암호", error.Message);
        Assert.False(Directory.Exists(Path.Combine(Root("pc2"), "user1.restoring")));
        using var still = ProfileStore.Open(Root("pc2"), "user1");
        Assert.Single(still.SearchNotes(new NoteQuery(["원래"])));
    }

    [Fact]
    public void 이미_있던_자료는_지우지_않고_옆으로_옮긴다()
    {
        MakeBackup();
        using (var existing = ProfileStore.Open(Root("pc2"), "user1"))
        {
            existing.SaveReceived(Note("원래 있던 쪽지"), At);
        }

        var result = ProfileBackup.Restore(Root("pc2"), "user1", Zip, Password);

        Assert.NotNull(result.PreviousDataMovedTo);
        Assert.True(File.Exists(Path.Combine(result.PreviousDataMovedTo, "notes.db")));
        using var restored = ProfileStore.Open(Root("pc2"), "user1");
        Assert.Empty(restored.SearchNotes(new NoteQuery(["원래"])));
    }

    [Fact]
    public void 다른_계정의_백업은_거부한다()
    {
        MakeBackup();

        Assert.Throws<InvalidOperationException>(() => ProfileBackup.Restore(Root("pc2"), "user2", Zip, Password));
    }

    [Fact]
    public void 짧은_암호로는_백업하지_않는다()
    {
        using var store = ProfileStore.Open(Root("pc1"), "user1");

        Assert.Throws<ArgumentException>(() => ProfileBackup.Create(store, Zip, "1234567"));
        Assert.False(File.Exists(Zip));
    }

    [Fact]
    public void 조작된_백업은_저장소_폴더_밖에_쓰지_않는다()
    {
        MakeBackup();
        using (var zip = ZipFile.Open(Zip, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../outside.txt").Open());
            writer.Write("x");
        }

        Assert.Throws<InvalidOperationException>(() => ProfileBackup.Restore(Root("pc2"), "user1", Zip, Password));

        Assert.False(File.Exists(Path.Combine(Root("pc2"), "outside.txt")));
        Assert.False(File.Exists(Path.Combine(_base, "outside.txt")));
        Assert.False(Directory.Exists(Path.Combine(Root("pc2"), "user1")));
    }

    [Fact]
    public void 백업이_아닌_파일은_거부한다()
    {
        Directory.CreateDirectory(_base);
        using (var zip = ZipFile.Open(Zip, ZipArchiveMode.Create))
        {
            zip.CreateEntry("something.txt");
        }

        Assert.Throws<InvalidOperationException>(() => ProfileBackup.Restore(Root("pc2"), "user1", Zip, Password));
    }

    [Fact]
    public void 계정_자료를_지우면_그_계정만_없어진다()
    {
        ProfileStore.Open(Root("pc1"), "user1").Dispose();
        ProfileStore.Open(Root("pc1"), "user2").Dispose();

        ProfileStore.DeleteProfile(Root("pc1"), "user1");
        ProfileStore.DeleteProfile(Root("pc1"), "..");

        Assert.False(Directory.Exists(Path.Combine(Root("pc1"), "user1")));
        Assert.True(Directory.Exists(Path.Combine(Root("pc1"), "user2")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }
}
