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

    [GeneratedRegex(@"(?<=[.!?。…])\s+")]
    private static partial Regex SentenceBreak();

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

    /// <summary>
    /// AI가 줄 바꿈 없이 한 덩어리로 돌려준 글에 원문의 줄·문단 구조를 되살린다.
    /// 변환 결과에 이미 줄 바꿈이 있거나 원문이 한 줄뿐이면 그대로 돌려준다.
    /// 원문의 줄 수(비어 있지 않은 줄)만큼 결과의 문장을 글자 수 비율대로 나누어 놓고, 줄 사이의 빈 줄 수도 원문과 같게 한다.
    /// </summary>
    public static string RestoreLineBreaks(string original, string refined)
    {
        refined = refined.Replace("\r\n", "\n").Trim();
        if (refined.Contains('\n')) return refined;
        var lines = original.Replace("\r\n", "\n").Trim('\n').Split('\n');
        var kept = new List<int>();          // 비어 있지 않은 줄의 글자 수
        var gaps = new List<int>();          // k번째 줄 앞의 빈 줄 수(k >= 1)
        var blanks = 0;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) { if (kept.Count > 0) blanks++; continue; }
            if (kept.Count > 0) gaps.Add(blanks);
            blanks = 0;
            kept.Add(line.Trim().Length);
        }
        var sentences = SentenceBreak().Split(refined).Where(x => x.Length > 0).ToList();
        var groups = Math.Min(kept.Count, sentences.Count);
        if (groups < 2) return refined;

        // 원문 줄을 groups개 묶음으로 나눈다. 묶음 경계는 글자 수 누적이 균등 분할점에 가장 가까운 줄 번호로 고른다.
        var lineTotal = kept.Sum();
        var lineEnd = Boundaries(kept, groups, Enumerable.Range(1, groups - 1).Select(k => lineTotal * (double)k / groups).ToList());
        // 같은 누적 비율 위치에서 결과 문장을 나누고, 묶음 사이에는 원문과 같은 수의 빈 줄을 둔다.
        var sentenceLengths = sentences.Select(x => x.Length).ToList();
        var sentenceTotal = sentenceLengths.Sum();
        var sentenceEnd = Boundaries(sentenceLengths, groups,
            lineEnd.Select(end => kept.Take(end).Sum() / (double)lineTotal * sentenceTotal).ToList());
        var result = new System.Text.StringBuilder();
        var from = 0;
        for (var k = 0; k < groups; k++)
        {
            var to = k < groups - 1 ? sentenceEnd[k] : sentences.Count;
            result.Append(string.Join(' ', sentences.Skip(from).Take(to - from)));
            if (k < groups - 1) result.Append('\n', gaps[lineEnd[k] - 1] + 1);
            from = to;
        }
        return result.ToString();
    }

    /// <summary>항목 길이의 누적이 각 목표값에 가장 가까워지는 항목 수를 골라, 항목마다 최소 하나씩 남도록 groups-1개의 경계를 돌려준다.</summary>
    private static List<int> Boundaries(IReadOnlyList<int> lengths, int groups, IReadOnlyList<double> targets)
    {
        var ends = new List<int>();
        var previous = 0;
        for (var k = 1; k < groups; k++)
        {
            var best = previous + 1;
            var bestDistance = double.MaxValue;
            var cumulative = lengths.Take(previous).Sum();
            for (var count = previous + 1; count <= lengths.Count - (groups - k); count++)
            {
                cumulative += lengths[count - 1];
                var distance = Math.Abs(cumulative - targets[k - 1]);
                if (distance < bestDistance) { bestDistance = distance; best = count; }
            }
            ends.Add(best);
            previous = best;
        }
        return ends;
    }
}
