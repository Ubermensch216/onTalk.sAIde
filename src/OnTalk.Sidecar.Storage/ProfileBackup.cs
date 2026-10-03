using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OnTalk.Sidecar.Storage;

public sealed record RestoreResult(int Notes, string? PreviousDataMovedTo);

/// <summary>
/// 계정 저장소를 다른 PC나 다른 Windows 사용자로 옮길 수 있는 백업 파일로 만든다.
/// 저장소의 암호화 키는 Windows 사용자에 묶여 있어 폴더를 그대로 복사해서는 다른 곳에서 열 수 없다.
/// 그래서 백업에는 키를 사용자가 정한 암호로 다시 잠가 넣는다. 쪽지와 파일은 저장소에 있던 암호화 상태 그대로 들어간다.
/// </summary>
public static partial class ProfileBackup
{
    public const int MinPasswordLength = 8;
    private const int Iterations = 600_000;

    private sealed record Manifest(
        int Format, string LoginId, DateTimeOffset CreatedAt, int SchemaVersion, string Salt, int Iterations,
        string Nonce, string Tag, string Key);

    [GeneratedRegex(@"^files/[0-9A-F]{64}\.bin$")]
    private static partial Regex BlobEntry();

    public static void Create(ProfileStore store, string zipPath, string password)
    {
        RequirePassword(password);
        var (dir, key) = store.PrepareForBackup();
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[key.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Derive(password, salt, Iterations), tag.Length))
        {
            aes.Encrypt(nonce, key, cipher, tag);
        }
        var manifest = new Manifest(1, store.LoginId, DateTimeOffset.Now, ProfileStore.CurrentSchemaVersion,
            Convert.ToBase64String(salt), Iterations, Convert.ToBase64String(nonce), Convert.ToBase64String(tag),
            Convert.ToBase64String(cipher));

        var temp = zipPath + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(manifest));
            }
            // 저장소가 열려 있어도 읽을 수 있게 공유 모드로 연다.
            AddFile(zip, Path.Combine(dir, "notes.db"), "notes.db");
            var files = Path.Combine(dir, "files");
            if (Directory.Exists(files))
            {
                foreach (var blob in Directory.EnumerateFiles(files, "*.bin"))
                {
                    AddFile(zip, blob, "files/" + Path.GetFileName(blob));
                }
            }
        }
        File.Move(temp, zipPath, overwrite: true);
    }

    /// <summary>
    /// 백업을 <paramref name="loginId"/> 계정의 저장소로 되살린다. 그 계정의 저장소가 열려 있으면 먼저 닫아야 한다.
    /// 이미 있던 자료는 지우지 않고 옆 폴더로 옮긴다.
    /// </summary>
    public static RestoreResult Restore(string root, string loginId, string zipPath, string password)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifest = ReadManifest(zip);
        if (manifest.LoginId != loginId)
        {
            throw new InvalidOperationException($"다른 계정({manifest.LoginId})의 백업입니다.");
        }
        if (manifest.SchemaVersion > ProfileStore.CurrentSchemaVersion)
        {
            throw new InvalidOperationException("더 새로운 버전에서 만든 백업입니다. 프로그램을 먼저 올려 주세요.");
        }
        byte[] key;
        try
        {
            var cipher = Convert.FromBase64String(manifest.Key);
            key = new byte[cipher.Length];
            using var aes = new AesGcm(Derive(password, Convert.FromBase64String(manifest.Salt), manifest.Iterations), 16);
            aes.Decrypt(Convert.FromBase64String(manifest.Nonce), cipher, Convert.FromBase64String(manifest.Tag), key);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException("백업 암호가 맞지 않습니다.", ex);
        }

        var staging = Path.Combine(root, loginId + ".restoring");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
        Directory.CreateDirectory(Path.Combine(staging, "files"));
        try
        {
            foreach (var entry in zip.Entries)
            {
                // 정해진 이름의 파일만 푼다. 백업 파일이 조작되어도 저장소 폴더 밖에 쓰지 않는다.
                if (entry.FullName == "notes.db" || BlobEntry().IsMatch(entry.FullName))
                {
                    entry.ExtractToFile(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                }
                else if (entry.FullName != "manifest.json")
                {
                    throw new InvalidOperationException("백업 파일에 알 수 없는 항목이 있습니다.");
                }
            }
            File.WriteAllBytes(Path.Combine(staging, "key.bin"), ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));

            // 옮기기 전에 실제로 열리는지 확인한다.
            int notes;
            using (var check = ProfileStore.Open(root, loginId + ".restoring"))
            {
                notes = check.CountNotes();
            }

            var target = Path.Combine(root, loginId);
            string? moved = null;
            if (Directory.Exists(target))
            {
                moved = $"{target}.before-restore-{DateTime.Now:yyyyMMdd-HHmmss}";
                Directory.Move(target, moved);
            }
            Directory.Move(staging, target);
            return new RestoreResult(notes, moved);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            throw;
        }
    }

    private static Manifest ReadManifest(ZipArchive zip)
    {
        try
        {
            using var reader = new StreamReader((zip.GetEntry("manifest.json") ?? throw new InvalidDataException()).Open());
            return JsonSerializer.Deserialize<Manifest>(reader.ReadToEnd()) is { Format: 1 } manifest
                ? manifest
                : throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new InvalidOperationException("onTalk.sAIde 백업 파일이 아닙니다.", ex);
        }
    }

    private static void RequirePassword(string password)
    {
        if (password.Length < MinPasswordLength)
        {
            throw new ArgumentException($"백업 암호는 {MinPasswordLength}자 이상이어야 합니다.", nameof(password));
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static void AddFile(ZipArchive zip, string path, string name)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
        source.CopyTo(target);
    }
}
