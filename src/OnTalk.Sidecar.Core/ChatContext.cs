using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace OnTalk.Sidecar.Core;

public sealed record ChatMessage(string Key, string Sender, string Time, DateOnly? Date, string Body)
{
    public string Label => $"{Sender} · {(Date is { } date ? date.ToString("yyyy-MM-dd") + " " : "날짜 미확인 · ")}{Time}\n{Body}";
    public override string ToString() => Label;
}

/// <summary>접근성에서 읽은 일부 텍스트. RoomHint는 방 고유 ID가 아니다.</summary>
public sealed record ChatSnapshot(string RoomHint, IReadOnlyList<ChatMessage> Messages, DateTimeOffset ObservedAt, bool Truncated = false)
{
    public string Fingerprint => Hash(string.Join("\n", Messages.Select(m => m.Key)));
    public string Coverage => $"접근성에서 읽은 텍스트 {Messages.Count}건 · {ObservedAt.ToLocalTime():HH:mm:ss}" +
        (Truncated ? " · 읽기 한도에 도달한 일부 범위" : " · 전체 이력 여부 미확인") + "\n방·사람 고유 ID와 파일 내용은 확인하지 못했습니다.";
    public bool Continues(ChatSnapshot other) => RoomHint == other.RoomHint &&
        (Fingerprint == other.Fingerprint || Messages.Any(m => other.Messages.Any(n => n.Key == m.Key)));
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public sealed record ConversationItem(string Key, string Name, string Detail, string Preview)
{
    public string Label => $"{Name} · {Detail}\n목록 미리보기: {Preview}";
}

public sealed record RawChatPoint(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("message")] int Message,
    [property: JsonPropertyName("evidence")] string Evidence);
public sealed record RawChatAnalysis(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("points")] IReadOnlyList<RawChatPoint> Points);
public sealed record ChatPoint(string Kind, string Title, ChatMessage Message, string Evidence);
public sealed record ChatAnalysis(string Summary, IReadOnlyList<ChatPoint> Points, int Dropped);

public static class ChatAnalysisValidator
{
    public static ChatAnalysis Validate(RawChatAnalysis raw, IReadOnlyList<ChatMessage> messages)
    {
        var points = new List<ChatPoint>(); var dropped = 0;
        foreach (var point in raw.Points ?? [])
        {
            if (point is null) { dropped++; continue; }
            var quote = point.Evidence?.Trim() ?? "";
            if (point.Message < 1 || point.Message > messages.Count || quote.Length < 2 ||
                string.IsNullOrWhiteSpace(point.Title) || point.Kind is not ("decision" or "question" or "action") ||
                !messages[point.Message - 1].Body.Contains(quote, StringComparison.Ordinal)) { dropped++; continue; }
            points.Add(new ChatPoint(point.Kind, point.Title.Trim(), messages[point.Message - 1], quote));
        }
        return new ChatAnalysis(raw.Summary ?? "", points.Distinct().ToList(), dropped);
    }
}

public sealed record ChatTaskSource(string RoomHint, string Sender, string Time, DateOnly? Date, string MessageKey,
    string Body, string Evidence, DateTimeOffset ObservedAt)
{
    public string Label => $"대화에서 직접 선택 · {RoomHint}\n{Sender} · {Date?.ToString("yyyy-MM-dd") ?? "날짜 미확인"} {Time}\n" +
        $"근거: {Evidence}\n수집 {ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm} · 방/사람 ID 미확인";
}
