using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.App;

/// <summary>패널의 할 일 후보 한 줄.</summary>
public sealed class CandidateRow(ActionCandidate candidate, bool registered)
{
    public ActionCandidate Candidate { get; } = candidate;
    public bool CanRegister { get; } = !registered;
    public bool IsChecked { get; set; }
    public string Title { get; } = registered ? $"{candidate.Title} (등록됨)" : candidate.Title;

    public string DueLine { get; } = candidate switch
    {
        { Due: { } due } => $"기한 {due:yyyy-MM-dd} · 원문 \"{candidate.DueText}\"",
        { DueText: { } text } => $"기한 표현 \"{text}\"을(를) 날짜로 해석하지 못했습니다. 등록 후 직접 정하세요.",
        _ => "기한: 원문에 적혀 있지 않음",
    };

    public string EvidenceLine { get; } = $"근거: {candidate.Evidence.ReplaceLineEndings(" ")}";
}

/// <summary>업무 한 줄. 패널과 할 일 목록 창이 함께 쓴다.</summary>
public sealed class TaskRow(TaskItem task, DateTimeOffset now, string? origin = null)
{
    public long Id { get; } = task.Id;
    public string Title { get; } = task.Title;
    public bool Done { get; } = task.Done;
    public DateTime? Due { get; } = task.Due?.ToDateTime(TimeOnly.MinValue);
    public string Origin { get; } = origin ?? "직접 추가";

    public string DueLine { get; } = task switch
    {
        { Done: true } => "완료",
        { Due: { } due } => $"기한 {due:yyyy-MM-dd} · {ReminderRule.Describe(due, now)}",
        _ => "기한 없음 · 알리지 않음",
    };
}
