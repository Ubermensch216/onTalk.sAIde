namespace OnTalk.Sidecar.Core;

public enum OnTalkSurface { Unknown, Main, NoteRead, NoteCompose, Chat, Auxiliary, Excluded, Login }
public enum MainMenu { Unknown, Friends, Organization, Conversations, Notes, Excluded }
public enum NoteComposeKind { Unknown, New, Reply, ReplyAll, Forward }

/// <summary>화면에서 확인한 문맥. 창 핸들과 접근성 노드는 Automation에만 둔다.</summary>
public sealed record SidecarContext(OnTalkSurface Surface, MainMenu Menu = MainMenu.Unknown,
    NoteComposeKind ComposeKind = NoteComposeKind.Unknown, string Title = "")
{
    public static SidecarContext Unknown { get; } = new(OnTalkSurface.Unknown);
}

public sealed record ContextGuide(string Title, string Purpose, string Tools, string Scope);

public static class SidecarPolicy
{
    public static ContextGuide Describe(SidecarContext context) => context.Surface switch
    {
        OnTalkSurface.NoteCompose => new(ComposeTitle(context.ComposeKind), ComposePurpose(context.ComposeKind),
            "초안 작성 · 문장 다듬기 · 제안 비교 · 되돌리기",
            "현재 작성창의 글과 원문 인용만 사용합니다. 반영 전에 온톡의 최신 본문을 다시 확인합니다."),
        OnTalkSurface.NoteRead => new(context.Title.Length > 0 ? context.Title : "쪽지 읽기", "내용과 요청을 이해하고 원문 근거를 확인하세요.",
            "요약 · 요청/기한 · 원문 · 첨부 · 연결 업무", "현재 열린 쪽지만 표시합니다."),
        OnTalkSurface.Chat => new("대화 지원", "최근 흐름을 파악하고 짧은 답변을 준비하세요.",
            "원문 선택 · 흐름/후보 정리 · 짧은 답변 · 초안 복사 · 근거 업무", "읽힌 텍스트 범위를 직접 선택합니다. 새 자료는 다시 불러옵니다. 온톡에서 실제 상대와 초안을 확인한 뒤 전송은 직접 합니다."),
        OnTalkSurface.Main => context.Menu switch
        {
            MainMenu.Friends => new("Home · 친구 소통", "그룹과 친구별로 보낸 쪽지 이력·최근 대화 요약·이어갈 업무를 확인하세요.",
                "보낸 쪽지 이력 · 최근 대화 정리 · 이어서 확인할 업무", "사이드카 발송 기록과 친구에게 직접 연결한 자료만 집계합니다. 관찰한 친구·그룹과 전체 온톡 이력을 구분합니다."),
            MainMenu.Organization => new("조직도 · 선택 직원에게 쪽지", "온톡 조직도에서 선택한 직원에게 쪽지를 보내세요.",
                "선택 직원 확인 · 같은 쪽지 · 개인별 쪽지 · 예약 전송", "화면에서 읽은 선택 직원만 대상으로 합니다. 접힌 부서의 구성원은 포함하지 않으며 수신자는 작성창에서 다시 확인합니다."),
            MainMenu.Conversations => new("대화 · 목록", "참여할 대화를 찾고 대화창에서 흐름과 답변을 준비하세요.",
                "대화 준비 · 보관 업무 확인", "목록 미리보기만으로 내용을 요약하지 않습니다. 대화창을 열면 대화 지원으로 전환합니다."),
            MainMenu.Notes => new("쪽지 · 목록", "보관한 자료에서 필요한 쪽지를 찾고 새 쪽지를 준비하세요.",
                "보관 자료 검색 · 연결 업무 · 새 쪽지", "이 PC에서 읽어 보관한 받은 쪽지만 검색됩니다. 상세 쪽지를 열면 읽기/작성 지원으로 전환합니다."),
            _ => new("메인 화면 · 메뉴 확인 필요", "현재 메뉴를 확인하지 못했습니다. 아래에서 메뉴를 지정할 수 있습니다.",
                "수동 메뉴 지정", "수동 지정은 사이드카 화면만 바꿉니다. 온톡 메뉴나 선택 대상을 변경하지 않습니다.")
        },
        OnTalkSurface.Auxiliary => new("작업 보조 창", "온톡의 보조 창을 마친 뒤 원래 작업으로 돌아가세요.", "", "이 화면에서는 본문을 반영하지 않습니다."),
        OnTalkSurface.Login => new("온톡 로그인 대기", "온톡에서 로그인하세요.", "", "로그인 화면의 입력값을 읽거나 저장하지 않습니다."),
        OnTalkSurface.Excluded => new("공통 도구", "현재 화면에는 전용 사이드카 지원을 제공하지 않습니다.", "", "사용자 검색·알리미는 이번 지원 범위에서 제외합니다."),
        _ => new("현재 화면 확인 중", "현재 온톡 창의 작업을 확인하지 못했습니다.", "", "다른 창의 쪽지나 초안을 대신 표시하지 않습니다.")
    };

    public static string ComposeTitle(NoteComposeKind kind) => kind switch
    {
        NoteComposeKind.New => "새 쪽지 작성", NoteComposeKind.Reply => "답장 작성",
        NoteComposeKind.ReplyAll => "전체답장 작성", NoteComposeKind.Forward => "전달 작성", _ => "쪽지 작성"
    };

    public static string ComposePurpose(NoteComposeKind kind) => kind switch
    {
        NoteComposeKind.New => "목적·핵심 사실·요청사항을 정리해 새 내용을 쓰세요.",
        NoteComposeKind.Reply => "원문의 요청과 질문에 빠짐없이 답하세요.",
        NoteComposeKind.ReplyAll => "모두에게 필요한 응답을 쓰고 실제 전체 수신자를 확인하세요.",
        NoteComposeKind.Forward => "전달 이유와 수신자가 할 일을 설명하고 원문·첨부 포함 여부를 확인하세요.",
        _ => "현재 글을 작성하고 다듬으세요. 작성 종류와 원문 연결은 추정하지 않습니다."
    };
}

/// <summary>초안은 창 세션·작성 종류별로 분리한다. 계정 저장소가 계정 범위를 분리한다.</summary>
public static class ContextDrafts
{
    public static string Slot(Guid session, NoteComposeKind kind) => $"compose:{session:N}:{kind}";
}

/// <summary>비동기 제안이 다른 대상 또는 편집된 글에 수락되지 않도록 버전을 비교한다.</summary>
public readonly record struct DraftRevision(Guid Session, long Version)
{
    public bool Matches(DraftRevision current) => this == current;
}
