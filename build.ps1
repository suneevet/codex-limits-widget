param([string]$InnoCompiler, [switch]$PortableOnly)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (!(Test-Path -LiteralPath 'node_modules/.bin/electron-builder.cmd')) {
        npm.cmd ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    }
    npm.cmd test
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    npm.cmd run dist
    if ($LASTEXITCODE -ne 0) { throw 'Electron package build failed.' }

    if (!$PortableOnly) {
        if (!$InnoCompiler) {
            $InnoCompiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
        }
        if (!(Test-Path -LiteralPath $InnoCompiler)) {
            throw 'Inno Setup 6 is required. Specify -InnoCompiler or use -PortableOnly.'
        }
        & $InnoCompiler "/O$(Join-Path $PSScriptRoot 'dist')" 'installer/CodexLimitsWidget.iss'
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    }
} finally {
    Pop-Location
}
