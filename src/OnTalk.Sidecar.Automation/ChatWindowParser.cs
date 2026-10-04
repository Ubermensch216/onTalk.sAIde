using System.Text.RegularExpressions;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

/// <summary>온톡 5.4.3에서 관찰한 날짜→시각→텍스트 순서만 사용한다. 입력·숨긴 요소는 읽지 않는다.</summary>
public static partial class ChatWindowParser
{
    public static ChatSnapshot? Parse(AccNode root, DateTimeOffset now)
    {
        if (WindowContextParser.Parse(root).Surface != OnTalkSurface.Chat) return null;
        var nodes = Visible(root).Where(n => n.Role is AccNode.StaticText or AccNode.Graphic or "pushbutton").ToList();
        var firstDate = nodes.FindIndex(n => DatePattern().IsMatch(n.Name.Trim()));
        if (firstDate < 0) return new ChatSnapshot("대화 상대 미확인", [], now, root.IsTruncated);
        var header = nodes.Take(firstDate).Where(n => n.Role == AccNode.StaticText).Select(n => n.Name.Trim()).Where(t => t.Length > 0 &&
            t is not ("싱글창" or "멀티창" or "온라인" or "오프라인" or "자리비움")).ToList();
        var room = string.Join(" · ", header.Take(4));
        var messages = new List<ChatMessage>(); DateOnly? date = null;
        var pending = new List<string>(); var occurrences = new Dictionary<string, int>();
        for (var i = firstDate; i < nodes.Count; i++)
        {
            var text = nodes[i].Name.Trim();
            if (nodes[i].Role != AccNode.StaticText) continue;
            if (DatePattern().Match(text) is { Success: true } dm)
            {
                date = DateOnly.TryParse($"{dm.Groups[1].Value}-{dm.Groups[2].Value}-{dm.Groups[3].Value}", out var parsed) ? parsed : null;
                pending.Clear(); continue;
            }
            if (!TimePattern().IsMatch(text)) { if (text.Length > 0) pending.Add(text); continue; }
            if (i + 1 >= nodes.Count) break;
            var bodyNode = nodes[i + 1]; var body = bodyNode.Name.Trim();
            // 입력 도구나 다음 날짜/시각은 본문으로 만들지 않는다. 그래픽 뒤의 파일명도 일반 글로 간주하지 않는다.
            if (bodyNode.Role != AccNode.StaticText || body.Length == 0 || body is "전송" or "파일 선택" || DatePattern().IsMatch(body) || TimePattern().IsMatch(body)) { pending.Clear(); continue; }
            var sender = pending.Count == 2 && (pending[1].Contains('│') || pending[1].Contains('|'))
                ? pending[0] : "발신자 미확인";
            var stem = ChatSnapshot.Hash($"{date}:{text}:{sender}:{body}");
            var occurrence = occurrences.GetValueOrDefault(stem); occurrences[stem] = occurrence + 1;
            messages.Add(new ChatMessage($"{stem}:{occurrence}", sender, text, date, body));
            pending.Clear(); i++;
        }
        return new ChatSnapshot(room.Length > 0 ? room : "대화 상대 미확인", messages, now, root.IsTruncated);
    }

    public static IReadOnlyList<ConversationItem> ParseList(AccNode root)
    {
        if (WindowContextParser.Parse(root) is not { Surface: OnTalkSurface.Main, Menu: Core.MainMenu.Conversations }) return [];
        // 대화 목록의 LIST는 사람/방 행 하나와 미리보기 하나를 가진다. 사이드바와 자기 프로필은 제외한다.
        var result = new List<ConversationItem>();
        foreach (var list in Visible(root).Where(n => n.Role == "list"))
        {
            var rows = list.Children.Where(n => n.Role == AccNode.ListItem && (n.State & 0x8000) == 0).ToList();
            var preview = list.Children.Where(n => n.Role is AccNode.StaticText or AccNode.ContentText)
                .Select(n => string.Join("\n", Visible(n).Where(c => c.Role == AccNode.StaticText).Select(c => c.Name.Trim()).Where(t => t.Length > 0)))
                .Where(t => t.Length > 0).ToList();
            if (rows.Count != 1 || preview.Count != 1) continue;
            var labels = Visible(rows[0]).Where(n => n.Role == AccNode.StaticText).Select(n => n.Name.Trim()).Where(t => t.Length > 0).ToList();
            if (labels.Count != 2) continue;
            result.Add(new ConversationItem(rows[0].Path, labels[0], labels[1], preview[0]));
        }
        return result;
    }

    private static IEnumerable<AccNode> Visible(AccNode node)
    {
        if ((node.State & 0x8000) != 0 || node.Role is AccNode.EditableText or "tooltip" or "link") yield break;
        yield return node;
        foreach (var child in node.Children) foreach (var next in Visible(child)) yield return next;
    }
    [GeneratedRegex(@"^(\d{4})년\s*(\d{1,2})월\s*(\d{1,2})일(?:\s.*)?$")] private static partial Regex DatePattern();
    [GeneratedRegex(@"^(오전|오후)\s*(?:0?[1-9]|1[0-2]):[0-5]\d$")] private static partial Regex TimePattern();
}
