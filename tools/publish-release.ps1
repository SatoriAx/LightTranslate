param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$ReleaseNotesPath = "",
    [string]$Title = "",
    [string]$Repo = "SatoriAx/LightTranslate"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repoRoot "release-v$Version"
$staging = Join-Path $releaseRoot "staging"
$dotnet = "$env:USERPROFILE\.dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

Write-Host "== 0. 环境检查 =="
gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "gh 未登录，先执行 gh auth login" }

Write-Host "== 1. 发布主程序 (v$Version) =="
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
& $dotnet publish (Join-Path $repoRoot "LightTranslate.csproj") `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $staging
if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

Write-Host "== 2. 重命名资产 + SHA256 =="
$exe = Join-Path $staging "LightTranslate-windows-x64.exe"
Copy-Item (Join-Path $staging "LightTranslate.exe") $exe -Force
Remove-Item (Join-Path $staging "LightTranslate.exe") -Force
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Set-Content -Path "$exe.sha256" -Value $hash -NoNewline -Encoding Ascii
Write-Host "EXE  SHA256: $hash"
Write-Host "EXE  size  : $((Get-Item $exe).Length) bytes"

Write-Host "== 3. 源码包 =="
$srcTemp = Join-Path $env:TEMP "LightTranslate-src-$Version"
if (Test-Path $srcTemp) { Remove-Item $srcTemp -Recurse -Force }
New-Item -ItemType Directory -Path $srcTemp -Force | Out-Null
robocopy $repoRoot $srcTemp /E /XD bin obj publish-* release-* .git .agents `
    /XF *.user *.userprefs > $null
$zip = Join-Path $releaseRoot "LightTranslate-v$Version-source.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$srcTemp\*" -DestinationPath $zip -CompressionLevel Optimal
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host "ZIP  SHA256: $zipHash"
Write-Host "ZIP  size  : $((Get-Item $zip).Length) bytes"

Write-Host "== 4. 创建 GitHub Release =="
$notes = $ReleaseNotesPath
if ([string]::IsNullOrWhiteSpace($notes) -or -not (Test-Path $notes)) {
    $notes = Join-Path $releaseRoot "release-notes-v$Version.md"
    if (-not (Test-Path $notes)) {
        $notes = "$env:TEMP\LightTranslate-release-notes-$Version.md"
        Set-Content -Path $notes -Value "# LightTranslate v$Version" -Encoding UTF8
    }
}
$relTitle = if ([string]::IsNullOrWhiteSpace($Title)) { "LightTranslate v$Version" } else { $Title }
if (Test-Path $notes) {
    $notesText = Get-Content $notes -Raw -Encoding UTF8
    $notesText = $notesText.Replace('{{VERSION}}', $Version)
    $notesText = $notesText.Replace('{{EXE_SHA256}}', $hash)
    $notesText = $notesText.Replace('{{ZIP_SHA256}}', $zipHash)
    Set-Content -Path $notes -Value $notesText -Encoding UTF8 -NoNewline
}
gh release create "v$Version" --repo $Repo --title $relTitle --notes-file $notes `
    --assets "$exe,$exe.sha256,$zip"
if ($LASTEXITCODE -ne 0) { throw "gh release create 失败" }

Write-Host "== 完成 =="
Write-Host "Release: https://github.com/$Repo/releases/tag/v$Version"
Write-Host "本地产物: $releaseRoot"
