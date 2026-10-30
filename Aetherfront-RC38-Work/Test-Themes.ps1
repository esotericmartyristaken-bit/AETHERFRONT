param(
 [string]$GameRoot='C:\Users\jkell\Documents\Codex\APFBeta'
)
# Run with Windows PowerShell 5.1 for reflection-only metadata loading.
$ErrorActionPreference='Stop'
$controller=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$theme=Join-Path $controller 'build/rc38-themes'
$managed=Join-Path $GameRoot 'KSP_x64_Data/Managed'
$harmony=Join-Path $GameRoot 'GameData/000_Harmony/0Harmony.dll'
$handler=[ResolveEventHandler]{param($sender,$event)
 $name=([Reflection.AssemblyName]$event.Name).Name
 $candidate=Join-Path $managed ($name+'.dll')
 if($name -eq '0Harmony'){$candidate=$harmony}
 if(Test-Path -LiteralPath $candidate){return [Reflection.Assembly]::ReflectionOnlyLoadFrom($candidate)}
 return [Reflection.Assembly]::ReflectionOnlyLoad($event.Name)
}
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($handler)
try {
 $dll=Join-Path $theme 'Checks/GameData/HUDReplacer/HUDReplacer.dll'
 $assembly=[Reflection.Assembly]::ReflectionOnlyLoadFrom($dll)
 $types=$assembly.GetTypes()
 if($types.Count -lt 10){throw 'HUDReplacer metadata/type inventory incomplete'}
 if(!($types.FullName -contains 'HUDReplacer.Loader')){throw 'HUDReplacer loader missing'}
 if(!($types.FullName -contains 'HUDReplacer.Database')){throw 'HUDReplacer texture database missing'}
 $requirements=@($assembly.GetReferencedAssemblies() | ForEach-Object FullName)
 $report=[ordered]@{dllSha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLower();assembly=$assembly.FullName;loadedTypes=$types.Count;requirements=$requirements;metadataDependencyResolution='passed';pluginExecuted=$false;liveGuiTest='pending'}
 $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $theme 'DLL-VALIDATION.json') -Encoding UTF8
 [pscustomobject]@{Types=$types.Count;DependencyMetadata='passed';LiveGUI='pending'} | ConvertTo-Json
} finally {[AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($handler)}
