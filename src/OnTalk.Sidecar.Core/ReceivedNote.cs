using System.Security.Cryptography;
using System.Text;

namespace OnTalk.Sidecar.Core;

/// <summary>온톡 받은 쪽지 창에서 읽은 한 건.</summary>
/// <param name="SentAtText">화면에 표시된 시각 원문. 연도가 없을 수 있다.</param>
/// <param name="SentAt">해석한 시각. 해석하지 못하면 null.</param>
public sealed record ReceivedNote(
    string SenderName,
    string SenderDetail,
    string SentAtText,
    NoteTime? SentAt,
    int? RecipientCount,
    string Body,
    IReadOnlyList<string> AttachmentNames)
{
    /// <summary>
    /// 온톡이 쪽지 고유번호를 화면에 내놓지 않아 발신자·시각·본문으로 같은 쪽지를 판별한다.
    /// 같은 사람이 같은 분에 같은 본문을 두 번 보내면 한 건으로 합쳐진다.
    /// </summary>
    public string Key
    {
        get
        {
            var time = SentAt?.Value.ToString("O") ?? SentAtText;
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{SenderName}\n{time}\n{Body}"));
            return Convert.ToHexString(bytes);
        }
    }
}

/// <param name="YearInferred">원문에 연도가 없어 수집 시점 기준으로 추정했는지.</param>
public sealed record NoteTime(DateTimeOffset Value, bool YearInferred);
