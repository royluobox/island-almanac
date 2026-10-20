# 把插件打成市场用的 .lwp（zip 改扩展名），并做打包自检。
#
#   powershell -ExecutionPolicy Bypass -File tools\pack.ps1
#
# 产出：dist\<id>.lwp
# 规则：只带插件自己的文件；宿主的 WinIsland.Core / WinUI / WebView2 等一律剔除。

param(
    [string]$Configuration = 'Release',
    [string]$SourceDir,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$manifest = Get-Content (Join-Path $root 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$id = $manifest.id

if (-not $SourceDir) {
    $SourceDir = Join-Path $root "bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"
}
if (-not $OutputDir) {
    $OutputDir = Join-Path $root 'dist'
}

Write-Host "==> 构建 $Configuration"
$stage = Join-Path $env:TEMP "island-almanac-pack-$([guid]::NewGuid().ToString('n').Substring(0,8))"
dotnet build (Join-Path $root 'IslandAlmanac.csproj') -c $Configuration -v q --nologo -p:PluginTargetDir="$stage"
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

# 宿主自带、绝对不该进包的文件
$strip = @(
    'WinIsland.Core.dll',
    'Microsoft.WinUI.dll',
    'Microsoft.Windows.SDK.NET.dll',
    'WinRT.Runtime.dll',
    'WebView2Loader.dll',
    'Microsoft.Windows.*.dll',
    'Microsoft.Web.WebView2*.dll',
    'Microsoft.Graphics.*.dll',
    'Microsoft.InteractiveExperiences.*.dll',
    'Microsoft.*.Projection.dll',
    'Microsoft.WindowsAppRuntime*',
    '*.pdb'
)

$files = Get-ChildItem $stage -Recurse -File
foreach ($pattern in $strip) {
    $files = $files | Where-Object { $_.Name -notlike $pattern }
}

if (-not ($files | Where-Object { $_.Name -eq 'plugin.json' })) { throw "包内缺少 plugin.json" }
if (-not ($files | Where-Object { $_.Name -eq $manifest.entry_dll })) { throw "包内缺少入口 $($manifest.entry_dll)" }

$payload = Join-Path $env:TEMP "island-almanac-lwp-$([guid]::NewGuid().ToString('n').Substring(0,8))"
New-Item -ItemType Directory -Path $payload -Force | Out-Null
foreach ($f in $files) {
    $rel = $f.FullName.Substring($stage.Length + 1)
    $dest = Join-Path $payload $rel
    New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
    Copy-Item $f.FullName $dest
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$lwp = Join-Path $OutputDir "$id.lwp"
$zip = [IO.Path]::ChangeExtension($lwp, '.zip')
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal
if (Test-Path $lwp) { Remove-Item $lwp -Force }
Move-Item $zip $lwp

Remove-Item $payload -Recurse -Force
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==> $lwp  ($([math]::Round((Get-Item $lwp).Length / 1KB, 1)) KB)"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($lwp)
try {
    $archive.Entries | ForEach-Object {
        "{0,-36} {1,8:N1} KB" -f $_.FullName, ($_.Length / 1KB)
    }
} finally {
    $archive.Dispose()
}
