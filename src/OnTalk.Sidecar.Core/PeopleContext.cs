using System.Security.Cryptography;
using System.Text;

namespace OnTalk.Sidecar.Core;

/// <summary>관찰한 행. Key는 현재 화면 안의 주소이며 계정/사람의 고유 식별자가 아니다.</summary>
public sealed record ContactPerson(string Key, string Name, string Title, IReadOnlyList<string> Organization,
    string Status = "", string Duty = "", string Phone = "", string Mail = "")
{
    public DateTimeOffset? DetailsObservedAt { get; init; }
    // 선택 유지용. 온라인 상태나 잠깐 나타나는 상세 정보는 선택 대상을 바꾸지 않는다.
    public string SelectionKey => Key + ":" + DisplayIdentity;
    public string DisplayIdentity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Name + "\n" + Title + "\n" + string.Join("\n", Organization))));
    public string Label => $"{Name} · {Title}\n{string.Join(" · ", Organization)}";
    public override string ToString() => Label;
}

public enum ContactScopeKind { Group, Department }
public sealed record ContactScope(string Key, string Name, ContactScopeKind Kind,
    IReadOnlyList<string> MemberKeys, int? DeclaredCount = null, bool Selected = false)
{
    public override string ToString() => Name;
}

public sealed record PeopleSnapshot(MainMenu Menu, IReadOnlyList<ContactPerson> People,
    IReadOnlyList<ContactScope> Scopes, IReadOnlyList<string> SelectedKeys, DateTimeOffset ObservedAt,
    bool Truncated = false)
{
    public string Coverage => $"현재 화면에서 확인한 {People.Count}명 · " +
        (Truncated ? "읽기 한도에 도달한 일부 목록" : "접힌 부서·필터 밖 사람은 포함하지 않음") +
        $" · {ObservedAt.ToLocalTime():HH:mm:ss} 확인";
}

public sealed record ContactMatch(ContactPerson Person, string Evidence);
public sealed record ContactPreparation(string Purpose, IReadOnlyList<ContactPerson> People, string Source, DateTimeOffset ObservedAt)
{
    public string InitialDraft => Purpose switch
    {
        "진행 확인" => "안녕하세요. {업무}의 진행 상황과 다음 일정을 알려 주시면 감사하겠습니다.",
        "자료 요청" => "안녕하세요. {업무 목적}에 필요한 {자료}를 요청드립니다. {필요 시점}까지 공유 가능한지 확인 부탁드립니다.",
        "감사" => "안녕하세요. {도움을 받은 내용}에 감사드립니다.",
        _ => "안녕하세요. {문의할 내용}에 관해 확인 부탁드립니다."
    };
}

public static class ContactSupport
{
    /// <summary>확인한 글자만 검색한다. 담당업무가 없으면 역할을 추정하지 않는다.</summary>
    public static IReadOnlyList<ContactMatch> Find(IEnumerable<ContactPerson> people, string query)
    {
        var terms = NoteQuery.SplitTerms(query);
        if (terms.Count == 0) return [];
        return people.Select(person =>
        {
            var fields = new[] { (Label: "담당업무", Value: person.Duty), (Label: "소속", Value: string.Join(" · ", person.Organization)),
                (Label: "이름", Value: person.Name), (Label: "직급", Value: person.Title) };
            var matches = fields.Where(f => terms.Any(t => f.Value.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
            var text = string.Join("\n", fields.Select(f => f.Value));
            return terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))
                ? new ContactMatch(person, string.Join("\n", matches.Select(f => $"{f.Label} 글자 일치: {f.Value}"))) : null;
        }).OfType<ContactMatch>().ToList();
    }

    public static string IdentityWarning(IReadOnlyList<ContactPerson> people) =>
        people.GroupBy(p => p.DisplayIdentity).Any(g => g.Count() > 1)
            ? "이름·직급·소속이 같은 행이 여러 개입니다. 같은 사람이라고 합치지 않았습니다. 수신자 검색에서 다시 확인하세요."
            : people.GroupBy(p => p.Name).Any(g => g.Count() > 1)
                ? "동명이인이 있습니다. 직급과 소속을 비교하고 수신자를 다시 확인하세요."
                : "화면 표시 정보입니다. 사람 고유 ID는 확인하지 못했습니다. 실제 수신자는 작성창에서 다시 확인하세요.";
}
