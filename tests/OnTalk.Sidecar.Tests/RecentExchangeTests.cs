using OnTalk.Sidecar.Core;
using OnTalk.Sidecar.Storage;

namespace OnTalk.Sidecar.Tests;

public sealed class RecentExchangeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ontalk-saide-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, KoreanNoteTime.SeoulOffset);
    private static readonly ContactPerson Yoon = new("y", "윤소윤", "주무관", ["총무과"]);
    private static ReceivedNote From(string name, string body) => new(name, "총무과", "오늘 오전 10:00", null, 1, body, []);

    [Fact]
    public void 받은쪽지_보낸쪽지_대화정리를_최신순으로_세건만_고른다()
    {
        using var store = ProfileStore.Open(_root, "a");
        store.SaveReceived(From("윤소윤", "첫 받은 쪽지"), Now.AddDays(-4));
        store.SaveReceived(From("다른사람", "관계없는 쪽지"), Now.AddHours(-1));
        store.SaveReceived(From("윤소윤", "최근 받은 쪽지"), Now.AddHours(-3));
        var sent = store.BeginDispatch(["윤소윤", "김철수"], "보낸 쪽지", null, Now.AddDays(-1));
        store.FinishDispatch(sent, DispatchStatus.WindowClosed, "");
        var blocked = store.BeginDispatch(["윤소윤"], "보내지 않은 쪽지", null, Now.AddMinutes(-5));
        store.FinishDispatch(blocked, DispatchStatus.NotSent, "");
        var message = new ChatMessage("m1", "윤소윤", "오전 10:00", new(2026, 10, 6), "자료를 공유해 주세요.");
        store.SaveHomeChat(Yoon, new("점검 협의", [message], Now.AddDays(-2)),
            new("자료 공유를 요청했습니다.", [new("action", "자료 공유", message, "자료를 공유")], 0), Now);

        IReadOnlyList<string> names = ["윤소윤"];
        var recent = RecentExchange.Build(names, store.GetNotesFromSenders(names, 3), store.GetDispatches(), store.GetHomeChats(), [Yoon]);

        Assert.Equal(3, recent.Count);
        Assert.Equal(["최근 받은 쪽지", "보낸 쪽지", "자료 공유를 요청했습니다."], recent.Select(e => e.Body));
        Assert.Equal([ExchangeKind.Received, ExchangeKind.Sent, ExchangeKind.Chat], recent.Select(e => e.Kind));
        Assert.Equal("윤소윤", recent[1].Counterpart);
    }

    [Fact]
    public void 받는사람이_없거나_보관된_내역이_없으면_빈목록이다()
    {
        using var store = ProfileStore.Open(_root, "a");
        store.SaveReceived(From("윤소윤", "받은 쪽지"), Now);
        Assert.Empty(RecentExchange.Build([], store.GetNotesFromSenders(["윤소윤"], 3), [], [], []));
        Assert.Empty(store.GetNotesFromSenders([], 3));
        Assert.Empty(RecentExchange.Build(["김철수"], store.GetNotesFromSenders(["김철수"], 3), store.GetDispatches(), [], []));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
