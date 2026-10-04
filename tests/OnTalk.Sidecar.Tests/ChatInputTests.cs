using OnTalk.Sidecar.Automation;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Tests;

public sealed class ChatInputTests
{
    [Theory]
    [InlineData(0x100000, "text", 100, 30, true)]
    [InlineData(0, "text", 100, 30, false)]
    [InlineData(0x108000, "text", 100, 30, false)]
    [InlineData(0x100001, "text", 100, 30, false)]
    [InlineData(0x300000, "text", 100, 30, false)]
    [InlineData(0x100000, "statictext", 100, 30, false)]
    [InlineData(0x100000, "text", 0, 30, false)]
    [InlineData(0x100000, "text", 100, 0, false)]
    public void 표시_본문과_숨긴_입력과_암호_입력은_UIA_조회_대상에서_제외한다(int state, string role, int width, int height, bool expected)
    {
        var node = new AccNode(role, "", "", state, []) { Bounds = new(10,20,width,height) };
        Assert.Equal(expected, NativeChatInputBackend.IsInputCandidate(node));
    }

    private static ChatSnapshot Chat(string key = "A", string room = "예시 상대") => new(room,
        [new(key, "발신자 미확인", "오전 10:00", new DateOnly(2026, 10, 4), "합성 메시지")], DateTimeOffset.Now);
    private static ChatInputState State(string body = "기존 글") => new(11, new IntPtr(22), "chat-page", "input-field", Chat(), body);
    private sealed class Backend : IChatInputBackend
    {
        public ChatInputState? Current = State();
        public int Writes;
        public bool Supported = true, Echo = true;
        public Action? BeforeRead, AfterReplace;
        public ChatInputState? Read() { BeforeRead?.Invoke(); return Current; }
        public bool Replace(ChatInputState expected, string body)
        {
            Writes++;
            if (!Supported) return false;
            if (Echo) Current = Current! with { Body = body };
            AfterReplace?.Invoke(); return true;
        }
    }
    [Fact]
    public void 줄바꿈과_이모지를_보존하고_값_재읽기로_확인한다()
    {
        var backend = new Backend(); var writer = new ChatInputWriter(backend, Chat(), () => true);
        var result = writer.Apply(writer.Read()!, "첫째 줄\r\n둘째 줄 ✅");
        Assert.Equal(ChatInputStatus.Applied, result.Status);
        Assert.Equal("첫째 줄\n둘째 줄 ✅", result.After!.Body); Assert.Equal(1, backend.Writes);
    }
    [Fact]
    public void 비교_후_온톡_글이_바뀌면_덮어쓰지_않는다()
    {
        var backend = new Backend(); var writer = new ChatInputWriter(backend, Chat(), () => true);
        var ticket = writer.Read()!; backend.Current = ticket with { Body = "온톡에서 직접 편집" };
        Assert.Equal(ChatInputStatus.Conflict, writer.Apply(ticket, "초안").Status); Assert.Equal(0, backend.Writes);
    }
    [Theory]
    [InlineData("pid")]
    [InlineData("render")]
    [InlineData("page")]
    [InlineData("field")]
    public void 같은_표시_상대라도_창이나_입력_요소가_바뀌면_중단한다(string change)
    {
        var backend = new Backend(); var writer = new ChatInputWriter(backend, Chat(), () => true); var ticket = writer.Read()!;
        backend.Current = change switch { "pid" => ticket with { ProcessId = 33 }, "render" => ticket with { Render = new IntPtr(44) },
            "page" => ticket with { Page = "another-page" }, _ => ticket with { Field = "other-field" } };
        Assert.Equal(ChatInputStatus.ContextChanged, writer.Apply(ticket, "초안").Status); Assert.Equal(0, backend.Writes);
    }
    [Fact]
    public void 계정이나_활성_세션이_바뀌면_값도_읽지_않는다()
    {
        var allowed = true; var reads = 0; var backend = new Backend { BeforeRead = () => reads++ };
        var writer = new ChatInputWriter(backend, Chat(), () => allowed); var ticket = writer.Read()!;
        allowed = false;
        Assert.Equal(ChatInputStatus.Unavailable, writer.Apply(ticket, "초안").Status);
        Assert.Equal(1, reads); Assert.Equal(0, backend.Writes);
    }
    [Fact]
    public void 읽는_중_계정이_바뀌면_반영하지_않는다()
    {
        var allowed = true; var backend = new Backend(); var writer = new ChatInputWriter(backend, Chat(), () => allowed); var ticket = writer.Read()!;
        backend.BeforeRead = () => allowed = false;
        Assert.Equal(ChatInputStatus.Unavailable, writer.Apply(ticket, "초안").Status); Assert.Equal(0, backend.Writes);
    }
    [Fact]
    public void 창_종료로_문맥_검사가_취소되면_쓰기_전에_중단한다()
    {
        var backend = new Backend(); var writer = new ChatInputWriter(backend, Chat(), () => throw new OperationCanceledException());
        Assert.Null(writer.Read()); Assert.Equal(ChatInputStatus.Unavailable,writer.Apply(State(),"초안").Status); Assert.Equal(0,backend.Writes);
    }
    [Theory]
    [InlineData("different")]
    [InlineData("truncated")]
    [InlineData("unknown")]
    [InlineData("empty")]
    public void 자료가_불충분하거나_다른_방이면_입력을_제공하지_않는다(string change)
    {
        var backend = new Backend(); var writer = new ChatInputWriter(backend, Chat(), () => true); var ticket = writer.Read()!;
        backend.Current = ticket with { Chat = change switch { "different" => Chat("B"), "truncated" => Chat() with { Truncated = true },
            "unknown" => Chat(room: "대화 상대 미확인"), _ => Chat() with { Messages = [] } } };
        Assert.Null(writer.Read()); Assert.Equal(ChatInputStatus.Unavailable, writer.Apply(ticket, "초안").Status); Assert.Equal(0, backend.Writes);
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("탭\t본문")]
    [InlineData("본문\0")]
    public void 공백과_제어문자는_쓰기_전에_차단한다(string body)
    {
        var backend = new Backend(); var writer = new ChatInputWriter(backend, Chat(), () => true);
        Assert.Equal(ChatInputStatus.InvalidText, writer.Apply(State(), body).Status); Assert.Equal(0, backend.Writes);
    }
    [Fact]
    public void 오백자까지_반영하고_그_이상은_차단한다()
    {
        var backend = new Backend(); var writer = new ChatInputWriter(backend, Chat(), () => true);
        Assert.Equal(ChatInputStatus.Applied, writer.Apply(State(), new string('가', 500)).Status);
        Assert.Equal(ChatInputStatus.InvalidText, writer.Apply(backend.Current!, new string('가', 501)).Status); Assert.Equal(1, backend.Writes);
    }
    [Fact]
    public void 새_메시지가_추가된_연속_범위에서는_비교를_유지한다()
    {
        var backend = new Backend(); var writer = new ChatInputWriter(backend, Chat(), () => true); var ticket = writer.Read()!;
        backend.Current = ticket with { Chat = ticket.Chat with { Messages = [.. ticket.Chat.Messages, .. Chat("B").Messages] } };
        Assert.Equal(ChatInputStatus.Applied, writer.Apply(ticket, "초안").Status);
    }
    [Fact]
    public void 이미_같은_값이면_다시_쓰지_않는다()
    {
        var backend = new Backend(); var writer = new ChatInputWriter(backend, Chat(), () => true);
        Assert.Equal(ChatInputStatus.Applied, writer.Apply(State(), "기존 글").Status); Assert.Equal(0, backend.Writes);
    }
    [Fact]
    public void 지원되지_않는_쓰기는_다른_입력_방식으로_재시도하지_않는다()
    {
        var backend = new Backend { Supported = false }; var writer = new ChatInputWriter(backend, Chat(), () => true);
        Assert.Equal(ChatInputStatus.NotSupported, writer.Apply(State(), "초안").Status); Assert.Equal(1, backend.Writes);
    }
    [Fact]
    public void 쓰기_응답_후_값이_다르면_성공으로_표시하지_않는다()
    {
        var backend = new Backend { Echo = false }; var writer = new ChatInputWriter(backend, Chat(), () => true);
        Assert.Equal(ChatInputStatus.Mismatch, writer.Apply(State(), "초안").Status); Assert.Equal(1, backend.Writes);
    }
    [Fact]
    public void 쓰기_응답_후_방이_바뀌면_성공으로_표시하지_않는다()
    {
        var backend = new Backend(); backend.AfterReplace = () => backend.Current = backend.Current! with { Chat = Chat("B") };
        var writer = new ChatInputWriter(backend, Chat(), () => true);
        Assert.Equal(ChatInputStatus.ContextChanged, writer.Apply(State(), "초안").Status); Assert.Equal(1, backend.Writes);
    }
}
