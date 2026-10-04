param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference='Stop'
$src=Join-Path $PSScriptRoot 'src'
$framework=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
$null=New-Item -ItemType Directory -Path $OutputDirectory -Force
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
& (Join-Path $PSScriptRoot 'Make-Icon.ps1') -Destination (Join-Path $src 'App.ico')
$references=@('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','System.Xaml.dll')|ForEach-Object { '/reference:'+(Join-Path $framework $_) }
$references+=@('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')|ForEach-Object { '/reference:'+(Join-Path (Join-Path $framework 'WPF') $_) }
$sources=Get-ChildItem -LiteralPath $src -Filter '*.cs'|Select-Object -ExpandProperty FullName
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/langversion:5',('/out:'+(Join-Path $OutputDirectory 'Bluetooth Renamer.exe')),('/win32manifest:'+(Join-Path $src 'app.manifest')),('/win32icon:'+(Join-Path $src 'App.ico')),('/resource:'+(Join-Path $src 'MainWindow.xaml')+',MainWindow.xaml'),('/resource:'+(Join-Path $src 'App.ico')+',App.ico'))+$references+$sources
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Build failed.'}
Copy-Item -LiteralPath (Join-Path $src 'App.config') -Destination (Join-Path $OutputDirectory 'Bluetooth Renamer.exe.config') -Force
Get-Item -LiteralPath (Join-Path $OutputDirectory 'Bluetooth Renamer.exe')|Select-Object FullName,Length
