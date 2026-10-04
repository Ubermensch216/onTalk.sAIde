using System.Security.Cryptography;
using System.Text;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

/// <summary>같은 HWND의 읽기→작성 전환과 다른 원문으로의 전환을 독립 작업으로 구분한다.</summary>
public sealed class WindowSessionTracker
{
    private sealed record Entry(IntPtr Render, uint Pid, string Stamp, Guid Session);
    private readonly Dictionary<IntPtr, Entry> _windows = [];
    private readonly Dictionary<IntPtr, ChatSnapshot> _chats = [];

    public Guid Observe(IntPtr hwnd, IntPtr render, uint pid, SidecarContext context, NoteWindowContent? content = null, ChatSnapshot? chat = null)
    {
        var source = content?.Note?.Key ?? "";
        if (content?.Compose is { } compose)
        {
            // 편집 중인 본문과 수신자 변경은 새 작업이 아니다. 현재 창이 제공한 원문 인용만 구분한다.
            var quote = DraftText.SplitQuote(compose.Body).Quote;
            source = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(quote)));
        }
        var stamp = $"{context.Surface}:{context.ComposeKind}:{source}";
        var roomChanged = chat is not null && _chats.TryGetValue(hwnd, out var previousChat) && !chat.Continues(previousChat);
        if (chat is not null) { _chats[hwnd] = chat; stamp += ":" + chat.RoomHint; }
        else if (context.Surface != OnTalkSurface.Unknown) _chats.Remove(hwnd);
        if (!_windows.TryGetValue(hwnd, out var previous) || previous.Render != render || previous.Pid != pid ||
            roomChanged || (context.Surface != OnTalkSurface.Unknown && previous.Stamp != stamp))
        {
            _windows[hwnd] = previous = new Entry(render, pid, stamp, Guid.NewGuid());
        }
        return previous.Session;
    }

    public void RemoveMissing(Func<IntPtr, bool> exists)
    {
        foreach (var hwnd in _windows.Keys.Where(h => !exists(h)).ToList()) { _windows.Remove(hwnd); _chats.Remove(hwnd); }
    }

    public void Clear() { _windows.Clear(); _chats.Clear(); }
}
