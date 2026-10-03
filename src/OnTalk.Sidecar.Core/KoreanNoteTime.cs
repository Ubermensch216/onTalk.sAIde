using System.Text.RegularExpressions;

namespace OnTalk.Sidecar.Core;

public static partial class KoreanNoteTime
{
    public static readonly TimeSpan SeoulOffset = TimeSpan.FromHours(9);

    // 온톡은 오늘 받은 쪽지를 "오후 03:05", 그 전 쪽지를 "10월 2일 오후 07:51"로 표시한다.
    [GeneratedRegex(@"^(?:(?:(\d{4})년\s*)?(\d{1,2})월\s*(\d{1,2})일\s*)?(오전|오후)\s*(\d{1,2}):(\d{2})$")]
    private static partial Regex Pattern();

    public static bool LooksLikeTime(string text) => Pattern().IsMatch(text.Trim());

    /// <summary>
    /// 쪽지 창의 시각 표시를 해석한다. 날짜가 없으면 <paramref name="now"/>의 날짜로, 연도가 없으면
    /// <paramref name="now"/>보다 미래가 되지 않는 가장 최근 연도로 본다. 둘 다 추정으로 표시한다.
    /// </summary>
    public static NoteTime? Parse(string text, DateTimeOffset now)
    {
        var m = Pattern().Match(text.Trim());
        if (!m.Success)
        {
            return null;
        }
        var hour = int.Parse(m.Groups[5].Value) % 12 + (m.Groups[4].Value == "오후" ? 12 : 0);
        var minute = int.Parse(m.Groups[6].Value);
        var seoulNow = now.ToOffset(SeoulOffset);

        if (!m.Groups[2].Success)
        {
            return Build(seoulNow.Year, seoulNow.Month, seoulNow.Day, hour, minute) is { } today
                ? new NoteTime(today, true)
                : null;
        }
        var month = int.Parse(m.Groups[2].Value);
        var day = int.Parse(m.Groups[3].Value);
        if (m.Groups[1].Success)
        {
            return Build(int.Parse(m.Groups[1].Value), month, day, hour, minute) is { } exact
                ? new NoteTime(exact, false)
                : null;
        }
        // 2월 29일처럼 해당 연도에 없는 날짜는 건너뛴다.
        for (var year = seoulNow.Year; year >= seoulNow.Year - 4; year--)
        {
            if (Build(year, month, day, hour, minute) is { } candidate && candidate <= seoulNow.AddMinutes(5))
            {
                return new NoteTime(candidate, true);
            }
        }
        return null;
    }

    private static DateTimeOffset? Build(int year, int month, int day, int hour, int minute)
    {
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
        {
            return null;
        }
        return new DateTimeOffset(year, month, day, hour, minute, 0, SeoulOffset);
    }
}
