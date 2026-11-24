$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x is required.' }
$output = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $output | Out-Null
$sources = @('Usage', 'SessionQuota', 'Settings', 'Monitoring', 'Forecasting', 'StorageMigration', 'Translations', 'QuotaText', 'Native', 'IslandNative', 'GlassEnvironment') |
    ForEach-Object { Join-Path $root "src\$_.cs" }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warn:4 /define:ENGLISH /codepage:65001 "/out:$output\CodexLimitsBridge.exe" "/win32manifest:$root\app.manifest" "/resource:$root\assets\translations.en.json,translations.en.json" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources (Join-Path $PSScriptRoot 'Bridge.cs')
if ($LASTEXITCODE -ne 0) { throw 'Windows bridge compilation failed.' }
