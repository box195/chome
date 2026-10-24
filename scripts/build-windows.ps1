param([Parameter(Mandatory=$true)][string]$Ffmpeg)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$compiler="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$ffmpegPath=(Resolve-Path -LiteralPath $Ffmpeg).Path
$dist=Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$recorder=Get-ChildItem -LiteralPath (Join-Path $root 'windows\recorder') -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $compiler /nologo /target:winexe "/out:$dist\USBHomeCam-NAVER-Controlled.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Management.dll /r:System.Net.Http.dll /r:System.Security.dll "/resource:$ffmpegPath,ffmpeg.exe" $recorder
if($LASTEXITCODE -ne 0){throw 'Recorder compile failed'}
$relay=Get-ChildItem -LiteralPath (Join-Path $root 'windows\relay') -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $compiler /nologo /target:winexe "/out:$dist\HomeCamRemote-CHome.exe" /r:System.Web.Extensions.dll /r:System.Security.dll $relay
if($LASTEXITCODE -ne 0){throw 'Relay compile failed'}
