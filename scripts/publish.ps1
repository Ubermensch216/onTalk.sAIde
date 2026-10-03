# 배포 묶음을 만든다. .NET 런타임을 함께 넣으므로 받는 PC에 .NET을 설치할 필요가 없다.
#   powershell -File scripts\publish.ps1
# 결과: artifacts\OnTalk.sAIde-<버전>-win-x64\ 와 같은 이름의 .zip
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
$name = "OnTalk.sAIde-$version-win-x64"
$out = Join-Path $root "artifacts\$name"

dotnet test (Join-Path $root "tests\OnTalk.Sidecar.Tests") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "테스트가 실패해 묶지 않았습니다." }

if (Test-Path $out) { Remove-Item -Recurse -Force -LiteralPath $out }
dotnet publish (Join-Path $root "src\OnTalk.Sidecar.App") -c Release -r win-x64 --self-contained true -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "묶지 못했습니다." }

Copy-Item (Join-Path $root "README.md") (Join-Path $out "README.md")
Get-ChildItem $out -Filter *.pdb | Remove-Item

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
# 방금 쓴 파일을 백신이 검사하느라 잠깐 잡고 있을 수 있어 몇 번 다시 시도한다.
Add-Type -AssemblyName System.IO.Compression.FileSystem
for ($try = 1; ; $try++) {
    try { [IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, "Optimal", $true); break }
    catch [IO.IOException] {
        if (Test-Path $zip) { Remove-Item -LiteralPath $zip }
        if ($try -ge 5) { throw }
        Start-Sleep -Seconds 3
    }
}
"{0}  ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
