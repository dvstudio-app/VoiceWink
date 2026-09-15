# VoiceWink Installer (no admin required)
# Run: powershell -ExecutionPolicy Bypass -File VoiceWinkSetup.ps1

try {

$AppName = "VoiceWink"
$InstallDir = Join-Path $env:LOCALAPPDATA $AppName
$SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$StartMenu = [Environment]::GetFolderPath("Programs")

# Read version from the app DLL
$appDll = Join-Path $SourceDir "app\VoiceWink.dll"
if (Test-Path $appDll) {
    try { $Version = [System.Reflection.AssemblyName]::GetAssemblyName($appDll).Version.ToString(3) }
    catch { $Version = "unknown" }
} else {
    $Version = "unknown"
}

Write-Host ""
Write-Host "=== $AppName Installer v$Version ===" -ForegroundColor Cyan
Write-Host ""

# Validate folder structure
$appFolder = Join-Path $SourceDir "app"
if (-not (Test-Path $appFolder)) {
    Write-Host "ERROR: 'app' folder not found next to VoiceWinkSetup.ps1." -ForegroundColor Red
    Write-Host "Expected at: $appFolder" -ForegroundColor Red
    Write-Host "Make sure you extracted the full zip before running the installer." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Press Enter to close..." -NoNewline; $null = Read-Host
    exit 1
}

# -- Step 1: Ensure .NET 8 Desktop Runtime (user-level, no admin) ---------

Write-Host "[1/6] Checking .NET 8 Desktop Runtime..."
$dotnetDir = Join-Path $env:LOCALAPPDATA "VoiceWink\dotnet"
$systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$hasDotnet = $false

# Check system dotnet first
$systemHasRuntime = $false
if ($systemDotnet) {
    $runtimeMatch = & dotnet --list-runtimes 2>$null | Select-String "Microsoft.WindowsDesktop.App 8\."
    if ($runtimeMatch) {
        $hasDotnet = $true
        $systemHasRuntime = $true
        Write-Host "   .NET 8 Desktop Runtime: OK (system)" -ForegroundColor Green
    }
}

# Check local dotnet install
if (-not $hasDotnet) {
    $localDotnet = Join-Path $dotnetDir "dotnet.exe"
    if (Test-Path $localDotnet) {
        $runtimeMatch = & $localDotnet --list-runtimes 2>$null | Select-String "Microsoft.WindowsDesktop.App 8\."
        if ($runtimeMatch) {
            $hasDotnet = $true
            Write-Host "   .NET 8 Desktop Runtime: OK (user-level)" -ForegroundColor Green
        }
    }
}

# Install .NET runtime
if (-not $hasDotnet) {
    Write-Host "   .NET 8 Desktop Runtime: not found" -ForegroundColor Yellow
    # Per-run randomized directory: the fallback strategy below verifies Authenticode and then
    # elevates the downloaded installer (-Verb RunAs). A fixed, predictable path would let a
    # co-resident same-user process pre-watch it and swap the binary between the signature
    # check and the elevated launch; a random name removes the predictable target.
    $tempDir = Join-Path $env:TEMP ("VoiceWink-Setup-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'

    # --- Attempt 1: Direct ZIP extraction (user-level, no admin) ---
    # Most reliable approach: just download and extract binaries, no script execution needed.
    Write-Host "   Installing .NET 8 Desktop Runtime (user-level, no admin needed)..." -ForegroundColor Yellow
    $userInstallOk = $false
    New-Item -ItemType Directory -Path $dotnetDir -Force | Out-Null

    try {
        $zipUrl = "https://aka.ms/dotnet/8.0/dotnet-runtime-win-x64.zip"
        $desktopZipUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.zip"
        $runtimeZip = Join-Path $tempDir "dotnet-runtime.zip"
        $desktopZip = Join-Path $tempDir "dotnet-desktop.zip"

        Write-Host "   Downloading .NET 8 base runtime (~30 MB)..." -ForegroundColor DarkGray
        Invoke-WebRequest -Uri $zipUrl -OutFile $runtimeZip -UseBasicParsing
        Write-Host "   Downloading .NET 8 Desktop runtime (~60 MB)..." -ForegroundColor DarkGray
        Invoke-WebRequest -Uri $desktopZipUrl -OutFile $desktopZip -UseBasicParsing

        if ((Test-Path $runtimeZip) -and (Test-Path $desktopZip)) {
            Write-Host "   Extracting to $dotnetDir..." -ForegroundColor DarkGray
            # Extract base runtime first, then desktop overlay (overwrite existing)
            Add-Type -AssemblyName System.IO.Compression
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            foreach ($zipPath in @($runtimeZip, $desktopZip)) {
                $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
                try {
                    foreach ($entry in $archive.Entries) {
                        $destPath = Join-Path $dotnetDir $entry.FullName
                        $destDir = Split-Path $destPath -Parent
                        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
                        if ($entry.FullName[-1] -ne '/' -and $entry.FullName[-1] -ne '\') {
                            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destPath, $true)
                        }
                    }
                } finally { $archive.Dispose() }
            }

            $localDotnetExe = Join-Path $dotnetDir "dotnet.exe"
            if (Test-Path $localDotnetExe) {
                # Verify Authenticode signature on extracted dotnet.exe — guards against MITM on aka.ms downloads.
                # ZIPs have no Authenticode slot, so verification must happen post-extract on the PE file.
                $sig = Get-AuthenticodeSignature $localDotnetExe
                if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
                    Write-Host "   dotnet.exe Authenticode check FAILED (Status=$($sig.Status), Subject=$($sig.SignerCertificate.Subject)) — aborting." -ForegroundColor Red
                    Remove-Item -Recurse -Force $dotnetDir -ErrorAction SilentlyContinue
                    throw "dotnet.exe Authenticode verification failed"
                }
                Write-Host "   dotnet.exe Authenticode: Valid (Microsoft Corporation)." -ForegroundColor DarkGray
                $runtimeCheck = & $localDotnetExe --list-runtimes 2>$null | Select-String "Microsoft.WindowsDesktop.App 8\."
                if ($runtimeCheck) {
                    $userInstallOk = $true
                    Write-Host "   .NET 8 Desktop Runtime: installed to $dotnetDir" -ForegroundColor Green
                } else {
                    Write-Host "   ZIP extracted but Desktop Runtime not detected." -ForegroundColor Yellow
                }
            }
        } else {
            Write-Host "   ZIP download failed." -ForegroundColor Yellow
        }
    } catch {
        Write-Host "   Direct ZIP install error: $_" -ForegroundColor Yellow
    }

    # --- Attempt 2: dotnet-install.ps1 fallback (user-level, no admin) ---
    if (-not $userInstallOk) {
        Write-Host "   Trying dotnet-install.ps1 fallback..." -ForegroundColor Yellow
        try {
            $installScript = Join-Path $tempDir "dotnet-install.ps1"
            Write-Host "   Downloading dotnet-install.ps1..." -ForegroundColor DarkGray
            Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installScript -UseBasicParsing

            if (-not (Test-Path $installScript)) {
                throw "Download failed -- dotnet-install.ps1 not found at $installScript"
            }

            # Verify the downloaded install script is Microsoft-signed before executing.
            $scriptSig = Get-AuthenticodeSignature $installScript
            if ($scriptSig.Status -ne 'Valid' -or $scriptSig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
                Write-Host "   dotnet-install.ps1 Authenticode check FAILED (Status=$($scriptSig.Status)) — aborting." -ForegroundColor Red
                throw "dotnet-install.ps1 Authenticode verification failed"
            }
            Write-Host "   dotnet-install.ps1 Authenticode: Valid (Microsoft Corporation)." -ForegroundColor DarkGray

            Write-Host "   Running dotnet-install.ps1 (this may take a minute)..." -ForegroundColor DarkGray

            # Run in a child process with explicit -ExecutionPolicy Bypass to avoid
            # security software blocking downloaded scripts in the current session.
            $escapedScript = $installScript -replace "'", "''"
            $escapedDir = $dotnetDir -replace "'", "''"
            $psCommand = "`$ProgressPreference = 'SilentlyContinue'; & '$escapedScript' -Runtime windowsdesktop -Channel 8.0 -InstallDir '$escapedDir' -NoPath"
            $installOutput = & powershell.exe -ExecutionPolicy Bypass -NoProfile -Command $psCommand 2>&1
            foreach ($line in $installOutput) {
                $msg = "$line"
                if ($msg -match "Error|fail|exception" -and $msg -notmatch "ErrorAction") {
                    Write-Host "   $msg" -ForegroundColor Yellow
                } else {
                    Write-Host "   $msg" -ForegroundColor DarkGray
                }
            }

            $localDotnetExe = Join-Path $dotnetDir "dotnet.exe"
            if (Test-Path $localDotnetExe) {
                $runtimeCheck = & $localDotnetExe --list-runtimes 2>$null | Select-String "Microsoft.WindowsDesktop.App 8\."
                if ($runtimeCheck) {
                    $userInstallOk = $true
                    Write-Host "   .NET 8 Desktop Runtime: installed to $dotnetDir" -ForegroundColor Green
                } else {
                    Write-Host "   dotnet.exe found but Desktop Runtime 8.x not detected." -ForegroundColor Yellow
                    $allRuntimes = & $localDotnetExe --list-runtimes 2>$null
                    if ($allRuntimes) {
                        Write-Host "   Installed runtimes:" -ForegroundColor DarkGray
                        foreach ($rt in $allRuntimes) { Write-Host "     $rt" -ForegroundColor DarkGray }
                    }
                }
            } else {
                Write-Host "   dotnet.exe was not created at $localDotnetExe" -ForegroundColor Yellow
            }
        } catch {
            Write-Host "   dotnet-install.ps1 error: $_" -ForegroundColor Yellow
        }
    }

    # --- Attempt 3: System-level install with admin (last resort, UAC prompt) ---
    if (-not $userInstallOk) {
        Write-Host ""
        Write-Host "   User-level installs did not succeed." -ForegroundColor Yellow
        Write-Host "   Falling back to system installer (requires admin/UAC)..." -ForegroundColor Yellow
        try {
            $dotnetUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
            $dotnetInstaller = Join-Path $tempDir "dotnet-desktop-runtime-8.0.exe"
            Write-Host "   Downloading .NET 8 Desktop Runtime installer..." -ForegroundColor Yellow
            Invoke-WebRequest -Uri $dotnetUrl -OutFile $dotnetInstaller -UseBasicParsing
            # Verify Authenticode BEFORE elevating — this is the ONLY strategy that runs a
            # downloaded artifact as admin (-Verb RunAs), so a tampered aka.ms redirect/MITM here
            # would grant the attacker administrator execution. Mirrors the signature checks the
            # two preceding (user-level) strategies already apply to dotnet.exe / dotnet-install.ps1.
            $installerSig = Get-AuthenticodeSignature $dotnetInstaller
            if ($installerSig.Status -ne 'Valid' -or $installerSig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
                Write-Host "   .NET installer Authenticode check FAILED (Status=$($installerSig.Status), Subject=$($installerSig.SignerCertificate.Subject)) — refusing to elevate." -ForegroundColor Red
                Remove-Item $dotnetInstaller -Force -ErrorAction SilentlyContinue
                throw ".NET installer Authenticode verification failed"
            }
            Write-Host "   .NET installer Authenticode: Valid (Microsoft Corporation)." -ForegroundColor DarkGray
            Write-Host "   Installing (a UAC prompt may appear)..." -ForegroundColor Yellow

            # The check above runs at MEDIUM integrity, so on its own it is advisory: any
            # same-user process could swap the file between that check and an elevated launch
            # (TOCTOU — Codex review, PR #162). The elevated side therefore re-verifies what it
            # actually runs: a stub passed via -EncodedCommand (baked into the process arguments,
            # so it cannot be tampered on disk) copies the installer into a staging directory
            # CREATED ATOMICALLY with an Administrators+SYSTEM-only security descriptor (no
            # create-then-restrict window; ACL verified before the copy — a pre-existing dir
            # fails verification), re-checks the Microsoft Authenticode signature on that
            # protected copy, and only then executes it. The source path travels as base64 —
            # inert data, never elevated PowerShell syntax, whatever %TEMP% contains.
            # Exit 23 = elevated signature check failed; 24 = staging lockdown failed.
            $stubTemplate = @'
$ErrorActionPreference = "Stop"
$stage = $null
try {
    $src = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("__SOURCE_B64__"))
    $stage = Join-Path $env:ProgramData ("VoiceWink-Setup-" + [Guid]::NewGuid().ToString("N"))
    $sec = New-Object System.Security.AccessControl.DirectorySecurity
    $sec.SetAccessRuleProtection($true, $false)
    foreach ($sidValue in @("S-1-5-32-544", "S-1-5-18")) {
        $sid = New-Object System.Security.Principal.SecurityIdentifier($sidValue)
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $sid, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
        $sec.AddAccessRule($rule)
    }
    [System.IO.Directory]::CreateDirectory($stage, $sec) | Out-Null
    $acl = Get-Acl -LiteralPath $stage
    if (-not $acl.AreAccessRulesProtected) { exit 24 }
    foreach ($entry in $acl.Access) {
        $entrySid = $entry.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value
        if (@("S-1-5-32-544", "S-1-5-18") -notcontains $entrySid) { exit 24 }
    }
    $exe = Join-Path $stage "windowsdesktop-runtime.exe"
    Copy-Item -LiteralPath $src -Destination $exe -Force
    $sig = Get-AuthenticodeSignature -LiteralPath $exe
    if ($sig.Status -ne "Valid" -or $sig.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") { exit 23 }
    $p = Start-Process -FilePath $exe -ArgumentList "/install","/quiet","/norestart" -Wait -PassThru
    exit $p.ExitCode
}
finally {
    if ($stage -and (Test-Path $stage)) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
'@
            $sourceB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($dotnetInstaller))
            $stub = $stubTemplate.Replace('__SOURCE_B64__', $sourceB64)
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($stub))
            $proc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-EncodedCommand",$encoded -Verb RunAs -Wait -PassThru
            if ($proc.ExitCode -eq 23) {
                Write-Host "   Elevated Authenticode re-check FAILED — the staged installer was not a valid Microsoft binary. Nothing was installed." -ForegroundColor Red
            } elseif ($proc.ExitCode -eq 24) {
                Write-Host "   Could not lock down the elevated staging directory — refusing to install." -ForegroundColor Red
            } elseif ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 3010) {
                $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
                $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
                $env:Path = "$machinePath;$userPath"
                $systemHasRuntime = $true
                Write-Host "   .NET 8 Desktop Runtime: installed (system-wide)" -ForegroundColor Green
            } else {
                Write-Host "   Installer exited with code $($proc.ExitCode)." -ForegroundColor Yellow
            }
        } catch {
            Write-Host "   Could not install with admin: $_" -ForegroundColor Yellow
        }
    }

    # --- Final check ---
    if (-not $userInstallOk -and -not $systemHasRuntime) {
        Write-Host ""
        Write-Host "   WARNING: .NET 8 Desktop Runtime could not be installed automatically." -ForegroundColor Red
        Write-Host "   VoiceWink will not work without it." -ForegroundColor Red
        Write-Host ""
        Write-Host "   Please install it manually from:" -ForegroundColor Yellow
        Write-Host "   https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
        Write-Host "   Download: '.NET Desktop Runtime 8.x (x64)'" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Press Enter to continue..." -NoNewline; $null = Read-Host
    }

    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

# Determine which dotnet to bake into the VBS launcher. Capture the *absolute*
# path of the validated host — bare "dotnet" relies on PATH, which on
# Windows-on-ARM resolves to an arm64 host that lacks Microsoft.WindowsDesktop.App
# and would silently fail under wscript's hidden window.
$localDotnet = Join-Path $dotnetDir "dotnet.exe"
if ($systemHasRuntime) {
    $resolvedSystem = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($resolvedSystem -and $resolvedSystem.Source) {
        $dotnetExe = $resolvedSystem.Source
    } else {
        $dotnetExe = "dotnet"
    }
} elseif (Test-Path $localDotnet) {
    $dotnetExe = $localDotnet
} else {
    $dotnetExe = "dotnet"
}

# -- Step 2: Ensure Windows App SDK Runtime --------------------------------

Write-Host "[2/6] Checking Windows App SDK Runtime..."
# The app requires four Windows App SDK 1.6 packages. Check each individually --
# a broad wildcard can match leftover packages from partial uninstalls or other versions.
$minVersion = [Version]"6000.519.329.0"
$requiredPkgs = @(
    @{ Label = "Framework";  Pattern = "Microsoft.WindowsAppRuntime.1.6" },
    @{ Label = "Main";       Pattern = "*WinAppRuntime.Main.1.6*" },
    @{ Label = "DDLM";       Pattern = "*WinAppRuntime.DDLM.6000*" },
    @{ Label = "Singleton";  Pattern = "*WinAppRuntime.Singleton*" }
)
$allPresent = $true
foreach ($req in $requiredPkgs) {
    $pkg = Get-AppxPackage $req.Pattern -ErrorAction SilentlyContinue |
        Where-Object { [Version]$_.Version -ge $minVersion } |
        Select-Object -First 1
    if ($pkg) {
        Write-Host "   $($req.Label): OK (v$($pkg.Version))" -ForegroundColor Green
    } else {
        Write-Host "   $($req.Label): missing" -ForegroundColor Yellow
        $allPresent = $false
    }
}
if ($allPresent) {
    Write-Host "   Windows App SDK Runtime 1.6: OK" -ForegroundColor Green
} else {
    $runtimeDir = Join-Path $SourceDir "runtime"
    if (Test-Path $runtimeDir) {
        $msixFiles = Get-ChildItem $runtimeDir -Filter "*.msix" -ErrorAction SilentlyContinue
        if ($msixFiles) {
            $msixOk = $false
            foreach ($msix in $msixFiles) {
                try {
                    Add-AppxPackage -Path $msix.FullName -ErrorAction Stop
                    Write-Host "   Installed: $($msix.Name)" -ForegroundColor Green
                    $msixOk = $true
                } catch {
                    $msg = "$_"
                    # 0x80073D06 = higher version installed; 0x80073CFB = already installed
                    if ($msg -match "0x80073D06|higher version.*already installed") {
                        Write-Host "   $($msix.Name): newer version already present" -ForegroundColor DarkGray
                        $msixOk = $true
                    } elseif ($msg -match "0x80073CFB|already installed") {
                        Write-Host "   $($msix.Name): already installed" -ForegroundColor DarkGray
                        $msixOk = $true
                    } else {
                        Write-Host "   $($msix.Name): $msg" -ForegroundColor Yellow
                    }
                }
            }
            # Verify all required packages are actually present after install attempts
            $verifyOk = $true
            foreach ($req in $requiredPkgs) {
                $vpkg = Get-AppxPackage $req.Pattern -ErrorAction SilentlyContinue |
                    Where-Object { [Version]$_.Version -ge $minVersion } |
                    Select-Object -First 1
                if (-not $vpkg) { $verifyOk = $false }
            }
            if ($verifyOk) {
                Write-Host "   Windows App SDK Runtime 1.6: OK" -ForegroundColor Green
            } else {
                Write-Host ""
                Write-Host "   WARNING: Some Windows App SDK Runtime packages are still missing." -ForegroundColor Red
                Write-Host "   VoiceWink may not start without them." -ForegroundColor Red
                Write-Host "   Try installing manually: https://aka.ms/windowsappsdk/1.6/latest/windowsappruntimeinstall-x64.exe" -ForegroundColor Yellow
            }
        }
    } else {
        Write-Host "   WARNING: Windows App SDK Runtime not found and no runtime packages bundled." -ForegroundColor Yellow
        Write-Host "   VoiceWink may not start. Download from: https://aka.ms/windowsappsdk/1.6/latest/windowsappruntimeinstall-x64.exe" -ForegroundColor Yellow
    }
}

# -- Step 3: Kill running instance ----------------------------------------

Write-Host "[3/6] Checking for running instance..."
$killedPids = @()
try {
    $wmiProcs = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue
    foreach ($p in $wmiProcs) {
        if ($p.CommandLine -match "VoiceWink") {
            Write-Host "   Stopping running VoiceWink (PID $($p.ProcessId))..."
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            $killedPids += $p.ProcessId
        }
    }
} catch {
    # Fallback: CIM query unavailable — use wmic which supports commandline filtering.
    # Can't track PIDs in this path, so use a fixed delay instead of Wait-Process.
    wmic process where "name='dotnet.exe' and commandline like '%VoiceWink%'" call terminate 2>$null | Out-Null
    Start-Sleep -Seconds 3
}
# Wait for killed processes to fully exit and release file handles
foreach ($killedPid in $killedPids) {
    try { Wait-Process -Id $killedPid -Timeout 10 -ErrorAction SilentlyContinue } catch { }
}
if ($killedPids.Count -gt 0) { Start-Sleep -Seconds 1 }

# -- Step 4: Copy app files -----------------------------------------------

Write-Host "[4/6] Installing to $InstallDir..."
$appFiles = Join-Path $SourceDir "app"

if (Test-Path $InstallDir) {
    # Upgrade: remove old app binaries but keep user data. The exclude list must cover every
    # user-owned file/dir: the settings backup + corrupt-quarantine pair with settings.json,
    # the SQLite -wal/-shm/-journal sidecars pair with voicewink.db (deleting -wal loses
    # un-checkpointed transactions), and Images/References are user media like Recordings.
    $keepOnUpgrade = @(
        "Logs","Models","Recordings","Images","References","dotnet",
        "settings.json","settings.json.bak","settings.json.corrupt",
        "voicewink.db","voicewink.db-wal","voicewink.db-shm","voicewink.db-journal"
    )
    Get-ChildItem $InstallDir -Force -Exclude $keepOnUpgrade |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Copy new app files
try {
    Copy-Item -Path "$appFiles\*" -Destination $InstallDir -Recurse -Force
} catch {
    Write-Host "   WARNING: Some files could not be copied (may be locked): $_" -ForegroundColor Yellow
    Write-Host "   Try closing VoiceWink and running the installer again." -ForegroundColor Yellow
}

# Create VBS launcher (no console window).
$vbsPath = Join-Path $InstallDir "VoiceWink.vbs"
$vbsContent = @"
Set WshShell = CreateObject("WScript.Shell")
WshShell.CurrentDirectory = "$InstallDir"
WshShell.Run """$dotnetExe"" VoiceWink.dll", 0, False
"@
Set-Content -Path $vbsPath -Value $vbsContent -Encoding Default

# -- Step 5: Create shortcuts ---------------------------------------------

Write-Host "[5/6] Creating shortcuts..."

$WshShell = New-Object -ComObject WScript.Shell

# Start Menu folder with VoiceWink + Uninstall
$startMenuFolder = Join-Path $StartMenu $AppName
New-Item -ItemType Directory -Path $startMenuFolder -Force | Out-Null

$startMenuLink = Join-Path $startMenuFolder "$AppName.lnk"
$shortcut = $WshShell.CreateShortcut($startMenuLink)
$shortcut.TargetPath = "wscript.exe"
$shortcut.Arguments = "`"$vbsPath`""
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation = "$(Join-Path $InstallDir 'Assets\appicon.ico'),0"
$shortcut.Description = "Voice-to-text for Windows"
$shortcut.Save()

$uninstallLink = Join-Path $startMenuFolder "Uninstall $AppName.lnk"
$shortcut = $WshShell.CreateShortcut($uninstallLink)
$shortcut.TargetPath = "powershell.exe"
$shortcut.Arguments = "-ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'Uninstall.ps1')`""
$shortcut.WorkingDirectory = $env:TEMP
$shortcut.IconLocation = "shell32.dll,131"
$shortcut.Description = "Uninstall VoiceWink"
$shortcut.Save()

Write-Host "   Start Menu: OK (VoiceWink + Uninstall)"

# Desktop shortcut (ask)
$desktop = [Environment]::GetFolderPath("Desktop")
$answer = Read-Host "   Create desktop shortcut? (Y/n)"
if ($answer -ne "n" -and $answer -ne "N") {
    $desktopLink = Join-Path $desktop "$AppName.lnk"
    $shortcut = $WshShell.CreateShortcut($desktopLink)
    $shortcut.TargetPath = "wscript.exe"
    $shortcut.Arguments = "`"$vbsPath`""
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.IconLocation = "$(Join-Path $InstallDir 'Assets\appicon.ico'),0"
    $shortcut.Description = "Voice-to-text for Windows"
    $shortcut.Save()
    Write-Host "   Desktop: OK"
}

# Auto-start at login: only set the registry on fresh install (no existing settings.json).
# On upgrades, respect the user's existing preference (they may have disabled it in Settings).
$settingsFile = Join-Path $InstallDir "settings.json"
$isFreshInstall = -not (Test-Path $settingsFile)
if ($isFreshInstall) {
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "VoiceWink" -Value "wscript.exe `"$vbsPath`""
    Write-Host "   Auto-start at login: enabled (change in Settings or onboarding)"
} else {
    Write-Host "   Auto-start at login: keeping existing preference"
}

# -- Step 6: Create uninstaller -------------------------------------------

$uninstallScript = Join-Path $InstallDir "Uninstall.ps1"
$uninstallContent = @'
# Default install dir (PS 5.1 compatible -- no expression-if)
if ($args[0]) { $installDir = $args[0] } else { $installDir = Join-Path $env:LOCALAPPDATA "VoiceWink" }

# --- Safety: resolve path and validate before doing anything destructive ---
# GetFullPath first (resolves ..\, relative paths), then TrimEnd for consistent comparison
try { $installDir = [System.IO.Path]::GetFullPath($installDir).TrimEnd('\') } catch {
    Write-Host "ERROR: Invalid path: $installDir" -ForegroundColor Red
    Write-Host "Press Enter to close..." -NoNewline; $null = Read-Host
    exit 1
}
# Block drive roots, system directories, and user profile root
$dangerousPaths = @(
    $env:SystemRoot,                          # C:\Windows
    $env:ProgramFiles,                        # C:\Program Files
    ${env:ProgramFiles(x86)},                 # C:\Program Files (x86)
    $env:SystemDrive + '\',                   # C:\
    $env:USERPROFILE,                         # C:\Users\<name>
    $env:LOCALAPPDATA,                        # %LOCALAPPDATA% itself (not subdirs)
    $env:APPDATA,                             # %APPDATA% itself
    $env:TEMP                                 # %LOCALAPPDATA%\Temp (inside LOCALAPPDATA!)
)
foreach ($dp in $dangerousPaths) {
    if ($dp -and $installDir -eq [System.IO.Path]::GetFullPath($dp).TrimEnd('\')) {
        Write-Host "ERROR: Refusing to uninstall -- target path is a system directory: $installDir" -ForegroundColor Red
        Write-Host "Press Enter to close..." -NoNewline; $null = Read-Host
        exit 1
    }
}
# Must be inside %LOCALAPPDATA% (the only place VoiceWink installs to)
$localAppData = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\')
if (-not $installDir.StartsWith($localAppData + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "ERROR: Refusing to uninstall -- path is not inside %LOCALAPPDATA%: $installDir" -ForegroundColor Red
    Write-Host "Press Enter to close..." -NoNewline; $null = Read-Host
    exit 1
}
# Must contain VoiceWink.dll (fingerprint check -- proves it's actually our install)
if ((Test-Path $installDir) -and -not (Test-Path (Join-Path $installDir "VoiceWink.dll"))) {
    Write-Host "ERROR: Refusing to uninstall -- directory does not contain VoiceWink.dll: $installDir" -ForegroundColor Red
    Write-Host "   This does not look like a VoiceWink installation." -ForegroundColor Yellow
    Write-Host "Press Enter to close..." -NoNewline; $null = Read-Host
    exit 1
}

# Self-relocate to temp so the install directory isn't locked during deletion
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($scriptDir -eq $installDir) {
    try {
        $tempScript = Join-Path $env:TEMP "VoiceWink-Uninstall.ps1"
        Copy-Item $MyInvocation.MyCommand.Path $tempScript -Force
        Start-Process powershell -WorkingDirectory $env:TEMP -ArgumentList "-ExecutionPolicy Bypass -File `"$tempScript`" `"$installDir`""
        exit
    } catch {
        Write-Host "ERROR: Failed to relocate uninstaller: $_" -ForegroundColor Red
        Write-Host ""
        Write-Host "Press Enter to close..." -NoNewline; $null = Read-Host
        exit 1
    }
}

Write-Host ""
Write-Host "=== Uninstalling VoiceWink ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "   Install directory: $installDir"

# Kill running instance
Write-Host "   Checking for running instance..."
$killedPids = @()
try {
    $wmiProcs = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue
    foreach ($p in $wmiProcs) {
        if ($p.CommandLine -match "VoiceWink") {
            Write-Host "   Stopping VoiceWink (PID $($p.ProcessId))..." -ForegroundColor Yellow
            Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
            $killedPids += $p.ProcessId
        }
    }
} catch { }
if ($killedPids.Count -eq 0) { Write-Host "   No running instance found" }
# Wait for killed processes to fully exit and release file handles
foreach ($killedPid in $killedPids) {
    try { Wait-Process -Id $killedPid -Timeout 10 -ErrorAction SilentlyContinue } catch { }
}
if ($killedPids.Count -gt 0) { Start-Sleep -Seconds 1 }

# Remove Start Menu shortcuts
$startMenu = [Environment]::GetFolderPath("Programs")
$startMenuFolder = Join-Path $startMenu "VoiceWink"
if (Test-Path $startMenuFolder) {
    Remove-Item $startMenuFolder -Recurse -ErrorAction SilentlyContinue
    Write-Host "   Start Menu folder removed"
} else {
    $startMenuLink = Join-Path $startMenu "VoiceWink.lnk"
    if (Test-Path $startMenuLink) {
        Remove-Item $startMenuLink -ErrorAction SilentlyContinue
        Write-Host "   Start Menu shortcut removed"
    } else {
        Write-Host "   Start Menu: nothing to remove"
    }
}

# Remove Desktop shortcut
$desktop = [Environment]::GetFolderPath("Desktop")
$desktopLink = Join-Path $desktop "VoiceWink.lnk"
if (Test-Path $desktopLink) {
    Remove-Item $desktopLink -ErrorAction SilentlyContinue
    Write-Host "   Desktop shortcut removed"
} else {
    Write-Host "   Desktop: nothing to remove"
}

# Remove auto-start registry entry
$regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$regExists = (Get-Item -Path $regPath -ErrorAction SilentlyContinue).GetValue("VoiceWink", $null) -ne $null
if ($regExists) {
    Remove-ItemProperty -Path $regPath -Name "VoiceWink" -ErrorAction SilentlyContinue
    Write-Host "   Auto-start registry entry removed"
} else {
    Write-Host "   Auto-start: nothing to remove"
}

# Ask about user data
Write-Host ""
$keepData = Read-Host "   Keep your settings, models, and data? (Y/n)"
$removeAll = ($keepData -eq "n" -or $keepData -eq "N")

# Remove install directory
Write-Host ""
Write-Host "   Removing install directory..."
# Ensure our CWD isn't locking the install directory
Set-Location $env:TEMP
if (Test-Path $installDir) {
    if ($removeAll) {
        # Full removal -- retry up to 3 times (file handles may linger after process kill)
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            Remove-Item -Recurse -Force $installDir -ErrorAction SilentlyContinue
            if (-not (Test-Path $installDir)) { break }
            if ($attempt -lt 3) {
                Write-Host "   Retrying removal (attempt $($attempt + 1)/3)..." -ForegroundColor DarkGray
                Start-Sleep -Seconds 2
            }
        }
    } else {
        # Keep user data -- only remove app binaries and the bundled runtime. The prompt
        # promises "settings, models, and data", so EVERY user-owned file/dir must survive:
        # recordings/images/references/logs, the settings backup/quarantine paired with
        # settings.json, and the SQLite -wal/-shm/-journal sidecars paired with voicewink.db
        # (deleting -wal loses un-checkpointed transactions from the preserved database).
        $preserve = @(
            "Logs", "Models", "Recordings", "Images", "References",
            "settings.json", "settings.json.bak", "settings.json.corrupt",
            "voicewink.db", "voicewink.db-wal", "voicewink.db-shm", "voicewink.db-journal"
        )
        Get-ChildItem $installDir -Force -Exclude $preserve | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "   Preserved: settings, database, models, recordings, images, logs"
    }
    if ($removeAll) {
        if (Test-Path $installDir) {
            Write-Host "   WARNING: Could not fully remove $installDir -- some files may be locked" -ForegroundColor Yellow
        } else {
            Write-Host "   Install directory removed"
        }
    } else {
        Write-Host "   App files removed, user data preserved"
    }
} else {
    Write-Host "   Install directory: already gone"
}

Write-Host ""
Write-Host "VoiceWink has been uninstalled." -ForegroundColor Green
Write-Host ""

# Offer to remove Windows App SDK 1.6 Runtime (only the version VoiceWink installs)
# Only remove 1.6-specific packages. The Singleton is shared across SDK versions
# (1.6, 1.7, 1.8 all use it) -- removing it could break other apps.
$winAppPkgs = @((
    @(Get-AppxPackage "Microsoft.WindowsAppRuntime.1.6" -ErrorAction SilentlyContinue) +
    @(Get-AppxPackage "*WinAppRuntime.Main.1.6*" -ErrorAction SilentlyContinue) +
    @(Get-AppxPackage "*WinAppRuntime.DDLM.6000*" -ErrorAction SilentlyContinue)
) | Sort-Object -Property PackageFullName -Unique)
if ($winAppPkgs.Count -gt 0) {
    Write-Host "   The Windows App SDK 1.6 Runtime is still installed (shared framework)." -ForegroundColor DarkGray
    Write-Host "   Other apps may depend on it." -ForegroundColor DarkGray
    $removeRuntime = Read-Host "   Remove Windows App SDK 1.6 Runtime? (y/N)"
    if ($removeRuntime -eq "y" -or $removeRuntime -eq "Y") {
        foreach ($pkg in $winAppPkgs) {
            try {
                Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
                Write-Host "   Removed: $($pkg.Name)" -ForegroundColor Green
            } catch {
                Write-Host "   Could not remove $($pkg.Name): $_" -ForegroundColor Yellow
            }
        }
    }
}

Write-Host ""
Write-Host "Press Enter to close..." -NoNewline; $null = Read-Host
'@
Set-Content -Path $uninstallScript -Value $uninstallContent

Write-Host "[6/6] Done!" -ForegroundColor Green
Write-Host ""
Write-Host "   Launch VoiceWink from the Start Menu."
Write-Host "   To uninstall, use 'Uninstall VoiceWink' in the Start Menu."
Write-Host ""

# Launch
$answer = Read-Host "Launch VoiceWink now? (Y/n)"
if ($answer -ne "n" -and $answer -ne "N") {
    Start-Process "wscript.exe" -ArgumentList "`"$vbsPath`""
    Write-Host "VoiceWink started!" -ForegroundColor Green
}

} catch {
    Write-Host ""
    Write-Host "ERROR: Installation failed: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "Press Enter to close..." -NoNewline; $null = Read-Host
    exit 1
}
