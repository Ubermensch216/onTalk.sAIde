using OnTalk.Sidecar.Core;
using MainMenu = OnTalk.Sidecar.Core.MainMenu;

namespace OnTalk.Sidecar.Automation;

public static class WindowContextParser
{
    public static SidecarContext Parse(AccNode root, NoteWindowContent? note = null)
    {
        if (root.Role != AccNode.Document) return SidecarContext.Unknown;
        var url = root.Value;
        if (IsPage(url, "note"))
        {
            return note?.Kind switch
            {
                NoteWindowKind.Compose => new(OnTalkSurface.NoteCompose, ComposeKind: note.ComposeKind, Title: note.Title),
                NoteWindowKind.Received => new(OnTalkSurface.NoteRead, Title: note.Title),
                _ => SidecarContext.Unknown
            };
        }
        if (IsPage(url, "chat")) return new(OnTalkSurface.Chat);
        if (IsPage(url, "userSearch")) return new(OnTalkSurface.Excluded);
        if (IsPage(url, "alert") || IsPage(url, "onnaraAlerts")) return new(OnTalkSurface.Excluded);
        if (!IsPage(url, "renderer")) return SidecarContext.Unknown;

        // 로그인 화면은 프로필 폴더가 남아 있어도 메인 작업으로 취급하지 않는다.
        var nodes = root.DescendantsAndSelf().ToList();
        if (nodes.Any(n => n.Name.Trim() == "로그인") && nodes.Any(n => n.Name.Trim() == "아이디 저장") &&
            !root.ContainsText("친구") && !root.ContainsText("조직도"))
            return new(OnTalkSurface.Login);

        // 온톡 5.4.3 실화면에서 확인한 해시 경로. 선택 상태가 없는 메뉴도 구분한다.
        var hash = url.IndexOf('#');
        var route = hash >= 0 ? url[(hash + 1)..].Split('?')[0].TrimEnd('/') : "";
        if (route == "/main/1") return new(OnTalkSurface.Main, MainMenu.Friends);
        if (route == "/organizations") return new(OnTalkSurface.Main, MainMenu.Organization);
        if (route == "/chatrooms") return new(OnTalkSurface.Main, MainMenu.Conversations);
        if (route == "/notes") return new(OnTalkSurface.Main, MainMenu.Notes);

        var selectedMenus = root.DescendantsAndSelf()
            .Where(n => n.Role is "pagetab" or "listitem" or "link" or "pushbutton" &&
                        (n.State & (AccNode.StateSelected | AccNode.StatePressed)) != 0)
            .Select(n => MenuOf(n.Name.Trim())).Where(m => m != MainMenu.Unknown).Distinct().ToList();
        if (selectedMenus.Count == 1) return selectedMenus[0] == MainMenu.Excluded
            ? new(OnTalkSurface.Excluded) : new(OnTalkSurface.Main, selectedMenus[0]);
        if (selectedMenus.Count > 1) return new(OnTalkSurface.Main);

        // 사이드바에는 모든 메뉴명이 있다. 본문의 고유 표식이 하나인 경우에만 판별한다.
        var markers = new List<MainMenu>();
        var noteTabs = nodes.Where(n => n.Role == "pagetab" && (n.State & 0x8000) == 0).Select(n => n.Name.Trim()).ToHashSet();
        if (root.StaticTexts().Any(t => t.Trim().StartsWith("나의 그룹", StringComparison.Ordinal))) markers.Add(MainMenu.Friends);
        if (root.ContainsText("타 기관 조회")) markers.Add(MainMenu.Organization);
        if (root.ContainsText("받은 쪽지함") || root.ContainsText("보낸 쪽지함") || root.ContainsText("보관 쪽지함") ||
            (root.ContainsText("받은 쪽지") && root.ContainsText("보낸 쪽지") && root.ContainsText("보관 쪽지")) ||
            (noteTabs.Contains("받은 쪽지") && noteTabs.Contains("보낸 쪽지") && noteTabs.Contains("보관 쪽지"))) markers.Add(MainMenu.Notes);
        return new(OnTalkSurface.Main, markers.Count == 1 ? markers[0] : MainMenu.Unknown);
    }

    private static bool IsPage(string url, string page) =>
        url.Contains($"/windows/{page}/", StringComparison.OrdinalIgnoreCase);

    private static MainMenu MenuOf(string text) => text switch
    {
        "친구" => MainMenu.Friends, "조직도" => MainMenu.Organization, "대화" => MainMenu.Conversations,
        "쪽지" => MainMenu.Notes, "알리미" or "사용자 검색" => MainMenu.Excluded, _ => MainMenu.Unknown
    };
}
