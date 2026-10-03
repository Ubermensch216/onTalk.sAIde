using System.Text.RegularExpressions;

namespace OnTalk.Sidecar.Core;

/// <summary>온톡 화면에 표시되는 예약 시각 글자를 해석한다. 시각은 모두 한국 시각이고 분 단위다.</summary>
public static partial class ReservationTime
{
    // 작성 창: "2026. 10. 3 / 오후 3:30"
    [GeneratedRegex(@"^(\d{4})\.\s*(\d{1,2})\.\s*(\d{1,2})\s*/\s*(오전|오후)\s*(\d{1,2}):(\d{2})$")]
    private static partial Regex ComposePattern();

    // 예약 설정 대화상자: "2026년 10월 03일 " + "오후 " + "03:30"
    [GeneratedRegex(@"^(\d{4})년\s*(\d{1,2})월\s*(\d{1,2})일\s*(오전|오후)\s*(\d{1,2}):(\d{2})$")]
    private static partial Regex DialogPattern();

    public static DateTime? ParseCompose(string text) => Build(ComposePattern().Match(text.Trim()));

    public static DateTime? ParseDialog(string text) => Build(DialogPattern().Match(text.Trim()));

    /// <summary>예약 대화상자의 오전/오후·시·분 선택 값.</summary>
    public static (string AmPm, string Hour, string Minute) ComboValues(DateTime when) =>
        (when.Hour < 12 ? "오전" : "오후", (when.Hour % 12 == 0 ? 12 : when.Hour % 12).ToString(), when.Minute.ToString());

    private static DateTime? Build(Match m)
    {
        if (!m.Success)
        {
            return null;
        }
        int Group(int i) => int.Parse(m.Groups[i].Value);
        var hour = Group(5) % 12 + (m.Groups[4].Value == "오후" ? 12 : 0);
        return Group(2) is >= 1 and <= 12 && Group(3) >= 1 && Group(3) <= DateTime.DaysInMonth(Group(1), Group(2)) &&
               Group(5) is >= 1 and <= 12 && Group(6) <= 59
            ? new DateTime(Group(1), Group(2), Group(3), hour, Group(6), 0)
            : null;
    }
}

/// <summary>사이드카가 온톡 "받는 사람 추가" 창에서 골라 넣은 수신자. 넣는 순간 본 정보를 기록해 둔다.</summary>
/// <param name="Details">소속·부서·전화·메일 등 그 행에 표시된 글자.</param>
public sealed record Recipient(string Name, string Title, IReadOnlyList<string> Details);

public enum DispatchStatus
{
    /// <summary>전송 단추를 누르기 직전에 남긴 기록. 이 상태로 남아 있으면 결과를 모른다.</summary>
    Pending,
    /// <summary>전송 단추를 눌렀고 온톡 작성 창이 닫혔다. 서버 도착까지 확인한 것은 아니다.</summary>
    WindowClosed,
    /// <summary>전송 단추를 눌렀지만 작성 창이 그대로다. 온톡이 거부했거나 확인 창을 띄웠을 수 있다.</summary>
    StillOpen,
    /// <summary>누르기 전 재확인에서 초안과 달라 누르지 않았다.</summary>
    NotSent,
}

/// <param name="ReserveAt">예약 전송이면 예약 시각(한국 시각). 즉시 전송이면 null.</param>
public sealed record DispatchRecord(
    long Id,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> RecipientNames,
    string Body,
    DateTime? ReserveAt,
    DispatchStatus Status,
    string Detail);
