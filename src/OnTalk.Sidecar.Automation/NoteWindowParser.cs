using System.Text;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.Automation;

public enum NoteWindowKind
{
    /// <summary>쪽지 페이지지만 종류를 판별하지 못했다.</summary>
    Unknown,
    Received,
    Compose,
}

/// <param name="Note">받은 쪽지를 끝까지 해석했을 때만 값이 있다.</param>
/// <param name="Compose">작성 창을 끝까지 해석했을 때만 값이 있다.</param>
public sealed record NoteWindowContent(NoteWindowKind Kind, string Title, ReceivedNote? Note, ComposeState? Compose = null)
{
    public NoteComposeKind ComposeKind { get; init; }
}

/// <summary>온톡 작성 창(새 쪽지·답장·전달)의 현재 상태.</summary>
/// <param name="Recipients">수신자 이름. 온톡 작성 창은 이름만 보여 주므로 동명이인은 구분되지 않는다.</param>
/// <param name="UsedBytes">온톡이 표시한 본문 바이트 수.</param>
/// <param name="DialogOpen">예약 설정 같은 창 안 대화상자가 떠 있어 본문을 조작할 수 없는 상태.</param>
/// <param name="BodyBounds">본문 입력창의 화면 위치.</param>
/// <param name="Reservation">예약 전송이 걸려 있으면 그 시각(한국 시각). 즉시 전송이면 null.</param>
public sealed record ComposeState(
    IReadOnlyList<string> Recipients,
    string Body,
    int UsedBytes,
    int LimitBytes,
    bool SmsChecked,
    bool DialogOpen,
    AccBounds BodyBounds,
    DateTime? Reservation = null);

/// <summary>
/// 온톡 5.4.3 쪽지 창(windows/note/app.html)의 접근성 트리를 해석한다.
/// 화면 문구와 요소 배치에 의존하므로 온톡이 바뀌면 이 파일을 고친다.
/// </summary>
public static class NoteWindowParser
{
    private const string ReceivedTitle = "받은 쪽지";
    private static readonly string[] ModeToggles = ["싱글창", "멀티창"];
    // 수신자가 없을 때 이름 자리에 나오는 안내 문구.
    private const string NoRecipientHint = "받는 사람을 추가해주세요.";

    public static bool IsNotePage(AccNode root) =>
        root.Role == AccNode.Document && root.Value.Contains("/windows/note/", StringComparison.OrdinalIgnoreCase);

    public static NoteWindowContent Parse(AccNode root, DateTimeOffset now)
    {
        var title = root.StaticTexts().FirstOrDefault(t => !ModeToggles.Contains(t)) ?? "";
        if (root.StaticTexts().Any(IsByteCounter))
        {
            return new NoteWindowContent(NoteWindowKind.Compose, title, null, ParseCompose(root))
            {
                ComposeKind = title.Trim() switch
                {
                    "새 쪽지" => NoteComposeKind.New, "답장" => NoteComposeKind.Reply,
                    "전체답장" or "전체 답장" => NoteComposeKind.ReplyAll,
                    "전달" => NoteComposeKind.Forward, _ => NoteComposeKind.Unknown
                }
            };
        }
        if (title != ReceivedTitle)
        {
            return new NoteWindowContent(NoteWindowKind.Unknown, title, null);
        }
        return new NoteWindowContent(NoteWindowKind.Received, title, ParseReceived(root, now));
    }

    private static ComposeState? ParseCompose(AccNode root)
    {
        var nodes = root.DescendantsAndSelf().ToList();
        // 첨부 "추가" 단추도 입력 요소로 나오므로 안내 문구가 있는 것을 본문으로 본다.
        var bodyIndex = nodes.FindIndex(n => n.Role == AccNode.EditableText && n.Name.Length > 0);
        var counter = nodes.FirstOrDefault(n => n.Role == AccNode.StaticText && IsByteCounter(n.Name));
        var addIndex = nodes.FindIndex(n => n.Role == AccNode.StaticText && n.Name == "추가");
        if (bodyIndex < 0 || counter is null || addIndex < 0 || addIndex > bodyIndex)
        {
            return null;
        }
        var numbers = counter.Name[..^" Bytes".Length].Split('/');
        if (numbers.Length != 2 || !int.TryParse(numbers[0], out var used) || !int.TryParse(numbers[1], out var limit))
        {
            return null;
        }
        // 수신자 이름은 수신 "추가" 단추와 본문 입력창 사이에 나온다.
        var recipients = nodes.Skip(addIndex + 1).Take(bodyIndex - addIndex - 1)
            .Where(n => n.Role == AccNode.StaticText && n.Name.Trim().Length > 0 && n.Name != NoRecipientHint)
            .Select(n => n.Name.Trim()).ToList();
        var body = nodes[bodyIndex];
        return new ComposeState(
            recipients,
            body.Value.Replace("\r\n", "\n"),
            used,
            limit,
            nodes.Any(n => n.Role == AccNode.CheckBox && n.Name == "SMS 발송" && (n.State & AccNode.StateChecked) != 0),
            nodes.Any(n => n.Role == AccNode.StaticText && n.Name == "예약 전송 일시"),
            body.Bounds,
            nodes.Where(n => n.Role == AccNode.StaticText).Select(n => ReservationTime.ParseCompose(n.Name))
                .FirstOrDefault(t => t is not null));
    }

    private static ReceivedNote? ParseReceived(AccNode root, DateTimeOffset now)
    {
        // 본문 영역은 [머리글, 본문, (첨부), 버튼줄] 순서의 자식을 가진다. 버튼줄을 기준으로 찾는다.
        var container = FindParentOfButtonRow(root);
        if (container is null || container.Children.Count < 3)
        {
            return null;
        }
        var header = container.Children[0];
        var body = container.Children[1];
        var attachments = container.Children.Skip(2).FirstOrDefault(c => c.ContainsText("첨부 파일"));

        var headerTexts = header.StaticTexts().Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var timeIndex = headerTexts.FindIndex(KoreanNoteTime.LooksLikeTime);
        if (headerTexts.Count == 0 || timeIndex < 1)
        {
            return null;
        }
        var sentAtText = headerTexts[timeIndex];
        var recipientLabel = headerTexts.IndexOf("수신");
        int? recipientCount = recipientLabel >= 0 && recipientLabel + 1 < headerTexts.Count &&
                              int.TryParse(headerTexts[recipientLabel + 1], out var n)
            ? n
            : null;

        return new ReceivedNote(
            SenderName: headerTexts[0],
            SenderDetail: string.Join(" ", headerTexts.Skip(1).Take(timeIndex - 1)),
            SentAtText: sentAtText,
            SentAt: KoreanNoteTime.Parse(sentAtText, now),
            RecipientCount: recipientCount,
            Body: ReadText(body),
            AttachmentNames: attachments is null ? [] : ReadAttachmentNames(attachments));
    }

    private static AccNode? FindParentOfButtonRow(AccNode node)
    {
        static bool IsButtonRow(AccNode n) => n.ContainsText("답장") && n.ContainsText("삭제");

        if (!IsButtonRow(node))
        {
            return null;
        }
        foreach (var child in node.Children)
        {
            if (!IsButtonRow(child))
            {
                continue;
            }
            // 자식이 버튼을 모두 담고 있고 그 아래로는 더 내려가지 않으면 그 자식이 버튼줄이다.
            return child.Children.Any(IsButtonRow) ? FindParentOfButtonRow(child) : node;
        }
        return null;
    }

    private static string ReadText(AccNode node)
    {
        var sb = new StringBuilder();
        foreach (var n in node.DescendantsAndSelf())
        {
            if (n.Role == AccNode.StaticText)
            {
                sb.Append(n.Name);
            }
            else if (n.Role == AccNode.Whitespace)
            {
                sb.Append('\n');
            }
        }
        return sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
    }

    // 파일 한 줄은 "파일명, 열기, 저장" 순서다.
    private static List<string> ReadAttachmentNames(AccNode node)
    {
        var texts = node.StaticTexts().ToList();
        var names = new List<string>();
        for (var i = 1; i < texts.Count; i++)
        {
            if (texts[i] == "열기" && i + 1 < texts.Count && texts[i + 1] == "저장")
            {
                names.Add(texts[i - 1]);
            }
        }
        return names;
    }

    private static bool IsByteCounter(string text) =>
        text.EndsWith(" Bytes", StringComparison.Ordinal) && text.Contains('/') && char.IsDigit(text[0]);
}
