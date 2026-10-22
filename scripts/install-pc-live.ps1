$ErrorActionPreference='Stop'
$root=if($env:CHOME_PROJECT_ROOT){$env:CHOME_PROJECT_ROOT}else{Split-Path -Parent $PSScriptRoot}
$appRoot=Join-Path $env:LOCALAPPDATA 'USBHomeCamNAVER'
$dest=Join-Path $appRoot 'live-view'
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'windows/live-view/pc_live.py') -Destination (Join-Path $dest 'pc_live.py') -Force
Copy-Item -LiteralPath (Join-Path $root 'windows/live-view/requirements.txt') -Destination (Join-Path $dest 'requirements.txt') -Force
$helperSrc=Join-Path $root 'windows/push-sender/ReadPushSecrets.exe'
$helperDestDir=Join-Path $appRoot 'push-sender'
New-Item -ItemType Directory -Path $helperDestDir -Force | Out-Null
if(-not (Test-Path $helperSrc)){throw 'Existing protected-secret helper missing'}
Copy-Item -LiteralPath $helperSrc -Destination (Join-Path $helperDestDir 'ReadPushSecrets.exe') -Force
$uv=Get-Command uv -ErrorAction Stop
$venv=Join-Path $dest '.venv'
if(-not (Test-Path (Join-Path $venv 'Scripts/python.exe'))){
 & $uv.Source venv --python 3.12 $venv
 if($LASTEXITCODE -ne 0){throw 'Python environment failed'}
}
& $uv.Source pip install --python (Join-Path $venv 'Scripts/python.exe') -r (Join-Path $dest 'requirements.txt')
if($LASTEXITCODE -ne 0){throw 'PC WebRTC dependencies failed'}
$py=Join-Path $venv 'Scripts/python.exe'
$script=Join-Path $dest 'pc_live.py'
& $py -m py_compile $script
if($LASTEXITCODE -ne 0){throw 'PC live Python source invalid'}
& $py $script --self-test
if($LASTEXITCODE -ne 0){throw 'Authenticated PC live self-test failed'}
$pythonw=Join-Path $venv 'Scripts/pythonw.exe'
if(-not(Test-Path $pythonw)){throw 'Pythonw missing'}
$q=[char]34
$cmd=$q+$pythonw+$q+' '+$q+$script+$q
$run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $run -Force | Out-Null
Set-ItemProperty -Path $run -Name 'CHomePCLive' -Value $cmd
Start-Process -FilePath $pythonw -ArgumentList ($q+$script+$q) -WindowStyle Hidden | Out-Null
Write-Output 'PC_LIVE_INSTALLED_AND_STARTED'