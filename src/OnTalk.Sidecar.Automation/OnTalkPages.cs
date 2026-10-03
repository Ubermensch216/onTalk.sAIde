using System.Text.RegularExpressions;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

/// <summary>"받는 사람 추가" 창의 검색 결과 한 줄.</summary>
/// <param name="Node">행을 누를 때 쓰는 요소.</param>
public sealed record PersonRow(string Name, string Title, IReadOnlyList<string> Details, AccNode Node)
{
    public Recipient ToRecipient() => new(Name, Title, Details);

    public bool SamePerson(PersonRow other) =>
        Name == other.Name && Title == other.Title && Details.SequenceEqual(other.Details);
}

/// <summary>
/// 온톡 5.4.3의 "받는 사람 추가" 창(windows/userSearch)과 예약 설정 대화상자의 접근성 트리 해석.
/// 화면 배치에 의존하므로 온톡이 바뀌면 이 파일을 고친다.
/// </summary>
public static partial class OnTalkPages
{
    public static bool IsUserSearchPage(AccNode root) =>
        root.Role == AccNode.Document && root.Value.Contains("/windows/userSearch/", StringComparison.OrdinalIgnoreCase);

    public static AccNode? SearchBox(AccNode root) =>
        root.DescendantsAndSelf().FirstOrDefault(n => n.Role == AccNode.EditableText && n.Name.Contains("검색"));

    /// <summary>
    /// 검색 결과 행. 행은 [사진, 이름·직급, 상태, 세부 정보, …] 순서의 자식을 가진다.
    /// </summary>
    public static List<PersonRow> PersonRows(AccNode root)
    {
        var rows = new List<PersonRow>();
        foreach (var node in root.DescendantsAndSelf())
        {
            // 사진 칸은 그림을 직접 자식으로 가진다. 행들을 감싼 바깥 요소와 구분된다.
            if (node.Children.Count < 4 || !node.Children[0].Children.Any(n => n.Role == AccNode.Graphic))
            {
                continue;
            }
            var head = Texts(node.Children[1]);
            if (head.Count == 0)
            {
                continue;
            }
            rows.Add(new PersonRow(head[0], string.Join(" ", head.Skip(1)), Texts(node.Children[3]), node));
        }
        return rows;

        static List<string> Texts(AccNode n) =>
            n.StaticTexts().Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
    }

    [GeneratedRegex(@"^(\d+)명 선택$")]
    private static partial Regex SelectedPattern();

    /// <summary>창 아래에 표시되는 "n명 선택"의 n. 아무도 고르지 않았으면 0.</summary>
    public static int SelectedCount(AccNode root) =>
        root.StaticTexts().Select(t => SelectedPattern().Match(t.Trim())).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).FirstOrDefault();

    [GeneratedRegex(@"^(\d{4})년\s*(\d{1,2})월$")]
    private static partial Regex MonthPattern();

    /// <summary>예약 대화상자가 떠 있으면 그 부분만 돌려준다.</summary>
    public static AccNode? ReservationDialog(AccNode root) =>
        root.DescendantsAndSelf().LastOrDefault(n => n.ContainsText("예약 전송 일시") && n.ButtonLabeled("확인") is not null &&
                                                   !n.ContainsText("첨부 파일"));

    /// <summary>대화상자 위쪽에 글자로 표시된 현재 선택 일시.</summary>
    public static DateTime? ReservationDialogValue(AccNode dialog)
    {
        var texts = dialog.StaticTexts().ToList();
        var label = texts.IndexOf("예약 전송 일시");
        return label >= 0 && label + 3 < texts.Count
            ? ReservationTime.ParseDialog(string.Concat(texts.Skip(label + 1).Take(3)))
            : null;
    }

    /// <summary>달력이 보여 주는 달과 이전·다음 달 단추.</summary>
    public static (int Year, int Month, AccNode? Previous, AccNode? Next)? CalendarMonth(AccNode dialog)
    {
        foreach (var parent in dialog.DescendantsAndSelf())
        {
            var index = parent.Children.ToList().FindIndex(c => c.Role == AccNode.StaticText && MonthPattern().IsMatch(c.Name.Trim()));
            if (index < 0)
            {
                continue;
            }
            var m = MonthPattern().Match(parent.Children[index].Name.Trim());
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                parent.Children.ElementAtOrDefault(index - 1), parent.Children.ElementAtOrDefault(index + 1));
        }
        return null;
    }

    /// <summary>
    /// 달력에서 그 달 <paramref name="day"/>일 칸. 달력에는 앞뒤 달의 날짜도 같은 숫자로 섞여 있으므로
    /// 처음 나오는 "01"부터를 이번 달로 센다.
    /// </summary>
    public static AccNode? DayCell(AccNode dialog, int year, int month, int day)
    {
        var cells = dialog.DescendantsAndSelf().Where(n => n.Role == AccNode.Cell).ToList();
        var first = cells.FindIndex(c => c.Name.Trim() == "01");
        var index = first + day - 1;
        return first >= 0 && day >= 1 && day <= DateTime.DaysInMonth(year, month) && index < cells.Count &&
               int.TryParse(cells[index].Name.Trim(), out var shown) && shown == day
            ? cells[index]
            : null;
    }

    /// <summary>오전/오후, 시, 분 선택 상자. 화면 순서대로다.</summary>
    public static IReadOnlyList<AccNode> TimeCombos(AccNode dialog) =>
        dialog.DescendantsAndSelf().Where(n => n.Role == AccNode.ComboBox).ToList();
}
