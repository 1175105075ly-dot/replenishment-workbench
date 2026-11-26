[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The Windows x64 .NET Framework C# compiler was not found.'
}

$source = Join-Path $PSScriptRoot 'src'
$dist = Join-Path $PSScriptRoot 'dist'
$exe = Join-Path $dist '补货预测工作台.exe'
$icon = Join-Path $source 'replenishment.ico'
$files = @('Program.cs', 'Forecast.cs', 'LingXing.cs', 'ImportDialog.cs', 'TrendPanel.cs') |
    ForEach-Object { Join-Path $source $_ }
$references = @('System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Runtime.Serialization',
    'System.Net.Http', 'System.Web.Extensions') | ForEach-Object { "/r:$_.dll" }

[void][System.IO.Directory]::CreateDirectory($dist)
& $compiler /nologo /codepage:65001 /warnaserror+ /optimize+ /target:winexe "/out:$exe" "/win32icon:$icon" $references $files
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed with exit code $LASTEXITCODE." }
Write-Output $exe
