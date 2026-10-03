using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

public enum ApplyStatus
{
    /// <summary>반영 후 다시 읽은 본문과 바이트 표시가 초안과 일치한다.</summary>
    Applied,
    /// <summary>작성 창이 아니거나 닫혔다.</summary>
    NotCompose,
    /// <summary>예약 설정 같은 대화상자가 떠 있다.</summary>
    DialogOpen,
    /// <summary>온톡 창의 본문이 마지막으로 맞춘 내용과 다르다. 사용자가 온톡에서 직접 고쳤다.</summary>
    Conflict,
    TooLong,
    /// <summary>입력창이 다른 창에 가려져 있고 앞으로 올리지 못했다.</summary>
    Covered,
    /// <summary>클릭했지만 대상 창이 앞에 오지 않았다. 입력 주입이 막혔을 수 있다.</summary>
    FocusFailed,
    /// <summary>사용자가 Ctrl·Alt·Shift·Win 키를 누르고 있다.</summary>
    ModifierDown,
    ClipboardBusy,
    /// <summary>붙여넣은 뒤 다시 읽은 내용이 초안과 다르다. 다시 시도하지 않는다.</summary>
    Mismatch,
}

/// <param name="After">반영 뒤 다시 읽은 작성 창 상태.</param>
/// <param name="ClipboardRestored">클립보드를 원래대로 되돌렸는지. 클립보드를 건드리지 않았으면 true.</param>
public sealed record ApplyResult(ApplyStatus Status, ComposeState? After, bool ClipboardRestored = true);

/// <summary>
/// 온톡 작성 창의 본문을 초안으로 바꾼다. 어느 방식으로 넣든 넣은 뒤 본문과 온톡의 바이트 표시를 다시 읽어
/// 초안과 같은지 확인한다. 바이트 표시는 온톡 내부 상태에서 나오므로, 화면에만 글자가 보이고 내부 상태는
/// 바뀌지 않은 경우를 걸러낸다. 전송은 하지 않는다.
/// </summary>
public static class ComposeWriter
{
    public static ComposeState? Read(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd) || Win32.FindRenderChild(hwnd) is not { } render ||
            MsaaReader.ReadTree(render) is not { } tree || !NoteWindowParser.IsNotePage(tree))
        {
            return null;
        }
        return NoteWindowParser.Parse(tree, DateTimeOffset.Now).Compose;
    }

    /// <param name="expectedCurrentBody">
    /// 마지막으로 온톡과 맞춘 본문. 지금 온톡 본문이 이와 다르면 덮어쓰지 않고 <see cref="ApplyStatus.Conflict"/>를
    /// 돌려준다. null이면 확인 없이 덮어쓴다(사용자가 덮어쓰기를 고른 경우).
    /// </param>
    public static ApplyResult ApplyBody(IntPtr hwnd, string body, string? expectedCurrentBody)
    {
        body = body.Replace("\r\n", "\n");
        if (Read(hwnd) is not { } before)
        {
            return new ApplyResult(ApplyStatus.NotCompose, null);
        }
        if (before.DialogOpen)
        {
            return new ApplyResult(ApplyStatus.DialogOpen, before);
        }
        // 이미 같은 내용이면 손대지 않는다.
        if (before.Body == body)
        {
            return new ApplyResult(ApplyStatus.Applied, before);
        }        if (expectedCurrentBody is not null && before.Body != expectedCurrentBody.Replace("\r\n", "\n"))
        {
            return new ApplyResult(ApplyStatus.Conflict, before);
        }
        if (NoteBytes.Count(body) > before.LimitBytes)
        {
            return new ApplyResult(ApplyStatus.TooLong, before);
        }

        // 먼저 접근성 기능으로 값을 넣는다. 창을 앞으로 가져오지 않고 키보드·마우스·클립보드도 쓰지 않아
        // 사용자가 치던 글자가 온톡 창으로 들어갈 일이 없다. 넣은 뒤 본문과 바이트 표시를 다시 읽어 확인하고,
        // 확인되지 않을 때만 클릭해 붙여넣는 방식으로 넘어간다.
        if (Win32.FindRenderChild(hwnd) is { } render && MsaaReader.ReadTree(render) is { } tree &&
            tree.DescendantsAndSelf().FirstOrDefault(n => n.Role == AccNode.EditableText && n.Name.Length > 0) is { } field &&
            MsaaReader.SetValue(render, field, body) &&
            Verify(hwnd, body, typed: true, clipboardRestored: true, attempts: 6) is { Status: ApplyStatus.Applied } quiet)
        {
            return quiet;
        }
        switch (InputInjector.FocusByClick(hwnd, before.BodyBounds))
        {
            case FocusOutcome.ModifierDown:
                return new ApplyResult(ApplyStatus.ModifierDown, before);
            case FocusOutcome.Covered:
                return new ApplyResult(ApplyStatus.Covered, before);
            case FocusOutcome.NotForeground:
                return new ApplyResult(ApplyStatus.FocusFailed, Read(hwnd));
        }
        var (typed, restored) = InputInjector.ReplaceText(hwnd, body);
        if (!typed && restored && body.Length > 0 && Win32.GetForegroundWindow() == hwnd)
        {
            return new ApplyResult(ApplyStatus.ClipboardBusy, Read(hwnd));
        }
        return Verify(hwnd, body, typed, restored);
    }

    // 붙여넣기가 화면에 반영될 때까지 잠깐 기다리며 다시 읽는다.
    private static ApplyResult Verify(IntPtr hwnd, string body, bool typed, bool clipboardRestored, int attempts = 10)
    {
        if (!typed)
        {
            return new ApplyResult(ApplyStatus.FocusFailed, Read(hwnd), clipboardRestored);
        }
        ComposeState? after = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            Thread.Sleep(200);
            after = Read(hwnd);
            if (after is not null && after.Body == body && after.UsedBytes == NoteBytes.Count(body))
            {
                return new ApplyResult(ApplyStatus.Applied, after, clipboardRestored);
            }
        }
        return new ApplyResult(after is null ? ApplyStatus.NotCompose : ApplyStatus.Mismatch, after, clipboardRestored);
    }
}
