namespace OnTalk.Sidecar.Core;

/// <param name="NoteId">업무가 나온 쪽지. 직접 추가한 업무는 null.</param>
/// <param name="RemindedStage">이미 알린 단계. <see cref="ReminderRule"/> 참조.</param>
public sealed record TaskItem(
    long Id,
    long? NoteId,
    string Title,
    string? Evidence,
    string? DueText,
    DateOnly? Due,
    bool Done,
    DateTimeOffset CreatedAt,
    int RemindedStage);

/// <summary>기한이 있는 업무만 알린다. 전날 09:00에 한 번, 당일 09:00에 한 번.</summary>
public static class ReminderRule
{
    public const int DayBefore = 1;
    public const int DueDay = 2;
    private static readonly TimeOnly RemindAt = new(9, 0);

    /// <summary>지금 시각까지 도달한 알림 단계. 아직 알릴 때가 아니면 0.</summary>
    public static int StageReached(DateOnly due, DateTimeOffset now)
    {
        var seoulNow = now.ToOffset(KoreanNoteTime.SeoulOffset).DateTime;
        if (seoulNow >= due.ToDateTime(RemindAt))
        {
            return DueDay;
        }
        return seoulNow >= due.AddDays(-1).ToDateTime(RemindAt) ? DayBefore : 0;
    }

    public static bool IsDue(TaskItem task, DateTimeOffset now) =>
        !task.Done && task.Due is { } due && StageReached(due, now) > task.RemindedStage;

    public static string Describe(DateOnly due, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToOffset(KoreanNoteTime.SeoulOffset).DateTime);
        var days = due.DayNumber - today.DayNumber;
        return days switch
        {
            < 0 => $"기한 {-days}일 지남",
            0 => "오늘 마감",
            1 => "내일 마감",
            _ => $"{days}일 남음",
        };
    }
}
