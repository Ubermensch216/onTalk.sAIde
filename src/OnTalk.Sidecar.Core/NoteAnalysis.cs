using System.Text.Json.Serialization;

namespace OnTalk.Sidecar.Core;

/// <summary>모델이 내놓은 그대로의 결과. 검증 전이라 믿지 않는다.</summary>
public sealed record RawAnalysis(
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("actions")] List<RawAction>? Actions,
    [property: JsonPropertyName("uncertainties")] List<string>? Uncertainties);

public sealed record RawAction(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("evidence")] string? Evidence,
    [property: JsonPropertyName("due_text")] string? DueText);

/// <param name="Evidence">쪽지 원문에 실제로 있는 문장.</param>
/// <param name="DueText">쪽지 원문에 실제로 있는 기한 표현. 없으면 null.</param>
/// <param name="Due">DueText를 코드가 해석한 날짜. 해석하지 못하면 null.</param>
public sealed record ActionCandidate(string Title, string Evidence, string? DueText, DateOnly? Due);

/// <param name="Summary">AI가 쓴 요약. 원문과 기계적으로 대조할 수 없으므로 화면에 AI 요약임을 표시한다.</param>
/// <param name="DroppedActions">원문에서 근거를 찾지 못해 버린 후보 수.</param>
public sealed record NoteAnalysis(
    string Summary,
    IReadOnlyList<ActionCandidate> Actions,
    IReadOnlyList<string> Uncertainties,
    int DroppedActions);

public static class AnalysisValidator
{
    /// <summary>
    /// 모델 결과를 쪽지 원문과 대조한다. 근거 문장이 원문에 없는 할 일은 버리고,
    /// 기한 표현이 원문에 없으면 기한을 비운다.
    /// </summary>
    public static NoteAnalysis Validate(RawAnalysis raw, string body, DateOnly sentDate)
    {
        var haystack = Squash(body);
        var actions = new List<ActionCandidate>();
        var dropped = 0;
        foreach (var action in raw.Actions ?? [])
        {
            var title = action.Title?.Trim() ?? "";
            var evidence = action.Evidence?.Trim() ?? "";
            if (title.Length == 0 || !Quoted(haystack, evidence))
            {
                dropped++;
                continue;
            }
            if (actions.Any(a => a.Title == title))
            {
                continue;
            }
            var dueText = action.DueText?.Trim() ?? "";
            var hasDue = Quoted(haystack, dueText);
            actions.Add(new ActionCandidate(
                title, evidence, hasDue ? dueText : null, hasDue ? DueDateParser.Parse(dueText, sentDate) : null));
        }
        var uncertainties = (raw.Uncertainties ?? [])
            .Select(u => u?.Trim() ?? "").Where(u => u.Length > 0).Distinct().ToList();
        return new NoteAnalysis(raw.Summary?.Trim() ?? "", actions, uncertainties, dropped);
    }

    // 모델이 줄바꿈·띄어쓰기를 바꿔 옮기는 일이 잦아 공백은 무시하고 비교한다.
    private static bool Quoted(string squashedBody, string quote)
    {
        var q = Squash(quote);
        return q.Length >= 2 && squashedBody.Contains(q, StringComparison.Ordinal);
    }

    private static string Squash(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
