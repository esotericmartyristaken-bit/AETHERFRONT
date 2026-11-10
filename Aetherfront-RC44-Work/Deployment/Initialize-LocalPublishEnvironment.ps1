#requires -Version 7.4
function Initialize-LocalPublishEnvironment {
    # The persistent task intentionally supplies a minimal environment. Windows
    # command discovery still needs the executable extensions for child tools.
    $env:PATHEXT='.COM;.EXE;.BAT;.CMD'
    $git=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Git/cmd/git.exe'
    if(-not [IO.File]::Exists($git)){throw 'The approved installed Git executable is unavailable'}
    return $git
}
