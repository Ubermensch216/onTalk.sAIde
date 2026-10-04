using System.Text.RegularExpressions;

namespace OnTalk.Sidecar.Core;

/// <summary>
/// 로컬 AI를 쓸 수 없을 때 쪽지 원문에서 할 일로 보이는 문장을 글자 그대로 추린다.
/// 문장을 고쳐 쓰거나 요약하지 않으므로 근거는 언제나 원문과 같다.
/// </summary>
public static partial class SentenceCandidates
{
    private const int MaxCandidates = 8;
    private const int MaxTitleLength = 100;

    [GeneratedRegex("요청|제출|이수|참석|회신|확인|작성|등록|신청|검토|준비|바랍니다|주시기|주세요")]
    private static partial Regex ActionWords();

    // 기한으로 보이는 표현. 실제 날짜인지는 DueDateParser가 판단한다("56시간", "3.5점"은 걸러진다).
    [GeneratedRegex(@"(?:\d{4}\s*[.\-/년]\s*)?\d{1,2}\s*(?:월\s*\d{1,2}\s*일|[./]\s*\d{1,2}\s*\.?)(?:\s*\([^)]{1,3}\))?(?:\s*까지)?|오늘|내일|모레")]
    private static partial Regex DueWords();

    /// <param name="sentDate">쪽지를 보낸 날. 연도가 없는 기한을 해석하는 기준이다.</param>
    public static NoteAnalysis Find(string body, DateOnly sentDate)
    {
        var actions = new List<ActionCandidate>();
        foreach (var line in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length <= 3 || !ActionWords().IsMatch(line) || actions.Any(a => a.Evidence == line))
            {
                continue;
            }
            var due = DueWords().Matches(line)
                .Select(m => (Text: m.Value.Trim(), Date: DueDateParser.Parse(m.Value, sentDate)))
                .LastOrDefault(d => d.Date is not null);
            actions.Add(new ActionCandidate(
                line.Length <= MaxTitleLength ? line : line[..MaxTitleLength] + "…", line, due.Text, due.Date));
            if (actions.Count == MaxCandidates)
            {
                break;
            }
        }
        return new NoteAnalysis("", actions, [], 0);
    }
}
