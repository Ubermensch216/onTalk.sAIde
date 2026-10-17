using System.Text.RegularExpressions;

namespace OnTalk.Sidecar.Core;

public sealed record PersonalizedNote(ContactPerson Person, string Body, IReadOnlyList<string> Errors)
{
    public bool Ready => Errors.Count == 0;
}

/// <summary>One-pass substitution: values cannot introduce executable templates or other people's values.</summary>
public static partial class NotePersonalization
{
    public static readonly string[] IdentityFields = ["이름", "소속", "직급"];

    [GeneratedRegex(@"\{([^{}\r\n]+)\}")]
    private static partial Regex Variable();

    public static IReadOnlyList<string> ExtractVariables(string template) =>
        Variable().Matches(template ?? "")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(name => name.Length > 0 && !IdentityFields.Contains(name))
            .Distinct()
            .ToList();

    public const int MaxHeaderLength = 30;

    /// <summary>필수 항목 중 서식 내용에 {이름} 형태로 들어 있지 않은 것.</summary>
    public static IReadOnlyList<string> MissingFields(string template, IEnumerable<string> required)
    {
        var used = Variable().Matches(template ?? "").Select(m => m.Groups[1].Value.Trim()).ToHashSet();
        return required.Where(h => !used.Contains(h)).ToList();
    }

    /// <summary>항목 제목으로 쓸 수 있는지 검사한다. 문제가 없으면 null.</summary>
    public static string? ValidateHeaderName(string? name, IEnumerable<string> existing, string? renaming = null)
    {
        var value = (name ?? "").Trim();
        if (value.Length == 0) return "항목 제목을 입력하세요.";
        if (value.Length > MaxHeaderLength) return $"항목 제목은 {MaxHeaderLength}자 이하로 입력하세요.";
        if (value.IndexOfAny(['{', '}', '\r', '\n', '\t']) >= 0) return "제목에는 중괄호와 줄바꿈을 쓸 수 없습니다.";
        if (IdentityFields.Contains(value)) return $"‘{value}’은(는) 대상자 정보로 자동 채워지는 기본 변수입니다. 다른 제목을 쓰세요.";
        if (existing.Any(h => h == value && h != renaming)) return $"‘{value}’ 항목이 이미 있습니다.";
        return null;
    }

    /// <summary>서식의 {from} 변수를 {to}로 바꾼다 (중괄호 안 공백은 무시).</summary>
    public static string RenameVariable(string template, string from, string to) =>
        Variable().Replace(template ?? "", m => m.Groups[1].Value.Trim() == from ? "{" + to + "}" : m.Value);

    /// <summary>서식에서 {name} 변수를 모두 지운다.</summary>
    public static string RemoveVariable(string template, string name) =>
        Variable().Replace(template ?? "", m => m.Groups[1].Value.Trim() == name ? "" : m.Value);

    public static bool IsValidBody(string body) => !string.IsNullOrWhiteSpace(body) &&
        !Variable().IsMatch(body) && NoteBytes.Count(body) <= NoteBytes.Limit;

    public static PersonalizedNote Render(ContactPerson person, string template, IReadOnlyDictionary<string, string> values)
    {
        var data = new Dictionary<string, string>(values)
        {
            ["이름"] = person.Name, ["소속"] = string.Join(" · ", person.Organization), ["직급"] = person.Title
        };
        var errors = new List<string>();
        var body = Variable().Replace(template, m =>
        {
            var field = m.Groups[1].Value.Trim();
            if (field.Length == 0) { errors.Add($"빈 변수: {m.Value}"); return m.Value; }
            if (!data.TryGetValue(field, out var value)) { errors.Add($"'{field}' 헤더가 없습니다."); return m.Value; }
            if (string.IsNullOrWhiteSpace(value))
            { errors.Add($"{field} 값이 없습니다."); return m.Value; }
            return value;
        });
        if (Variable().IsMatch(body) && errors.Count == 0) errors.Add("입력값에 미완성 변수가 있습니다.");
        if (string.IsNullOrWhiteSpace(body)) errors.Add("본문이 비어 있습니다.");
        if (NoteBytes.Count(body) > NoteBytes.Limit) errors.Add($"본문이 {NoteBytes.Limit}바이트를 초과합니다.");
        return new(person, body, errors.Distinct().ToList());
    }
}

public static class PreparedRecipientMatch
{
    // Compare whole organization segments, never substrings (총무과 != 총무과지원팀).
    public static bool Matches(ContactPerson person, Recipient candidate)
    {
        if (person.Name != candidate.Name || person.Title != candidate.Title || person.Organization.Count == 0) return false;
        static string[] Segments(IEnumerable<string> values) => values.SelectMany(v => v.Split(['/', '|', '·', '>'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var expected = Segments(person.Organization);
        var actual = Segments(candidate.Details);
        return expected.Length > 0 && expected.All(actual.Contains);
    }
}
