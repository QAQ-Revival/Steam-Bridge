$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$testExe = Join-Path $output 'UiSmokeTests.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /utf8output /codepage:65001 /main:EndfieldSteamBridge.UiSmokeTests /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Management.dll "/out:$testExe" @sources (Join-Path $PSScriptRoot 'tests\UiSmokeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test build failed.' }
& $testExe (Join-Path $output 'ui-previews')
if ($LASTEXITCODE -ne 0) { throw 'UI smoke tests failed.' }
