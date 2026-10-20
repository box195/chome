$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$source=Join-Path $root 'windows\push-sender'
$compiler="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
foreach($name in @('ReadPushSecrets','ProtectVapid')){
 & $compiler /nologo /target:exe "/out:$source\$name.exe" /r:System.Security.dll /r:System.Web.Extensions.dll "$source\$name.cs"
 if($LASTEXITCODE -ne 0){throw "Failed to build $name"}
}
& npm.cmd ci --prefix $source --no-audit --no-fund
if($LASTEXITCODE -ne 0){throw 'Dependency installation failed'}
