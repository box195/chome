param([Parameter(Mandatory=$true)][string]$OwnerUid)
$ErrorActionPreference='Stop'
if($OwnerUid -notmatch '^[a-zA-Z0-9_-]{8,128}$' -or $OwnerUid -eq '__OWNER_UID__') { throw 'Invalid Firebase Google owner UID' }
$base=Split-Path -Parent $MyInvocation.MyCommand.Path
$template=Get-Content -LiteralPath (Join-Path $base 'database.rules.template.json') -Raw -Encoding UTF8
if($template -notlike '*__OWNER_UID__*'){throw 'owner placeholder missing'}
$rules=$template.Replace('__OWNER_UID__',$OwnerUid)
$null=$rules | ConvertFrom-Json
$path=Join-Path $base 'database.rules.owner.json'
[IO.File]::WriteAllText($path,$rules,[Text.UTF8Encoding]::new($false))
Write-Output 'OWNER_RULES_GENERATED_FOR_REVIEW_NOT_DEPLOYED'
Write-Output 'Owner must independently confirm UID; then provision %LOCALAPPDATA%\USBHomeCamNAVER\remote\owner-uid.txt with the same UID under their Windows account.'
Write-Output 'No Firebase deploy, Google login, recorder process or local credential file changed.'
