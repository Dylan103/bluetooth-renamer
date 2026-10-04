param([string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'src'
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path ([IO.Path]::GetTempPath()) ('BluetoothRenamerTests-' + [guid]::NewGuid().ToString('N').Substring(0,8))
}
$results = [IO.Path]::GetFullPath($ResultsDirectory)
$null = New-Item -ItemType Directory -Path $results
$backendExe = Join-Path $results 'BackendTests.exe'
$backendSources = @((Join-Path $source 'BluetoothService.cs'))
$persistentSource = Join-Path $source 'PersistentNameStore.cs'
if (Test-Path -LiteralPath $persistentSource) { $backendSources += $persistentSource }
& $compiler /nologo /target:exe /platform:x64 /langversion:5 ('/out:' + $backendExe) ('/reference:' + (Join-Path $framework 'System.Runtime.Serialization.dll')) @backendSources (Join-Path $PSScriptRoot 'BackendTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Backend test build failed.' }
& $backendExe (Join-Path $results 'data')
if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
$dpiTest = Join-Path $PSScriptRoot 'DpiTests.cs'
if (Test-Path -LiteralPath $dpiTest) {
    $dpiExe = Join-Path $results 'DpiTests.exe'
    $references = @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $framework 'WPF') $_) }
    $references += '/reference:' + (Join-Path $framework 'System.Xaml.dll')
    & $compiler /nologo /target:exe /platform:x64 /langversion:5 ('/out:' + $dpiExe) @references (Join-Path $source 'DpiSupport.cs') $dpiTest
    if ($LASTEXITCODE -ne 0) { throw 'DPI test build failed.' }
    & $dpiExe
    if ($LASTEXITCODE -ne 0) { throw 'DPI tests failed.' }
}
Write-Output ('Test artifacts: ' + $results)
