using OnTalk.Sidecar.Core;
using MainMenu = OnTalk.Sidecar.Core.MainMenu;

namespace OnTalk.Sidecar.Automation;

/// <summary>친구 목록/조직도 트리와 잠깐 표시되는 사람 상세를 읽는다. 선택 가능 상태를 선택으로 오인하지 않는다.</summary>
public static class MainPeopleParser
{
    private static readonly HashSet<string> Statuses = ["온라인", "오프라인", "자리비움", "다른 용무 중", "회의 중", "바쁨"];
    private sealed record Row(AccNode Node, ContactPerson Person, IReadOnlyList<AccNode> Ancestors);

    public static PeopleSnapshot Parse(AccNode root, MainMenu menu, DateTimeOffset observedAt)
    {
        if (WindowContextParser.Parse(root) is not { Surface: OnTalkSurface.Main } context || context.Menu != menu ||
            menu is not (MainMenu.Friends or MainMenu.Organization)) return new(menu, [], [], [], observedAt);
        var rows = new List<Row>();
        Walk(root, []);
        var scopes = new List<ContactScope>();
        var selected = rows.Where(r => IsSelected(r.Node)).Select(r => r.Person.Key).ToHashSet();
        if (menu == MainMenu.Organization)
        {
            foreach (var node in VisibleNodes(root).Where(n => n.Role == "outlineitem" && !rows.Any(r => ReferenceEquals(r.Node, n))))
            {
                var name = DepartmentName(node);
                if (name.Length == 0) continue;
                var members = rows.Where(r => r.Ancestors.Contains(node)).Select(r => r.Person.Key).ToList();
                var scope = new ContactScope(Key(node), name, ContactScopeKind.Department, members, Selected: IsSelected(node));
                scopes.Add(scope);
                if (scope.Selected) selected.UnionWith(members);
            }
        }
        else
        {
            foreach (var check in VisibleNodes(root).Where(n => n.Role == AccNode.CheckBox))
            {
                var chain = AncestorsOf(root, check);
                // 같은 체크박스를 가진 그룹의 가장 가까운 구성원 컨테이너. 바깥의 모든 그룹을 묶지 않는다.
                var container = chain.AsEnumerable().Reverse().FirstOrDefault(n =>
                    n.DescendantsAndSelf().Count(c => c.Role == AccNode.CheckBox) == 1 && rows.Any(r => r.Ancestors.Contains(n)));
                var header = chain.AsEnumerable().Reverse().FirstOrDefault(n =>
                    n.DescendantsAndSelf().Count(c => c.Role == AccNode.CheckBox) == 1 && GroupTexts(n, rows).Count > 0);
                var texts = header is null ? [] : GroupTexts(header, rows);
                var name = texts.FirstOrDefault(t => !int.TryParse(t, out _)) ?? "";
                if (name.Length == 0) continue;
                var members = container is null ? [] : rows.Where(r => r.Ancestors.Contains(container)).Select(r => r.Person.Key).ToList();
                int? count = texts.Select(t => int.TryParse(t, out var number) ? (int?)number : null).FirstOrDefault(c => c.HasValue);
                var scope = new ContactScope(Key(check), name, ContactScopeKind.Group, members, count, (check.State & AccNode.StateChecked) != 0);
                scopes.Add(scope);
                if (scope.Selected) selected.UnionWith(members);
            }
        }

        var people = rows.Select(r => r.Person).ToList();
        // 마우스 아래의 상세는 선택이 아니다. 이름·직급이 같은 여러 행에 한 사람의 업무를 붙이지 않는다.
        foreach (var tip in VisibleNodes(root).Where(n => n.Role == "tooltip"))
        {
            var texts = tip.StaticTexts().Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            if (texts.Count < 2) continue;
            var matching = people.Select((p, i) => (Person: p, Index: i)).Where(p => p.Person.Name == texts[0] && p.Person.Title == texts[1]).ToList();
            if (matching.Count != 1) continue;
            var index = matching[0].Index;
            people[index] = people[index] with { Duty = After("담당업무"), Phone = After("직장전화"), Mail = After("메일", "이메일"), DetailsObservedAt = observedAt };
            string After(params string[] labels)
            {
                var at = texts.FindIndex(t => labels.Contains(t));
                return at >= 0 ? texts.ElementAtOrDefault(at + 1) ?? "" : "";
            }
        }
        return new(menu, people, scopes, selected.ToList(), observedAt, root.IsTruncated);

        void Walk(AccNode node, IReadOnlyList<AccNode> ancestors)
        {
            if ((node.State & 0x8000) != 0) return; // MSAA STATE_SYSTEM_INVISIBLE
            if (node.Role is AccNode.ListItem or "outlineitem")
            {
                var texts = OwnTexts(node);
                var status = texts.LastOrDefault(t => Statuses.Contains(t)) ?? "";
                var organization = menu == MainMenu.Organization
                    ? ancestors.Where(a => a.Role == "outlineitem").Select(DepartmentName).Where(s => s.Length > 0).ToList()
                    : texts.Skip(2).Where(t => !Statuses.Contains(t) && t.Contains('|')).ToList();
                if (texts.Count >= 3 && status.Length > 0 && (menu == MainMenu.Organization ? node.Role == "outlineitem" : organization.Count > 0))
                {
                    rows.Add(new Row(node, new ContactPerson(Key(node), texts[0], texts[1], organization, status), ancestors));
                    return;
                }
            }
            foreach (var child in node.Children) Walk(child, [.. ancestors, node]);
        }
    }

    private static string Key(AccNode node) => node.Path.Length > 0 ? node.Path : "node:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(node);
    private static bool IsSelected(AccNode node) => (node.State & AccNode.StateSelected) != 0;
    private static List<string> OwnTexts(AccNode node) => OwnNodes(node).Where(n => n.Role == AccNode.StaticText)
        .Select(n => n.Name.Trim()).Where(t => t.Length > 0 && !t.StartsWith("icon:", StringComparison.Ordinal)).ToList();
    private static IEnumerable<AccNode> OwnNodes(AccNode node)
    {
        if ((node.State & 0x8000) != 0) yield break;
        yield return node;
        foreach (var child in node.Children.Where(n => n.Role is not ("outlineitem" or AccNode.ListItem)))
            foreach (var part in OwnNodes(child)) yield return part;
    }
    private static string DepartmentName(AccNode node) => OwnTexts(node).FirstOrDefault() ??
        node.Name.Replace("icon: caret-down", "").Replace("icon: caret-right", "").Trim();
    private static List<string> GroupTexts(AccNode node, IReadOnlyList<Row> rows)
    {
        if ((node.State & 0x8000) != 0) return [];
        if (rows.Any(r => ReferenceEquals(r.Node, node))) return [];
        if (node.Role == AccNode.StaticText) return node.Name.Trim().Length > 0 ? [node.Name.Trim()] : [];
        return node.Children.SelectMany(c => GroupTexts(c, rows)).ToList();
    }
    private static List<AccNode> AncestorsOf(AccNode root, AccNode target)
    {
        if (ReferenceEquals(root, target)) return [root];
        foreach (var child in root.Children)
        {
            var found = AncestorsOf(child, target);
            if (found.Count > 0) return [root, .. found];
        }
        return [];
    }

    private static IEnumerable<AccNode> VisibleNodes(AccNode node)
    {
        if ((node.State & 0x8000) != 0) yield break;
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in VisibleNodes(child)) yield return descendant;
    }
}
