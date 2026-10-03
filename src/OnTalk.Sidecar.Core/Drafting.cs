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
}
