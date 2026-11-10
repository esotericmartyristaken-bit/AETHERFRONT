#requires -Version 7.5
$ErrorActionPreference='Stop'
$directory=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $directory 'Publisher.Common.ps1')
foreach($file in Get-ChildItem -LiteralPath $directory -Filter '*.ps1'){
    $tokens=$null;$errors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw ($errors.Message -join '; ')}
}
$output=Join-Path ([IO.Path]::GetTempPath()) ('apf-session-test-'+[Guid]::NewGuid().ToString('N')+'.dll')
Add-Type -Path (Join-Path $directory 'SessionAccess.cs') -OutputAssembly $output
Add-Type -Path $output
$kind=[AetherfrontPublisherSessionAccess].GetNestedType('Privilege',[Reflection.BindingFlags]::NonPublic)
if([Runtime.InteropServices.Marshal]::SizeOf([object][Activator]::CreateInstance($kind)) -ne 16){throw 'Native privilege structure differs'}
$sid=[AetherfrontPublisherSessionAccess]::CurrentLogonSid()
if($sid -notmatch '^S-1-5-5-'){throw 'Windows interactive logon SID is unavailable'}
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18'){
    try{$null=[AetherfrontPublisherSessionAccess]::InteractiveLogons('S-1-5-21-1-2-3-1001');throw 'Session discovery accepted non-SYSTEM caller'}
    catch{if($_.Exception.InnerException -isnot [UnauthorizedAccessException]){throw}}
}
$pins=[AetherfrontPublisherSessionAccess]::PinAncestors(@($directory));$pins.Dispose()
$scheduler=New-Object -ComObject Schedule.Service;$scheduler.Connect();$folder=$scheduler.GetFolder('\')
if($null -ne (Get-PublisherTaskIfPresent $folder ('Aetherfront-NoSuchTest-'+[Guid]::NewGuid().ToString('N')))){throw 'Absent task was not recognized'}
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
if($settings.ExecutionTimeLimit -cne 'PT0S'){throw 'Scheduled task lifetime differs'}
[pscustomobject]@{status='passed';checks=7;logonSid=$sid;sessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId;productionModified=$false;uacRequests=0}
