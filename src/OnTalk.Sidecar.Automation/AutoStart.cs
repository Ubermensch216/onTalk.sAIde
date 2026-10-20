using Microsoft.Win32;

namespace OnTalk.Sidecar.Automation;

/// <summary>Run 키의 값을 읽고 쓰는 자리. 시험에서는 메모리 구현으로 바꾼다.</summary>
public interface IRunKey
{
    string? Get(string name);
    void Set(string name, string command);
    void Remove(string name);
}

/// <summary>현재 사용자(HKCU)의 Run 키. 관리자 권한이 필요 없다.</summary>
public sealed class CurrentUserRunKey : IRunKey
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Remove(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Windows 시작 시 자동 실행 등록. 등록 여부는 Run 키 값이 있는지로만 판단하므로 설정 파일에 따로 적지 않는다.
/// 값에는 지금 실행 중인 exe 경로와 <see cref="Argument"/>를 넣는다.
/// </summary>
public sealed class AutoStart(IRunKey runKey, string? exePath)
{
    /// <summary>자동 실행으로 뜬 것을 알리는 인자. 수동 실행과 구분한다.</summary>
    public const string Argument = "--autostart";

    public const string ValueName = "OnTalk.sAIde";

    /// <summary>등록할 수 있는 실행 파일인지. dotnet.exe 같은 호스트로 띄운 경우에는 등록하지 않는다.</summary>
    public bool CanRegister =>
        !string.IsNullOrWhiteSpace(exePath) &&
        !string.Equals(Path.GetFileNameWithoutExtension(exePath), "dotnet", StringComparison.OrdinalIgnoreCase);

    public static AutoStart ForCurrentProcess() => new(new CurrentUserRunKey(), Environment.ProcessPath);

    public string? RegisteredCommand => runKey.Get(ValueName);

    public bool IsEnabled => RegisteredCommand is { Length: > 0 };

    /// <summary>경로에 공백이 있어도 한 덩어리로 읽히도록 따옴표로 감싼다.</summary>
    public string BuildCommand() => $"\"{exePath}\" {Argument}";

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            runKey.Remove(ValueName);
            return;
        }
        if (!CanRegister)
        {
            throw new InvalidOperationException("실행 파일 경로를 알 수 없어 자동 실행을 등록할 수 없습니다.");
        }
        runKey.Set(ValueName, BuildCommand());
    }

    /// <summary>
    /// 등록되어 있는데 가리키는 경로가 지금 실행 파일과 다르면(폴더를 옮겼거나 새 버전으로 바꿨을 때) 다시 쓴다.
    /// 등록하지 않은 사용자의 레지스트리는 건드리지 않는다. 바꿨으면 true.
    /// </summary>
    public bool RefreshPath()
    {
        if (!CanRegister || RegisteredCommand is not { Length: > 0 } current)
        {
            return false;
        }
        var expected = BuildCommand();
        if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        runKey.Set(ValueName, expected);
        return true;
    }

    /// <summary>시작 인자에 자동 실행 표시가 있는지.</summary>
    public static bool IsAutoStartLaunch(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, Argument, StringComparison.OrdinalIgnoreCase));
}
