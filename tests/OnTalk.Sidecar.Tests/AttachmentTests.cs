using System.IO.Compression;
using System.Text;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.Tests;

public class AttachmentTextTests
{
    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public void 글자_파일은_UTF8과_CP949를_모두_읽는다()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Assert.Equal((ExtractStatus.Extracted, "회의 안내"), AttachmentText.Extract("a.txt", Encoding.UTF8.GetBytes("회의 안내")));
        Assert.Equal((ExtractStatus.Extracted, "회의 안내"), AttachmentText.Extract("a.TXT", Encoding.GetEncoding(949).GetBytes("회의 안내")));
    }

    [Fact]
    public void 워드_문서의_문단을_읽는다()
    {
        var docx = Zip(("word/document.xml",
            """<w:document xmlns:w="x"><w:body><w:p><w:r><w:t>첫 문단</w:t></w:r></w:p><w:p><w:r><w:t>둘째 </w:t></w:r><w:r><w:t>문단</w:t></w:r></w:p></w:body></w:document>"""),
            ("word/styles.xml", "<w:styles xmlns:w=\"x\"><w:t>양식 글자</w:t></w:styles>"));

        var (status, text) = AttachmentText.Extract("보고.docx", docx);

        Assert.Equal(ExtractStatus.Extracted, status);
        Assert.Equal("첫 문단\n둘째 문단\n", text);
    }

    [Fact]
    public void 엑셀의_글자와_파워포인트의_슬라이드를_읽는다()
    {
        var xlsx = Zip(("xl/sharedStrings.xml", """<sst xmlns="x"><si><t>부서명</t></si><si><t>총무과</t></si></sst>"""));
        var pptx = Zip(
            ("ppt/slides/slide2.xml", """<p:sld xmlns:p="x" xmlns:a="y"><a:p><a:r><a:t>둘째 장</a:t></a:r></a:p></p:sld>"""),
            ("ppt/slides/slide1.xml", """<p:sld xmlns:p="x" xmlns:a="y"><a:p><a:r><a:t>첫 장</a:t></a:r></a:p></p:sld>"""));

        Assert.Equal("부서명\n총무과\n", AttachmentText.Extract("표.xlsx", xlsx).Text);
        Assert.Equal("첫 장\n둘째 장\n", AttachmentText.Extract("발표.pptx", pptx).Text);
    }

    [Theory]
    [InlineData("그림.jpg")]
    [InlineData("문서.hwp")]
    [InlineData("문서.pdf")]
    [InlineData("확장자없음")]
    public void 지원하지_않는_형식은_뽑지_않는다(string name)
    {
        Assert.Equal((ExtractStatus.Unsupported, ""), AttachmentText.Extract(name, [1, 2, 3]));
    }

    [Fact]
    public void 손상된_문서는_실패로_알리고_예외를_내지_않는다()
    {
        Assert.Equal(ExtractStatus.Failed, AttachmentText.Extract("깨짐.docx", [1, 2, 3, 4]).Status);
        Assert.Equal(ExtractStatus.Failed, AttachmentText.Extract("깨짐.docx", Zip(("word/document.xml", "<w:p>닫히지 않음"))).Status);
    }

    [Fact]
    public void 외부_참조를_가진_문서는_읽지_않는다()
    {
        var docx = Zip(("word/document.xml",
            """<!DOCTYPE d [<!ENTITY x SYSTEM "file:///C:/Windows/win.ini">]><d><p>&x;</p></d>"""));

        Assert.Equal(ExtractStatus.Failed, AttachmentText.Extract("위험.docx", docx).Status);
    }
}

public sealed class FileVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ontalk-saide-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset At = new(2026, 10, 3, 14, 0, 0, KoreanNoteTime.SeoulOffset);

    private static ReceivedNote Note(string body, params string[] attachments) => new(
        "홍길동", "주무관", "10월 2일 오전 09:00",
        new NoteTime(new DateTimeOffset(2026, 10, 2, 9, 0, 0, KoreanNoteTime.SeoulOffset), true), 1, body, attachments);

    private string Blob(string hash) => Path.Combine(_root, "user1", "files", hash + ".bin");

    [Fact]
    public void 보관한_파일은_그대로_되돌아오고_디스크에는_평문이_없다()
    {
        using var store = ProfileStore.Open(_root, "user1");
        var noteId = store.SaveReceived(Note("본문", "안내.txt", "그림.jpg"), At).NoteId;
        var attachments = store.GetAttachments(noteId);
        Assert.All(attachments, a => Assert.Null(a.Sha256));
        var content = Encoding.UTF8.GetBytes("PLAIN-MARKER-98765 워크숍 장소는 대강당");

        var stored = store.StoreAttachment(attachments[0].Id, content, At);

        Assert.Equal(content.Length, stored.Size);
        Assert.Equal(ExtractStatus.Extracted, stored.Extract);
        Assert.Equal(content, store.ReadAttachment(attachments[0].Id));
        var raw = File.ReadAllBytes(Blob(stored.Sha256!));
        Assert.True(raw.AsSpan().IndexOf("PLAIN-MARKER-98765"u8) < 0);
        Assert.Null(store.GetAttachments(noteId)[1].Sha256);
    }

    [Fact]
    public void 첨부_내용으로도_쪽지를_찾는다()
    {
        using var store = ProfileStore.Open(_root, "user1");
        var noteId = store.SaveReceived(Note("첨부 참고 바랍니다", "안내.txt"), At).NoteId;
        Assert.Empty(store.SearchNotes(new NoteQuery(["대강당"])));

        store.StoreAttachment(store.GetAttachments(noteId)[0].Id, Encoding.UTF8.GetBytes("워크숍 장소는 대강당"), At);

        Assert.Equal(noteId, Assert.Single(store.SearchNotes(new NoteQuery(["대강당"]))).Id);
    }

    [Fact]
    public void 보관_파일이_바뀌면_내용을_내주지_않는다()
    {
        using var store = ProfileStore.Open(_root, "user1");
        var id = store.GetAttachments(store.SaveReceived(Note("본문", "a.bin"), At).NoteId)[0].Id;
        var stored = store.StoreAttachment(id, [1, 2, 3, 4, 5, 6, 7, 8], At);
        var path = Blob(stored.Sha256!);
        var raw = File.ReadAllBytes(path);
        raw[^1] ^= 0xFF;
        File.WriteAllBytes(path, raw);

        Assert.Throws<InvalidDataException>(() => store.ReadAttachment(id));

        File.Delete(path);
        Assert.Throws<InvalidDataException>(() => store.ReadAttachment(id));
    }

    [Fact]
    public void 같은_내용은_한_번만_저장하고_쪽지별_연결은_따로_둔다()
    {
        using var store = ProfileStore.Open(_root, "user1");
        var a = store.GetAttachments(store.SaveReceived(Note("가", "양식.bin"), At).NoteId)[0].Id;
        var b = store.GetAttachments(store.SaveReceived(Note("나", "같은양식.bin"), At).NoteId)[0].Id;
        byte[] same = [9, 9, 9];

        var first = store.StoreAttachment(a, same, At);
        var second = store.StoreAttachment(b, same, At);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "user1", "files")));

        // 한쪽을 다른 내용으로 바꿔도 다른 쪽이 쓰는 파일은 남는다.
        store.StoreAttachment(a, [1, 1, 1], At);
        Assert.Equal(same, store.ReadAttachment(b));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "user1", "files")).Length);

        // 아무도 쓰지 않게 된 파일은 지운다.
        store.StoreAttachment(b, [1, 1, 1], At);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "user1", "files")));
    }

    [Fact]
    public void 내용을_뽑지_못해도_보관은_된다()
    {
        using var store = ProfileStore.Open(_root, "user1");
        var id = store.GetAttachments(store.SaveReceived(Note("본문", "깨짐.docx"), At).NoteId)[0].Id;

        var stored = store.StoreAttachment(id, [1, 2, 3], At);

        Assert.Equal(ExtractStatus.Failed, stored.Extract);
        Assert.Equal([1, 2, 3], store.ReadAttachment(id));
    }

    [Fact]
    public void 다른_계정의_키로는_파일을_열_수_없다()
    {
        using var mine = ProfileStore.Open(_root, "user1");
        using var other = ProfileStore.Open(_root, "user2");
        var id = mine.GetAttachments(mine.SaveReceived(Note("본문", "a.bin"), At).NoteId)[0].Id;
        var stored = mine.StoreAttachment(id, [5, 5, 5], At);
        var otherId = other.GetAttachments(other.SaveReceived(Note("본문", "a.bin"), At).NoteId)[0].Id;
        other.StoreAttachment(otherId, [7], At);

        // 다른 계정 폴더로 암호화 파일을 옮겨 놓아도 풀리지 않는다.
        var otherBlob = Directory.GetFiles(Path.Combine(_root, "user2", "files")).Single();
        File.Copy(Blob(stored.Sha256!), otherBlob, overwrite: true);

        Assert.Throws<InvalidDataException>(() => other.ReadAttachment(otherId));
    }

    [Fact]
    public void 그룹은_구성원의_소속까지_저장하고_같은_이름은_바꿔_쓴다()
    {
        using var store = ProfileStore.Open(_root, "user1");
        var kim = new Recipient("김철수", "주무관", ["A시", "A시 / 총무과"]);
        var sameName = new Recipient("김철수", "소방장", ["A시", "A시 / 소방서"]);

        store.SaveGroup("총무팀", [kim]);
        store.SaveGroup("가나다", [kim, sameName]);
        store.SaveGroup("총무팀", [sameName]);

        var groups = store.GetGroups();
        Assert.Equal(["가나다", "총무팀"], groups.Select(g => g.Name));
        Assert.Equal(["주무관", "소방장"], groups[0].Members.Select(m => m.Title));
        Assert.Equal(["A시", "A시 / 소방서"], Assert.Single(groups[1].Members).Details);

        store.DeleteGroup(groups[0].Id);
        Assert.Equal("총무팀", Assert.Single(store.GetGroups()).Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
