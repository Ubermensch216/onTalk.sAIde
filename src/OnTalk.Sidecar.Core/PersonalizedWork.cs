using System.Text.Json.Serialization;

namespace OnTalk.Sidecar.Core;

public sealed class PersonalizedWork
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TemplateName { get; set; } = "";
    public string Template { get; set; } = "";
    public List<string> Headers { get; set; } = ["항목"];
    public List<PersonalizedWorkItem> Items { get; set; } = [];

    public const string DefaultHeader = "항목";

    /// <summary>서식에 아직 넣지 않은 항목 목록. 모든 항목은 서식에 필수로 들어가야 한다.</summary>
    public IReadOnlyList<string> MissingInTemplate() => NotePersonalization.MissingFields(Template, Headers);

    /// <summary>"항목", "항목2", … 중 아직 쓰지 않은 첫 이름.</summary>
    public string NextHeaderName()
    {
        if (!Headers.Contains(DefaultHeader)) return DefaultHeader;
        for (var n = 2; ; n++)
            if (!Headers.Contains(DefaultHeader + n)) return DefaultHeader + n;
    }

    /// <summary>항목을 추가한다. 이름이 잘못되었으면 이유를 돌려주고 바꾸지 않는다.</summary>
    public string? AddHeader(string name)
    {
        if (NotePersonalization.ValidateHeaderName(name, Headers) is { } error) return error;
        name = name.Trim();
        Headers.Add(name);
        foreach (var item in Items) item.Values.TryAdd(name, "");
        return null;
    }

    /// <summary>항목 제목을 바꾸고 사람별 값 키를 옮긴다. 서식의 변수는 호출한 쪽에서 함께 바꾼다.</summary>
    public string? RenameHeader(string from, string to)
    {
        if (!Headers.Contains(from)) return "바꿀 항목을 찾지 못했습니다.";
        if (NotePersonalization.ValidateHeaderName(to, Headers, from) is { } error) return error;
        to = to.Trim();
        if (to == from) return null;
        Headers[Headers.IndexOf(from)] = to;
        foreach (var item in Items)
        {
            if (item.Values.Remove(from, out var value)) item.Values[to] = value;
            else item.Values.TryAdd(to, "");
        }
        return null;
    }

    /// <summary>
    /// 서식을 불러온 뒤 항목 구성을 서식에 맞춘다. 서식에 쓰인 변수는 항목으로 추가하고,
    /// 서식에 없으면서 입력한 값도 없는 항목은 지운다(값이 들어 있는 항목은 데이터 보호를 위해 남긴다). 항목은 최소 하나를 남긴다.
    /// </summary>
    public (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) AlignHeadersTo(string template)
    {
        var used = NotePersonalization.ExtractVariables(template);
        var added = used.Where(v => !Headers.Contains(v)).ToList();
        foreach (var name in added)
        {
            Headers.Add(name);
            foreach (var item in Items) item.Values.TryAdd(name, "");
        }
        var removed = new List<string>();
        foreach (var header in Headers.Where(h => !used.Contains(h) && Items.All(i => string.IsNullOrWhiteSpace(i[h]))).ToList())
            if (RemoveHeader(header) is null) removed.Add(header);
        return (added, removed);
    }

    /// <summary>항목을 지운다. 하나는 반드시 남아야 한다.</summary>
    public string? RemoveHeader(string name)
    {
        if (!Headers.Contains(name)) return "지울 항목을 찾지 못했습니다.";
        if (Headers.Count <= 1) return "항목은 최소 하나가 있어야 합니다.";
        Headers.Remove(name);
        foreach (var item in Items) item.Values.Remove(name);
        return null;
    }

    /// <summary>사용자가 ‘임시 저장’으로 저장한 작업인지 기록한다. 발송 과정에서 남은 저장과 구분하기 위한 표시다.</summary>
    public bool Draft { get; set; }

    /// <summary>저장 목록에 ‘임시 저장’ 태그를 붙일 작업: 임시 저장했고 아직 발송을 시도하지 않은 것.</summary>
    [JsonIgnore]
    public bool IsDraft => Draft && Items.All(i => !i.Attempted);

    /// <summary>모든 대상의 발송(또는 예약)이 끝난 작업. 저장 목록에 ‘발송 완료’ 태그가 붙는다.</summary>
    [JsonIgnore]
    public bool IsSent => Items.Count > 0 && Items.All(i => i.Completed);

    /// <summary>발송을 시도했지만 아직 모두 끝나지 않은 작업(보류·결과 불명·미진행 대상 포함). ‘일부 발송’ 태그가 붙는다.</summary>
    [JsonIgnore]
    public bool IsPartlySent => !IsSent && Items.Any(i => i.Attempted);

    /// <summary>발송 기록도 임시 저장 표시도 없는 저장 작업. 이전 버전이 창을 닫을 때 자동 저장한 것이 여기에 해당한다.</summary>
    [JsonIgnore]
    public bool IsUnmarked => !Draft && Items.All(i => !i.Attempted);

    /// <summary>저장 목록의 제목 줄. 서식 제목, 없으면 서식 첫머리.</summary>
    [JsonIgnore]
    public string Headline
    {
        get
        {
            if (TemplateName.Trim().Length > 0) return TemplateName.Trim();
            var start = Template.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (start.Length > 25) start = start[..25] + "…";
            return start.Length == 0 ? "(서식 제목 없음)" : "(제목 없음) " + start;
        }
    }

    /// <summary>저장 목록의 설명 줄: 발송 대상 인원수.</summary>
    [JsonIgnore]
    public string Detail
    {
        get
        {
            var sent = Items.Count(i => i.Attempted);
            return $"발송 대상 {Items.Count}명" + (sent > 0 ? $" · 발송 시도 {sent}명" : "");
        }
    }

    public override string ToString() => Headline + " · " + Detail;
}

public sealed class PersonalizedWorkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int DispatchRevision { get; set; }
    public ContactPerson Person { get; set; } = new("", "", "", []);
    public string Name => Person.Name;
    public string Organization => string.Join(" · ", Person.Organization);
    public string Title => Person.Title;
    public Dictionary<string, string> Values { get; set; } = [];
    public string Body { get; set; } = "";
    public string Status { get; set; } = "미준비";
    public long? DispatchId { get; set; }
    public bool Attempted => DispatchId is not null;

    /// <summary>발송 또는 예약이 정상으로 끝난 대상.</summary>
    [JsonIgnore]
    public bool Completed => Attempted && (Status.StartsWith("발송 완료") || Status.StartsWith("예약 완료"));

    [JsonIgnore]
    public string this[string header]
    {
        get => Values.GetValueOrDefault(header, "");
        set { if (!Attempted) Values[header] = value ?? ""; }
    }
}

/// <summary>전송 시도 기록. 전송 단추를 누르기 전에 시작을 남기고, 결과를 확인한 뒤 끝을 남긴다.</summary>
public interface IDispatchLog
{
    long BeginDispatch(IReadOnlyList<string> recipientNames, string body, DateTime? reserveAt, DateTimeOffset at);
    void FinishDispatch(long id, DispatchStatus status, string detail);
}

public interface IPersonalizedDispatchStore : IDispatchLog
{
    void SavePersonalizedWork(PersonalizedWork work);
}
