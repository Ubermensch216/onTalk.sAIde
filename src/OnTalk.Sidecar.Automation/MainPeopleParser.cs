using OnTalk.Sidecar.Core;
using MainMenu = OnTalk.Sidecar.Core.MainMenu;

namespace OnTalk.Sidecar.Automation;

/// <summary>친구 목록/조직도 트리와 잠깐 표시되는 사람 상세를 읽는다. 선택 가능 상태를 선택으로 오인하지 않는다.</summary>
public static class MainPeopleParser
{
    private static readonly HashSet<string> Statuses = ["온라인", "오프라인", "자리비움", "다른 용무 중", "다른용무중", "회의 중", "회의중", "바쁨"];
    private sealed record Row(AccNode Node, ContactPerson Person, IReadOnlyList<AccNode> Ancestors);

    public static PeopleSnapshot Parse(AccNode root, MainMenu menu, DateTimeOffset observedAt)
    {
        if (WindowContextParser.Parse(root) is not { Surface: OnTalkSurface.Main } context || context.Menu != menu ||
            menu is not (MainMenu.Friends or MainMenu.Organization)) return new(menu, [], [], [], observedAt);
        var rows = new List<Row>();
        Walk(root, []);
        var scopes = new List<ContactScope>();
        // 행 자체의 선택 상태 외에 행 안의 체크된 확인란도 사용자의 선택으로 읽는다.
        // 실제 온톡 조직도는 확인란 체크를 MSAA 상태로 알리지 않고 CSS 클래스 ant-tree-checkbox-checked로만 표시한다.
        // 친구 목록은 체크한 친구의 사진 자리에 체크 표시를 띄우고, 우클릭 메뉴의 단체 대화·단체 쪽지가 이 선택을 쓴다.
        bool IsChosen(AccNode node) => IsSelected(node) || OwnNodes(node).Any(IsCheckedMark);
        // 온톡 5.4.9 친구 목록: 체크한 친구는 사진 옆에 내용 없는 클릭 요소(약 50px 너비)가 하나 더 생긴다.
        bool HasCheckOverlay(Row row) => menu == MainMenu.Friends && row.Ancestors.Count >= 2 &&
            row.Ancestors[^2].Children.Any(c => c.Role == "DIV" && c.HasAction && c.Children.Count == 0 && c.Name.Length == 0 && c.Bounds.Width >= 44);
        var selected = rows.Where(r => IsChosen(r.Node) || HasCheckOverlay(r)).Select(r => r.Person.Key).ToHashSet();
        if (menu == MainMenu.Organization)
        {
            foreach (var node in VisibleNodes(root).Where(n => n.Role == "outlineitem" && !rows.Any(r => ReferenceEquals(r.Node, n))))
            {
                var name = DepartmentName(node);
                if (name.Length == 0) continue;
                var members = rows.Where(r => r.Ancestors.Contains(node)).Select(r => r.Person.Key).ToList();
                var scope = new ContactScope(Key(node), name, ContactScopeKind.Department, members, Selected: IsChosen(node));
                scopes.Add(scope);
                if (scope.Selected) selected.UnionWith(members);
            }
        }
        else
        {
            // 친구 행 안의 확인란은 그 친구의 체크다. 그룹 머리의 확인란만 그룹으로 읽는다.
            var inRows = rows.SelectMany(r => OwnNodes(r.Node)).ToHashSet(ReferenceEqualityComparer.Instance);
            var groupChecks = VisibleNodes(root).Where(n => n.Role == AccNode.CheckBox && !inRows.Contains(n)).ToList();
            bool IsGroupCheck(AccNode n) => groupChecks.Any(c => ReferenceEquals(c, n));
            // 온톡 5.4.9는 그룹 머리와 구성원이 한 목록의 형제다. 이때는 화면 순서상 다음 그룹 머리 전까지가 그 그룹의 구성원이다.
            var ordered = VisibleNodes(root).ToList();
            int Order(AccNode n) => ordered.FindIndex(x => ReferenceEquals(x, n));
            foreach (var (check, at) in groupChecks.Select((c, i) => (c, i)))
            {
                var chain = AncestorsOf(root, check);
                // 같은 체크박스를 가진 그룹의 가장 가까운 구성원 컨테이너. 바깥의 모든 그룹을 묶지 않는다.
                var container = chain.AsEnumerable().Reverse().FirstOrDefault(n =>
                    n.DescendantsAndSelf().Count(IsGroupCheck) == 1 && rows.Any(r => r.Ancestors.Contains(n)));
                var header = chain.AsEnumerable().Reverse().FirstOrDefault(n =>
                    n.DescendantsAndSelf().Count(IsGroupCheck) == 1 && GroupTexts(n, rows).Count > 0);
                var texts = header is null ? [] : GroupTexts(header, rows);
                var name = texts.FirstOrDefault(t => !int.TryParse(t, out _)) ?? "";
                if (name.Length == 0) continue;
                var members = container is null
                    ? rows.Where(r => Order(r.Node) > Order(check) && (at + 1 >= groupChecks.Count || Order(r.Node) < Order(groupChecks[at + 1])))
                        .Select(r => r.Person.Key).ToList()
                    : rows.Where(r => r.Ancestors.Contains(container)).Select(r => r.Person.Key).ToList();
                int? count = texts.Select(t => int.TryParse(t, out var number) ? (int?)number : null).FirstOrDefault(c => c.HasValue);
                var scope = new ContactScope(Key(check), name, ContactScopeKind.Group, members, count,
                    // 5.4.9는 그룹 확인란의 체크를 알리지 않는다. 읽은 구성원이 모두 체크되어 있으면 그룹이 체크된 것으로 본다.
                    IsCheckedMark(check) || (members.Count > 0 && members.All(selected.Contains)));
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
    private static bool IsCheckedMark(AccNode node) =>
        (node.Role == AccNode.CheckBox && (node.State & AccNode.StateChecked) != 0) ||
        node.Class.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(c => c is "ant-tree-checkbox-checked" or "ant-checkbox-checked");
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
