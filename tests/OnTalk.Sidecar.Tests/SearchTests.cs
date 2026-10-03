using System.Diagnostics;
using System.Net;
using System.Text;
using OnTalk.Sidecar.AI;
using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;
using Xunit.Abstractions;

namespace OnTalk.Sidecar.Tests;

public sealed class NoteSearchTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ontalk-saide-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Seen = new(2026, 10, 3, 14, 0, 0, KoreanNoteTime.SeoulOffset);

    private static ReceivedNote Note(string sender, string body, int day, params string[] attachments) => new(
        sender, "주무관 │ 총무과", $"10월 {day}일 오전 09:00",
        new NoteTime(new DateTimeOffset(2026, 10, day, 9, 0, 0, KoreanNoteTime.SeoulOffset), true), 1, body, attachments);

    private ProfileStore Seeded()
    {
        var store = ProfileStore.Open(_root, "user1");
        store.SaveReceived(Note("김철수", "감사교육 이수 요청드립니다. 문서번호 총무-1234", 1, "교육안내.hwp"), Seen);
        store.SaveReceived(Note("이영", "주간 회의 자료 제출 바랍니다. Deadline 100% 준수", 2), Seen);
        store.SaveReceived(Note("박민수", "회의실 예약 안내", 3, "예약현황_10월.xlsx"), Seen);
        return store;
    }

    private static string[] Senders(IEnumerable<StoredNote> notes) => notes.Select(n => n.Note.SenderName).ToArray();

    [Theory]
    [InlineData("감사교육", "김철수")]
    [InlineData("이영", "이영")]
    [InlineData("회의", "박민수,이영")]
    [InlineData("회의 자료", "이영")]
    [InlineData("총무-1234", "김철수")]
    [InlineData("deadline", "이영")]
    [InlineData("xlsx", "박민수")]
    [InlineData("총무과", "박민수,이영,김철수")]
    [InlineData("없는말", "")]
    public void 낱말이_모두_들어_있는_쪽지를_최근_순으로_찾는다(string text, string expected)
    {
        using var store = Seeded();

        var found = store.SearchNotes(new NoteQuery(NoteQuery.SplitTerms(text)));

        Assert.Equal(expected, string.Join(",", Senders(found)));
    }

    [Theory]
    [InlineData("100%", "이영")]
    [InlineData("%", "이영")]
    [InlineData("예약현황_10", "박민수")]
    [InlineData("_", "박민수")]
    public void 검색어의_퍼센트와_밑줄은_글자_그대로_찾는다(string text, string expected)
    {
        using var store = Seeded();

        var found = store.SearchNotes(new NoteQuery([text]));

        Assert.Equal(expected, string.Join(",", Senders(found)));
    }

    [Fact]
    public void 기간과_첨부_조건으로_좁힌다()
    {
        using var store = Seeded();
        var since = new DateTimeOffset(2026, 10, 2, 0, 0, 0, KoreanNoteTime.SeoulOffset);

        Assert.Equal(["박민수", "이영"], Senders(store.SearchNotes(new NoteQuery([], since))));
        Assert.Equal(["박민수", "김철수"], Senders(store.SearchNotes(new NoteQuery([], WithAttachmentOnly: true))));
        Assert.Equal(["박민수"], Senders(store.SearchNotes(new NoteQuery([], since, true))));
    }

    [Fact]
    public void 찾은_쪽지는_원문과_첨부를_그대로_돌려준다()
    {
        using var store = Seeded();

        var found = Assert.Single(store.SearchNotes(new NoteQuery(["감사교육"])));

        Assert.Equal(Note("김철수", "감사교육 이수 요청드립니다. 문서번호 총무-1234", 1).Body, found.Note.Body);
        Assert.Equal(["교육안내.hwp"], found.Note.AttachmentNames);
        var byId = store.GetNote(found.Id);
        Assert.Equal(found.Note.Key, byId!.Note.Key);
        Assert.Equal(found.Note.AttachmentNames, byId.Note.AttachmentNames);
        Assert.Equal(Seen, byId.FirstSeenAt);
    }

    [Fact]
    public void 수집_범위는_보관한_건수와_처음_수집한_시각이다()
    {
        using var empty = ProfileStore.Open(_root, "user2");
        using var store = Seeded();
        store.SaveReceived(Note("최", "나중에 본 쪽지", 3), Seen.AddDays(2));

        Assert.Equal(new CollectionRange(0, null), empty.GetCollectionRange());
        Assert.Equal(new CollectionRange(4, Seen), store.GetCollectionRange());
    }

    [Fact]
    public void 임베딩이_없는_쪽지만_골라_주고_저장한_벡터를_그대로_돌려준다()
    {
        using var store = Seeded();
        var first = store.GetNotesWithoutEmbedding("m", 10);
        Assert.Equal(3, first.Count);

        store.SaveEmbedding(first[0].Id, "m", [0.6f, 0.8f]);

        Assert.Equal(2, store.GetNotesWithoutEmbedding("m", 10).Count);
        Assert.Equal(3, store.GetNotesWithoutEmbedding("other-model", 10).Count);
        var (id, vector) = Assert.Single(store.GetEmbeddings("m"));
        Assert.Equal(first[0].Id, id);
        Assert.Equal([0.6f, 0.8f], vector);
    }

    [Fact]
    public void 업무_제목에서도_찾는다()
    {
        using var store = Seeded();
        store.AddTask(null, "감사교육 이수", null, null, null, Seen);
        store.AddTask(null, "회의 자료 제출", null, null, null, Seen);

        Assert.Equal("감사교육 이수", Assert.Single(store.SearchTasks(["교육"])).Title);
        Assert.Empty(store.SearchTasks([]));
    }

    [Fact]
    public void 만_건에서_문자_검색이_1초_안에_끝난다()
    {
        using var store = ProfileStore.Open(_root, "perf");
        var filler = string.Concat(Enumerable.Repeat("업무 협조 요청드립니다. 관련 자료를 검토하여 회신 바랍니다. ", 12));
        for (var i = 0; i < 10_000; i++)
        {
            var marker = i == 7_777 ? " 찾을표식XYZ" : "";
            store.SaveReceived(new ReceivedNote($"발신자{i % 300}", "주무관 │ 부서", "10월 1일 오전 09:00",
                new NoteTime(Seen.AddMinutes(-i), true), 1, $"{i}번 쪽지. {filler}{marker}", []), Seen);
        }

        var sw = Stopwatch.StartNew();
        var rare = store.SearchNotes(new NoteQuery(["찾을표식XYZ"]));
        var rareMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var common = store.SearchNotes(new NoteQuery(["회신", "발신자12"]));
        var commonMs = sw.ElapsedMilliseconds;
        output.WriteLine($"10,000건: 드문 낱말 {rareMs}ms, 흔한 낱말 2개 {commonMs}ms");

        Assert.Single(rare);
        Assert.Equal(200, common.Count);
        Assert.True(rareMs < 1000 && commonMs < 1000, $"드문 낱말 {rareMs}ms, 흔한 낱말 {commonMs}ms");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public class SnippetTests
{
    [Fact]
    public void 검색어_주변을_한_줄로_잘라_낸다()
    {
        var body = new string('가', 100) + "\n찾는말 뒤따르는 글" + new string('나', 200);

        var snippet = Snippet.Make(body, ["찾는말"], radius: 10);

        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Contains("찾는말 뒤따르는 글", snippet);
        Assert.DoesNotContain('\n', snippet);
    }

    [Fact]
    public void 검색어가_본문에_없으면_앞부분을_쓴다()
    {
        Assert.Equal("짧은 본문", Snippet.Make("짧은\n본문", ["김철수"]));
    }

    [Fact]
    public void 이모지를_반으로_자르지_않는다()
    {
        var body = string.Concat(Enumerable.Repeat("😀", 100));

        var snippet = Snippet.Make(body, [], radius: 5).TrimEnd('…');

        Assert.Equal(snippet, new string(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(snippet))));
        Assert.DoesNotContain('�', snippet);
        Assert.Equal(0, snippet.Length % 2);
    }
}

public class SimilarityTests
{
    [Fact]
    public void 비슷한_순으로_고르고_기준_미만은_버린다()
    {
        var query = Similarity.Normalize([1f, 0f]);
        var items = new (long, float[])[]
        {
            (1, Similarity.Normalize([0f, 1f])),
            (2, Similarity.Normalize([1f, 0.1f])),
            (3, Similarity.Normalize([1f, 1f])),
        };

        var top = Similarity.Top(query, items, count: 5, minScore: 0.5f);

        Assert.Equal([2L, 3L], top.Select(t => t.Id));
        Assert.True(top[0].Score > top[1].Score);
    }

    [Fact]
    public void 차원이_다른_벡터는_비슷하지_않은_것으로_본다()
    {
        Assert.Equal(0, Similarity.Dot([1f, 0f], [1f, 0f, 0f]));
    }
}

public class OllamaEmbedderTests
{
    private sealed class Fake(string json) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task 벡터를_길이_1로_맞춰_돌려준다()
    {
        var fake = new Fake("""{"embeddings":[[3.0,4.0]]}""");
        using var embedder = new OllamaEmbedder("test", fake);

        var vector = await embedder.EmbedAsync("글", CancellationToken.None);

        Assert.Equal([0.6f, 0.8f], vector);
        Assert.Equal("127.0.0.1", fake.Uri!.Host);
    }

    [Theory]
    [InlineData("""{"embeddings":[]}""")]
    [InlineData("""{"error":"model not found"}""")]
    [InlineData("oops")]
    public async Task 벡터가_없으면_사용_불가로_알린다(string json)
    {
        using var embedder = new OllamaEmbedder("test", new Fake(json));

        await Assert.ThrowsAsync<AnalysisUnavailableException>(() => embedder.EmbedAsync("글", CancellationToken.None));
    }
}
