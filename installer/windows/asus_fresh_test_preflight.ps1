# Read-only gate for the specifically approved ASUS fresh installation test.
# Refuse every pre-existing state root, even if it appears empty: this prevents
# launching the production reconnect path against a linked family/auth store.
$ErrorActionPreference='Stop'
if($env:COMPUTERNAME -ne 'ASUSHADAD'){throw 'ASUS_TEST_HOST_MISMATCH'}
foreach($path in @('C:\Program Files\Yahad.FirstInstall','C:\ProgramData\Yahad.FirstInstall'))
{if(Test-Path -LiteralPath $path){throw 'PREEXISTING_STATE_PRESERVED_TEST_ABORTED'}}
if(Get-Service -Name Yahad.Acceptance -ErrorAction SilentlyContinue){throw 'PREEXISTING_SERVICE_TEST_ABORTED'}
if(Get-Process -Name Yahad.Setup,Yahad.ServiceHost,Yahad.Setup.Web -ErrorAction SilentlyContinue){throw 'PREEXISTING_PROCESS_TEST_ABORTED'}
Write-Output 'ASUS fresh-test preflight passed: no state root, service or application process. No existing auth can be reconnected.'
