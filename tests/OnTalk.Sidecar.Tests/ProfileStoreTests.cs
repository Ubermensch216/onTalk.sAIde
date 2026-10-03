using System.Text;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ontalk-saide-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Seen = new(2026, 10, 3, 14, 0, 0, KoreanNoteTime.SeoulOffset);

    private static ReceivedNote Note(string body = "본문 표식 가나다") => new(
        "홍길동", "주무관 │ 총무과", "10월 2일 오후 07:51",
        new NoteTime(new DateTimeOffset(2026, 10, 2, 19, 51, 0, KoreanNoteTime.SeoulOffset), true),
        1, body, ["안내문.hwp"]);

    [Fact]
    public void 같은_쪽지를_다시_열면_한_건으로_유지한다()
    {
        using var store = ProfileStore.Open(_root, "user1");

        var first = store.SaveReceived(Note(), Seen);
        var second = store.SaveReceived(Note(), Seen.AddMinutes(10));

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Equal(first.NoteId, second.NoteId);
        Assert.Equal(Seen, second.FirstSeenAt);
        Assert.Equal(1, store.CountNotes());
    }

    [Fact]
    public void 본문이_다르면_다른_쪽지다()
    {
        using var store = ProfileStore.Open(_root, "user1");

        store.SaveReceived(Note("가"), Seen);
        store.SaveReceived(Note("나"), Seen);

        Assert.Equal(2, store.CountNotes());
    }

    [Fact]
    public void 다시_열어도_자료가_남는다()
    {
        using (var store = ProfileStore.Open(_root, "user1"))
        {
            store.SaveReceived(Note(), Seen);
        }

        using var reopened = ProfileStore.Open(_root, "user1");

        Assert.Equal(1, reopened.CountNotes());
    }

    [Fact]
    public void 계정마다_저장소가_분리된다()
    {
        using var a = ProfileStore.Open(_root, "user1");
        using var b = ProfileStore.Open(_root, "user2");

        a.SaveReceived(Note(), Seen);

        Assert.Equal(0, b.CountNotes());
    }

    [Fact]
    public void 저장_파일에_본문이_평문으로_남지_않는다()
    {
        using (var store = ProfileStore.Open(_root, "user1"))
        {
            store.SaveReceived(Note("ASCII-MARKER-12345 본문 표식"), Seen);
        }

        var raw = File.ReadAllBytes(Path.Combine(_root, "user1", "notes.db"));

        Assert.False(Contains(raw, Encoding.UTF8.GetBytes("ASCII-MARKER-12345")));
        Assert.False(Contains(raw, Encoding.ASCII.GetBytes("SQLite format 3")));
    }

    [Fact]
    public void 키_파일이_없어지면_새_키를_만들지_않고_실패한다()
    {
        ProfileStore.Open(_root, "user1").Dispose();
        File.Delete(Path.Combine(_root, "user1", "key.bin"));

        Assert.Throws<InvalidOperationException>(() => ProfileStore.Open(_root, "user1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a\\b")]
    public void 폴더를_벗어나는_계정_ID는_거부한다(string loginId)
    {
        Assert.Throws<ArgumentException>(() => ProfileStore.Open(_root, loginId));
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
