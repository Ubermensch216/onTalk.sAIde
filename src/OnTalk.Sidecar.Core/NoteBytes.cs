using System.Text;

namespace OnTalk.Sidecar.Core;

public static class NoteBytes
{
    public const int Limit = 4000;

    /// <summary>
    /// 온톡 작성창의 "n/4000 Bytes"와 같은 값. 온톡 5.4.3에서 UTF-8 바이트 수이고 줄바꿈은 1바이트임을
    /// 확인했다(docs/feasibility-results.md).
    /// </summary>
    public static int Count(string text) => Encoding.UTF8.GetByteCount(text.Replace("\r\n", "\n"));
}
