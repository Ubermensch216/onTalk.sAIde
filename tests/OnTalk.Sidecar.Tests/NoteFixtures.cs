using OnTalk.Sidecar.Automation;

namespace OnTalk.Sidecar.Tests;

/// <summary>온톡 5.4.3에서 관측한 쪽지 창 구조를 합성 내용으로 재현한다.</summary>
internal static class NoteFixtures
{
    public const string NoteUrl = "file:///C:/Program%20Files/OnTalk/resources/app.asar/app/windows/note/app.html?note#/note";

    public static AccNode Text(string s) => new(AccNode.StaticText, s, "", 0, []);

    public static AccNode Div(params AccNode[] children) => new("DIV", "", "", 0, children);

    public static AccNode Break() => new(AccNode.Whitespace, "\n", "", 0, []);

    public static AccNode Document(string url, params AccNode[] children) =>
        new(AccNode.Document, "온톡", url, 0, [new AccNode("BODY", "", "", 0, [Div(children)])]);

    public static AccNode Received(string body, params string[] attachments)
    {
        var header = Div(Div(
            Div(),
            Div(Text("홍길동"), Text("지방행정주사/주무관 │ 총무과"), Break(), Text("10월 2일 오후 07:51")),
            Div(Text("수신"), Div(Text("3")))));
        var buttons = Div(
            Div(Text("삭제")), Div(Text("인쇄")), Div(Text("전체답장")), Div(Text("답장")), Div(Text("보관")),
            Div(Text("전달")));
        var parts = new List<AccNode> { header, Div(Text(body)) };
        if (attachments.Length > 0)
        {
            var rows = attachments.Select(a => Div(Div(Text(a)), Div(Div(Text("열기")), Div(Text("저장")), Div())));
            parts.Add(Div(Div(), Div(
                Div(Text("첨부 파일")),
                Div(Text("첨부파일은 7일 이후 자동 삭제 됩니다.")),
                Div(Text(attachments.Length.ToString())),
                Div(rows.ToArray()))));
        }
        parts.Add(buttons);
        return Document(NoteUrl,
            Div(Div(Div(Text("싱글창")), Div(Text("멀티창")))),
            Div(Text("받은 쪽지")),
            Div(Div(Div(parts.ToArray()))));
    }

    /// <summary>온톡 5.4.3 새 쪽지 창에서 관측한 첨부 영역(첨부 파일 · 개수 · 추가 · 파일 이름들 · 안내)을 재현한다.</summary>
    public static AccNode ComposeWithAttachments(string title, params string[] files)
    {
        var attach = new List<AccNode>
        {
            Div(Text("첨부 파일")),
        };
        if (files.Length > 0) attach.Add(Div(Text(files.Length.ToString())));
        attach.Add(new AccNode(AccNode.EditableText, "", "", 0, [Text("추가")]));
        attach.Add(files.Length == 0
            ? Div(Text("첨부파일을 추가해주세요."))
            : Div(files.Select(f => Div(Div(Text(f)), Div())).ToArray()));
        return Document(NoteUrl,
            Div(Text(title)),
            Div(Div(
                Div(Text("수신"), Div(Text("1")), Div(Text("추가"))),
                Div(Div(Text("김철수"))),
                new AccNode(AccNode.EditableText, "쪽지 내용을 입력해주세요.", "", 0, []),
                Div(Text("0/4000 Bytes")),
                Div(Div(attach.ToArray()),
                    Div(Text("* 첨부파일 용량은 최대 20MB, 최대 10개 발송 가능 (exe파일 첨부 불가)")),
                    Div(Div(Div(Text("예약 전송")), Div(Text("설정"))))),
                Div(Div(Text("전송")), Div(Text("(Alt+S)"))))));
    }

    public static AccNode Compose(string title) => Document(NoteUrl,
        Div(Text(title)),
        Div(Div(
            Div(Text("수신"), Div(Text("1")), Div(Text("추가"))),
            Div(Div(Text("김철수"))),
            new AccNode(AccNode.EditableText, "쪽지 내용을 입력해주세요.", "", 0, []),
            Div(Text("0/4000 Bytes")),
            Div(Div(Text("전송")), Div(Text("(Alt+S)"))))));
}
