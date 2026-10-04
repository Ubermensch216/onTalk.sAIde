using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

public enum ChatInputStatus { Applied, Unavailable, ContextChanged, Conflict, InvalidText, NotSupported, Mismatch }
public sealed record ChatInputState(uint ProcessId, IntPtr Render, string Page, string Field, ChatSnapshot Chat, string Body);
public sealed record ChatInputResult(ChatInputStatus Status, ChatInputState? After = null);

/// <summary>사용자가 요청한 입력 읽기/교체만 제공한다. 전송·키보드·클립보드 동작은 없다.</summary>
public interface IChatInput
{
    ChatInputState? Read();
    ChatInputResult Apply(ChatInputState expected, string body);
}

internal interface IChatInputBackend
{
    ChatInputState? Read();
    bool Replace(ChatInputState expected, string body);
}

/// <summary>비교 화면에서 확인한 창/입력값을 반영 직전에 다시 검사한다. 실패하면 자동 재시도하지 않는다.</summary>
public sealed class ChatInputWriter : IChatInput
{
    private readonly IChatInputBackend _backend;
    private readonly ChatSnapshot _bound;
    private readonly Func<bool> _canUse;

    public static IChatInput Create(IntPtr hwnd, string account, ChatSnapshot bound, Func<bool> canUse)
    {
        bool Allowed()
        {
            try { return canUse() && OnTalkAccount.DetectCandidate() == account; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException) { return false; }
        }
        return new ChatInputWriter(new NativeChatInputBackend(hwnd, Allowed), bound, Allowed);
    }

    internal ChatInputWriter(IChatInputBackend backend, ChatSnapshot bound, Func<bool> canUse)
    { _backend = backend; _bound = bound; _canUse = canUse; }

    public ChatInputState? Read()
    {
        if (!Allowed()) return null;
        var state = _backend.Read();
        return state is not null && Allowed() && Usable(state.Chat) && _bound.Continues(state.Chat) ? state : null;
    }

    public ChatInputResult Apply(ChatInputState expected, string body)
    {
        body = Normalize(body);
        // 비교하기 쉬운 짧은 텍스트 답변에만 반영 기능을 제공한다.
        if (string.IsNullOrWhiteSpace(body) || body.Length > 500 || body.Any(c => char.IsControl(c) && c != '\n'))
            return new(ChatInputStatus.InvalidText);
        if (Read() is not { } before) return new(ChatInputStatus.Unavailable);
        if (!SameTarget(expected, before)) return new(ChatInputStatus.ContextChanged, before);
        if (Normalize(before.Body) != Normalize(expected.Body)) return new(ChatInputStatus.Conflict, before);
        if (Normalize(before.Body) == body) return new(ChatInputStatus.Applied, before);
        if (!Allowed()) return new(ChatInputStatus.ContextChanged);
        if (!_backend.Replace(before, body)) return new(ChatInputStatus.NotSupported);
        // 쓰기 성공 응답만으로 성공으로 표시하지 않는다. 새로운 값을 다시 읽는다.
        for (var i = 0; i < 5; i++)
        {
            var after = Read();
            if (after is null || !SameTarget(before, after)) return new(ChatInputStatus.ContextChanged, after);
            if (Normalize(after.Body) == body) return new(ChatInputStatus.Applied, after);
            Thread.Sleep(100);
        }
        return new(ChatInputStatus.Mismatch);
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    private bool Allowed()
    {
        try { return _canUse(); }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { return false; }
    }
    private static bool Usable(ChatSnapshot chat) => !chat.Truncated && chat.Messages.Count > 0 &&
        !string.IsNullOrWhiteSpace(chat.RoomHint) && chat.RoomHint != "대화 상대 미확인";
    internal static bool SameTarget(ChatInputState a, ChatInputState b) =>
        a.ProcessId == b.ProcessId && a.Render == b.Render && a.Page == b.Page && a.Field == b.Field && a.Chat.Continues(b.Chat);
}
