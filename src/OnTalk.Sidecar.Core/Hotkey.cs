namespace OnTalk.Sidecar.Core;

/// <summary>
/// 패널을 접고 펴는 전역 단축키. 수정키 비트와 가상 키 코드는 Win32 RegisterHotKey가 쓰는 값과 같다.
/// 화면 표시·저장 문자열(<c>Ctrl+Shift+Space</c>)과의 변환, 쓸 수 없는 조합 판정만 맡는다.
/// </summary>
public readonly record struct Hotkey(uint Modifiers, uint Key)
{
    public const uint Alt = 1, Control = 2, Shift = 4, Win = 8;

    public static Hotkey Default { get; } = new(Control | Shift, 0x20);

    private static readonly (string Name, uint Key)[] Named =
    [
        ("Space", 0x20), ("PageUp", 0x21), ("PageDown", 0x22), ("End", 0x23), ("Home", 0x24),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28), ("Insert", 0x2D), ("Delete", 0x2E),
    ];

    /// <summary>글쇠로 쓸 수 있는 가상 키인지: 영문·숫자·F1~F24·Space·이동/편집 키.</summary>
    public static bool IsSupportedKey(uint key) => KeyName(key) is not null;

    public static string? KeyName(uint key)
    {
        if (key is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)key).ToString();
        if (key is >= 0x70 and <= 0x87) return "F" + (key - 0x6F);
        foreach (var (name, code) in Named) if (code == key) return name;
        return null;
    }

    private static uint? KeyFromName(string name)
    {
        if (name.Length == 1 && char.ToUpperInvariant(name[0]) is var c && c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 24) return (uint)(0x6F + f);
        foreach (var (known, code) in Named) if (known.Equals(name, StringComparison.OrdinalIgnoreCase)) return code;
        return null;
    }

    /// <summary>눌려 있는 수정키만 표시 순서(Ctrl, Alt, Shift, Win)대로 적는다. 입력 도중의 화면에도 쓴다.</summary>
    public static string FormatModifiers(uint modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & Control) != 0) parts.Add("Ctrl");
        if ((modifiers & Alt) != 0) parts.Add("Alt");
        if ((modifiers & Shift) != 0) parts.Add("Shift");
        if ((modifiers & Win) != 0) parts.Add("Win");
        return string.Join('+', parts);
    }

    public string Format()
    {
        var modifiers = FormatModifiers(Modifiers);
        var key = KeyName(Key) ?? "?";
        return modifiers.Length == 0 ? key : modifiers + "+" + key;
    }

    public override string ToString() => Format();

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return false;
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var bit = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Control, "alt" => Alt, "shift" => Shift, "win" or "windows" => Win, _ => 0u,
            };
            if (bit == 0 || (modifiers & bit) != 0) return false;
            modifiers |= bit;
        }
        if (KeyFromName(parts[^1]) is not { } key) return false;
        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    /// <summary>
    /// 단축키로 쓸 수 없는 이유. 없으면 null. Shift만 곁들인 조합은 글을 쓸 때마다 걸리고,
    /// 편집·창 전환의 기본 조합은 다른 프로그램의 동작을 빼앗으므로 막는다.
    /// </summary>
    public string? Problem
    {
        get
        {
            if (!IsSupportedKey(Key)) return "이 키는 단축키로 쓸 수 없습니다.";
            if ((Modifiers & (Control | Alt | Win)) == 0) return "Ctrl, Alt, Win 중 하나와 함께 눌러 주세요.";
            if (Modifiers == Control && Key is 0x41 or 0x43 or 0x56 or 0x58 or 0x59 or 0x5A) return "복사·붙여넣기 같은 기본 편집 조합은 쓸 수 없습니다.";
            if (Modifiers == Alt && Key is 0x73 or 0x20) return "Windows가 쓰는 조합이라 쓸 수 없습니다.";
            return null;
        }
    }
}
