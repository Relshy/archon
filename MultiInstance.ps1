#Requires -Version 5.1
# order matters: if roblox is already running it owns the names, so close its
# handles first and only then take them. the other order always fails.
[CmdletBinding()]
param(
    [string[]] $ProcessName = @('RobloxPlayerBeta'),

    [ValidateRange(1, 60)]
    [int] $RefreshSeconds = 3,

    # frees the handles and exits without holding the names - a named object
    # only lives as long as the process holding it, so it can't hold anything
    [switch] $FreeOnly,

    [switch] $SkipHandleClose
)

try { $Host.UI.RawUI.WindowTitle = 'Archon - Multi-Instance Active' } catch { }

$SingletonNames = @('ROBLOX_singletonEvent', 'ROBLOX_singletonMutex')

# HandleCloser.cs is compiled into the exe and read verbatim here, single implementation
if (-not ('Archon.RobloxHandleCloser' -as [type])) {
    $closerSource = Join-Path $PSScriptRoot 'HandleCloser.cs'

    if (-not (Test-Path $closerSource)) {
        Write-Host ''
        Write-Host '  [FAIL] HandleCloser.cs is missing from this folder.' -ForegroundColor Red
        Write-Host '         It sits next to this script and is required to free' -ForegroundColor Yellow
        Write-Host '         handles from an already-running Roblox.' -ForegroundColor Yellow
        Write-Host '         Re-run with -SkipHandleClose to hold the names only.' -ForegroundColor Yellow
        if (-not $SkipHandleClose) { exit 1 }
    }
    else {
        try {
            Add-Type -TypeDefinition ([System.IO.File]::ReadAllText($closerSource)) -ErrorAction Stop
        }
        catch {
            Write-Host ''
            Write-Host "  [FAIL] Could not compile HandleCloser.cs: $($_.Exception.Message)" -ForegroundColor Red
            if (-not $SkipHandleClose) { exit 1 }
        }
    }
}

function Write-Banner {
    Write-Host ''
    Write-Host '  ========================================' -ForegroundColor Cyan
    Write-Host '   ARCHON' -ForegroundColor Cyan
    Write-Host '  ========================================' -ForegroundColor Cyan
    Write-Host ''
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-RobloxProcess {
    param([string[]] $Name)
    $found = @()
    foreach ($n in $Name) {
        $found += @(Get-Process -Name $n -ErrorAction SilentlyContinue)
    }
    return $found
}

# the same Everyone-allowed DACL that CreateOpenMutex in Singleton.cs gives the
# GUI's names. without it an elevated run creates them admin-only, and a normal
# Roblox client is denied access where it would otherwise find the objects.
function New-OpenMutexSecurity {
    $security = New-Object System.Security.AccessControl.MutexSecurity
    $everyone = New-Object System.Security.Principal.SecurityIdentifier(
        [System.Security.Principal.WellKnownSidType]::WorldSid, $null)
    $security.AddAccessRule((New-Object System.Security.AccessControl.MutexAccessRule(
        $everyone,
        [System.Security.AccessControl.MutexRights]::FullControl,
        [System.Security.AccessControl.AccessControlType]::Allow)))
    return $security
}

# New-Object Mutex($true,$name) never throws when the name is already taken,
# it just hands back an unowned mutex - check Owned, not just that this succeeded
function New-SingletonLock {
    param([Parameter(Mandatory)] [string] $Name)

    $createdNew = $false
    $mutex = $null
    $owned = $false
    $err = $null

    try {
        try {
            $mutex = New-Object System.Threading.Mutex($true, $Name, [ref] $createdNew, (New-OpenMutexSecurity))
        }
        catch {
            # the GUI's last fallback too - default security beats no lock at all
            $mutex = New-Object System.Threading.Mutex($true, $Name, [ref] $createdNew)
        }

        if ($createdNew) {
            $owned = $true
        }
        else {
            try { $owned = $mutex.WaitOne(0) }
            catch [System.Threading.AbandonedMutexException] { $owned = $true }
        }
    }
    catch [System.Threading.WaitHandleCannotBeOpenedException] {
        $err = 'name is already held by another process (close Roblox first)'
    }
    catch {
        $err = $_.Exception.Message
    }

    return [pscustomobject]@{
        Name       = $Name
        Mutex      = $mutex
        Owned      = $owned
        CreatedNew = $createdNew
        Error      = $err
    }
}

function Remove-SingletonLock {
    param($Lock)
    if ($null -eq $Lock -or $null -eq $Lock.Mutex) { return }
    try { if ($Lock.Owned) { $Lock.Mutex.ReleaseMutex() } } catch { }
    try { $Lock.Mutex.Dispose() } catch { }
}

Write-Banner

if (-not $SkipHandleClose -and -not (Test-Administrator)) {
    Write-Host '  [WARN] Not running as Administrator.' -ForegroundColor Yellow
    Write-Host '         Pre-holding the names still works, but closing handles' -ForegroundColor DarkGray
    Write-Host '         on an already-running Roblox may fail.' -ForegroundColor DarkGray
    Write-Host ''
}

$running = Get-RobloxProcess -Name $ProcessName
if ($SkipHandleClose) {
    Write-Host '  [INFO] Skipping handle close (names-only mode)' -ForegroundColor DarkGray
    if ($running.Count -gt 0) {
        Write-Host "  [WARN] $($running.Count) Roblox process(es) already running." -ForegroundColor Yellow
        Write-Host '         If it still owns the names, the checks below will fail -' -ForegroundColor Yellow
        Write-Host '         re-run without -SkipHandleClose (menu option 1).' -ForegroundColor Yellow
    }
}
elseif ($running.Count -gt 0) {
    Write-Host "  [INFO] Found $($running.Count) running Roblox process(es)" -ForegroundColor Yellow
    Write-Host '  [INFO] Closing their singleton handles...' -ForegroundColor Yellow

    try {
        $close = [Archon.RobloxHandleCloser]::Close($ProcessName)

        if ($close.Error) {
            Write-Host "  [WARN] Handle scan failed: $($close.Error)" -ForegroundColor Yellow
        }
        elseif ($close.OpenFailures -gt 0 -and $close.Closed -eq 0) {
            Write-Host "  [WARN] Could not open $($close.OpenFailures) Roblox process(es)" -ForegroundColor Yellow
            Write-Host '  [WARN] Re-run as Administrator to close their handles.' -ForegroundColor Yellow
        }
        elseif ($close.Closed -gt 0) {
            Write-Host "  [OK] Closed $($close.Closed) singleton handle(s)" -ForegroundColor Green
        }
        else {
            Write-Host '  [OK] No singleton handles found (already cleared)' -ForegroundColor Green
        }
    }
    catch {
        Write-Host "  [WARN] Handle closing failed: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host '  [WARN] Try running as Administrator.' -ForegroundColor Yellow
    }
}
else {
    Write-Host '  [INFO] No Roblox running yet - taking the names up front' -ForegroundColor DarkGray
}

Write-Host ''

$locks = @()
foreach ($name in $SingletonNames) {
    $lock = New-SingletonLock -Name $name
    $locks += $lock

    if ($lock.Owned) {
        Write-Host "  [OK] Holding $name" -ForegroundColor Green
    }
    elseif ($lock.Error) {
        Write-Host "  [FAIL] $name - $($lock.Error)" -ForegroundColor Red
    }
    else {
        Write-Host "  [FAIL] $name - exists but is owned elsewhere" -ForegroundColor Red
    }
}

$ownedCount = @($locks | Where-Object { $_.Owned }).Count

Write-Host ''
Write-Host '  ----------------------------------------' -ForegroundColor DarkGray
if ($ownedCount -eq $SingletonNames.Count) {
    Write-Host '  MULTI-INSTANCE IS ACTIVE' -ForegroundColor Green
    Write-Host '  Keep this window open while playing!' -ForegroundColor White
}
elseif ($ownedCount -gt 0) {
    Write-Host '  PARTIALLY ACTIVE' -ForegroundColor Yellow
    Write-Host '  Some names could not be taken - multi-instance may not work.' -ForegroundColor Yellow
}
else {
    Write-Host '  NOT ACTIVE' -ForegroundColor Red
    Write-Host '  Close all Roblox windows, then run this again as Administrator.' -ForegroundColor Yellow
}
Write-Host '  ----------------------------------------' -ForegroundColor DarkGray
Write-Host ''

if ($ownedCount -gt 0) {
    Write-Host '  Steps:' -ForegroundColor Yellow
    Write-Host '    1. Open Roblox and join a game' -ForegroundColor Gray
    Write-Host '    2. Switch accounts in your browser' -ForegroundColor Gray
    Write-Host '    3. Join a game on the other account' -ForegroundColor Gray
    Write-Host '    4. Both clients will run side by side!' -ForegroundColor Gray
    Write-Host ''
}

if ($FreeOnly) {
    foreach ($lock in $locks) { Remove-SingletonLock -Lock $lock }
    Write-Host '  -FreeOnly: handles freed, names released, exiting.' -ForegroundColor DarkGray
    Write-Host '  Nothing is holding them now - start without -FreeOnly to hold them.' -ForegroundColor DarkGray
    exit ([int]($ownedCount -ne $SingletonNames.Count))
}

Write-Host '  Press Ctrl+C or close this window to stop.' -ForegroundColor DarkGray
Write-Host ''

# try/finally so the release below actually runs on Ctrl+C
try {
    while ($true) {
        $procs = Get-RobloxProcess -Name $ProcessName
        $timestamp = Get-Date -Format 'HH:mm:ss'
        $line = "  [$timestamp] Roblox instances running: $($procs.Count)"
        Write-Host ("`r" + $line.PadRight(60)) -NoNewline -ForegroundColor Cyan
        Start-Sleep -Seconds $RefreshSeconds
    }
}
finally {
    Write-Host ''
    Write-Host ''
    Write-Host '  Releasing singleton names...' -ForegroundColor DarkGray
    foreach ($lock in $locks) { Remove-SingletonLock -Lock $lock }
    Write-Host '  Multi-instance disabled.' -ForegroundColor DarkGray
}
