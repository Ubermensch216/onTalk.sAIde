using System.IO;
using System.Runtime.InteropServices;
using OnTalk.Sidecar.Core;

namespace OnTalk.Sidecar.App;

/// <summary>
/// 패널을 접고 펴는 전역 단축키의 등록 상태. 설정 화면에서 키를 입력받는 동안에는 잠시 풀어 두어
/// 지금 쓰는 단축키도 입력칸으로 들어오게 하고, 새 조합이 등록되면 설정 파일에 저장한다.
/// </summary>
public sealed class HotkeyRegistration(Func<IntPtr> window, AppSettings settings)
{
    public const int Id = 1;
    private const uint NoRepeat = 0x4000;
    private bool _registered;

    public Hotkey Current { get; private set; } =
        Hotkey.TryParse(settings.HotkeyText, out var saved) && saved.Problem is null ? saved : Hotkey.Default;

    /// <summary>지금 등록되어 눌러도 반응하는지. 다른 프로그램이 같은 조합을 쓰면 false.</summary>
    public bool Active => _registered;

    public event Action? Changed;

    public bool Register()
    {
        Unregister();
        _registered = window() != IntPtr.Zero && RegisterHotKey(window(), Id, Current.Modifiers | NoRepeat, Current.Key);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && window() != IntPtr.Zero) UnregisterHotKey(window(), Id);
        _registered = false;
    }

    /// <summary>입력칸에 키를 받는 동안 전역 등록을 풀고, 끝나면 다시 건다.</summary>
    public void Suspend() => Unregister();

    public void Resume()
    {
        if (!_registered) Register();
        Changed?.Invoke();
    }

    /// <summary>새 조합을 등록하고 저장한다. 실패하면 이전 조합을 되살리고 이유를 돌려준다.</summary>
    public string? TryApply(Hotkey key)
    {
        if (key.Problem is { } problem) return problem;
        var previous = Current;
        Current = key;
        if (!Register())
        {
            Current = previous;
            Register();
            Changed?.Invoke();
            return "다른 프로그램이 이미 쓰고 있어 등록하지 못했습니다. 다른 조합을 눌러 주세요.";
        }
        settings.HotkeyText = key.Format();
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 이번 실행에서는 바뀌었지만 다음 실행에 기억하지 못한다.
            Changed?.Invoke();
            return $"단축키는 바뀌었지만 저장하지 못했습니다. {ex.Message}";
        }
        Changed?.Invoke();
        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
