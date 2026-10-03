using System.Text.RegularExpressions;

namespace OnTalk.Sidecar.Core;

/// <summary>
/// 쪽지에 적힌 기한 표현을 날짜로 바꾼다. 날짜는 AI가 아니라 이 코드가 정한다.
/// 해석할 수 없는 표현은 추측하지 않고 null을 돌려준다.
/// </summary>
public static partial class DueDateParser
{
    [GeneratedRegex(@"(?<y>\d{4})\s*[.\-/년]\s*(?<m>\d{1,2})\s*[.\-/월]\s*(?<d>\d{1,2})")]
    private static partial Regex FullDate();

    [GeneratedRegex(@"(?<m>\d{1,2})\s*월\s*(?<d>\d{1,2})\s*일")]
    private static partial Regex MonthDay();

    // "10/15" 또는 "10. 15."처럼 날짜임이 드러나는 형태만 받는다. "3.5점" 같은 소수는 날짜로 보지 않는다.
    [GeneratedRegex(@"(?<![\d.\-/])(?:(?<m>\d{1,2})/(?<d>\d{1,2})(?![\d/])|(?<m>\d{1,2})\s*\.\s*(?<d>\d{1,2})(?=\s*(?:\.(?!\d)|\(|까지)))")]
    private static partial Regex ShortDate();

    /// <param name="reference">쪽지를 보낸 날. 연도가 없는 표현은 이 날 이후의 가장 가까운 날짜로 본다.</param>
    public static DateOnly? Parse(string text, DateOnly reference)
    {
        // 기간("10.1.~10.15.")이면 끝 날짜가 기한이므로 마지막 표현을 쓴다.
        if (FullDate().Matches(text).LastOrDefault() is { } full)
        {
            return Build(int.Parse(full.Groups["y"].Value), full);
        }
        var partial = MonthDay().Matches(text).LastOrDefault() ?? ShortDate().Matches(text).LastOrDefault();
        if (partial is not null)
        {
            var date = Build(reference.Year, partial);
            return date is { } d && d < reference ? Build(reference.Year + 1, partial) : date;
        }
        if (text.Contains("오늘"))
        {
            return reference;
        }
        if (text.Contains("내일"))
        {
            return reference.AddDays(1);
        }
        if (text.Contains("모레"))
        {
            return reference.AddDays(2);
        }
        return null;
    }

    private static DateOnly? Build(int year, Match match)
    {
        var month = int.Parse(match.Groups["m"].Value);
        var day = int.Parse(match.Groups["d"].Value);
        return year is >= 2000 and <= 2100 && month is >= 1 and <= 12 && day >= 1 &&
               day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
    }
}
