$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$out = Join-Path $root 'InzoneBattery.exe'

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/platform:anycpu',
    ('/out:' + $out),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll'
)

$icon = Join-Path $root 'build\app.ico'
if (Test-Path $icon) { $cscArgs += ('/win32icon:' + $icon) }

$cscArgs += $sources

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
Write-Host "built $out"

# IMPORTANT: set the exe's integrity label to Medium (S-1-16-8192).
# If the folder holding the exe carries a Low mandatory label, csc output inherits
# Low. Explorer then starts low-labelled files with a restricted token, so the
# process runs at Low IL and cannot post Shell_NotifyIcon messages to the Medium-IL
# taskbar (ERROR_ACCESS_DENIED) - symptom: no tray icon at all. Fix it after each build.
$setIl = Join-Path $root 'build\SetIntegrity.exe'
& $csc /nologo /platform:x64 ('/out:' + $setIl) (Join-Path $root 'tools\SetIntegrity.cs') | Out-Null
if ($LASTEXITCODE -eq 0 -and (Test-Path $setIl)) {
    & $setIl $out
    if ($LASTEXITCODE -ne 0) { Write-Warning 'failed to set integrity label; tray icon may not show' }
} else {
    Write-Warning 'SetIntegrity build failed; skipped integrity label fix'
}
