using System.Text.RegularExpressions;

namespace OnTalk.Sidecar.Core;

public sealed record Template(long Id, string Name, string Body);

public static partial class DraftText
{
    /// <summary>받은 쪽지에 대한 답장 초안이 저장되는 자리.</summary>
    public static string ReplySlot(long noteId) => $"note:{noteId}";

    /// <summary>특정 쪽지와 무관한 새 쪽지 초안이 저장되는 자리.</summary>
    public const string NewSlot = "new";

    [GeneratedRegex(@"^={10,}[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex QuoteSeparator();

    [GeneratedRegex(@"\{[^{}\n]{1,30}\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// 온톡 답장 창이 넣어 주는 원문 인용("====" 줄부터 끝까지)을 내가 쓴 글과 나눈다.
    /// 인용이 없으면 Quote는 빈 문자열이다.
    /// </summary>
    public static (string Message, string Quote) SplitQuote(string body)
    {
        body = body.Replace("\r\n", "\n");
        var match = QuoteSeparator().Match(body);
        if (!match.Success)
        {
            return (body, "");
        }
        var message = body[..match.Index];
        return (message.EndsWith('\n') ? message[..^1] : message, body[match.Index..]);
    }

    /// <summary><see cref="SplitQuote"/>의 반대. 나눈 것을 다시 합치면 원래 본문이 된다.</summary>
    public static string JoinQuote(string message, string quote) =>
        quote.Length == 0 ? message.Replace("\r\n", "\n") : $"{message.Replace("\r\n", "\n")}\n{quote}";

    /// <summary>아직 채우지 않은 "{이름}" 같은 자리 표시. 남아 있으면 온톡에 반영하지 않는다.</summary>
    public static IReadOnlyList<string> UnfilledPlaceholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Value).Distinct().ToList();

    /// <summary>
    /// 블럭 지정 범위에 공백·줄바꿈만 있으면 선택하지 않은 것으로 보고, 앞뒤 공백·줄바꿈은 범위에서 제외한다.
    /// </summary>
    public static bool TryTrimSelection(string text, int selectionStart, int selectionLength, out int start, out int length, out string selected)
    {
        start = selectionStart;
        length = selectionLength;
        selected = "";
        if (length <= 0 || start < 0 || start + length > text.Length) return false;
        var lead = 0;
        while (lead < length && char.IsWhiteSpace(text[start + lead])) lead++;
        var trail = 0;
        while (trail < length - lead && char.IsWhiteSpace(text[start + length - 1 - trail])) trail++;
        if (lead + trail >= length) return false;
        start += lead;
        length -= lead + trail;
        selected = text.Substring(start, length);
        return true;
    }

    /// <summary>
    /// 블럭 지정이 있으면 해당 구간만 변환 결과로 바꾸고(앞뒤 줄바꿈 보존), 없으면 전체 본문을 바꾼다.
    /// </summary>
    public static (string Text, int SelectionStart, int SelectionLength, bool IsBlock) ApplyRefinement(
        string text, int selectionStart, int selectionLength, string refined)
    {
        if (TryTrimSelection(text, selectionStart, selectionLength, out var start, out var length, out _))
        {
            var updated = string.Concat(text.AsSpan(0, start), refined, text.AsSpan(start + length));
            return (updated, start, refined.Length, true);
        }
        return (refined, 0, 0, false);
    }
}
