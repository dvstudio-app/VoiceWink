<#
.SYNOPSIS
Print the resolved release version, or fail with the reason. The CLI entry point over
scripts/lib/release-version.ps1.

.DESCRIPTION
This exists so the shell chains (installer/release-update.sh, installer/build-portable.sh) can use
the SAME resolver as the PowerShell chain instead of a second implementation. There used to be a
scripts/lib/release-version.sh; it was deleted because a second implementation of one decision is
drift waiting to happen — it disagreed with its twin on whitespace handling, and a comment is an XML
concept that a line-oriented shell cannot reliably see at all.

Calling pwsh from those scripts costs nothing new: both already hard-require it (`require_cmd pwsh`)
and already invoke it for the legal gate, the UAT gate, the runtime fetch and the payload check.

CONTRACT — depended on by the shell callers:
  * On success: the version, and NOTHING else, on stdout. Exit 0.
  * On failure: every reason on stderr, nothing on stdout. Exit 1.
Diagnostics must never go to stdout, because the caller captures stdout AS the version.

.PARAMETER Project
Path to VoiceWink.csproj. Accepts a Windows or a forward-slash path — the two shell callers
disagree about which form they hand pwsh (build-portable.sh converts, release-update.sh does not),
so this normalises rather than inheriting either convention.

.PARAMETER Override
Expected version. CHECKED against the project, not substituted for it — see the resolver's own
header for why an override cannot be honoured.

.PARAMETER SkipMirrorCheck
Do not verify <AssemblyVersion>/<FileVersion> mirror <Version>. For callers that are themselves
about to rewrite the mirrors (scripts/bump-version.ps1 reads BEFORE it writes, so mid-bump the
mirrors legitimately still hold the old value).

.EXAMPLE
pwsh -NoProfile -File scripts/resolve-release-version.ps1 -Project src/VoiceWink/VoiceWink.csproj
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [AllowEmptyString()][string]$Override = '',
    [switch]$SkipMirrorCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'lib/release-version.ps1')

# Resolve-Path over Test-Path + Join: the caller may pass a relative path, and XmlDocument.Load
# resolves a relative path against the PROCESS working directory, which for a pwsh spawned from
# Git Bash is not necessarily the repo root.
$resolved = $null
try {
    $resolved = (Resolve-Path -LiteralPath $Project -ErrorAction Stop).Path
} catch {
    [Console]::Error.WriteLine("ERROR: project file not found: $Project")
    exit 1
}

$result = Resolve-ReleaseVersion `
    -ProjectPath $resolved `
    -Override $Override `
    -ProjectLabel (Split-Path $resolved -Leaf) `
    -SkipMirrorCheck:$SkipMirrorCheck

if (-not $result.Ok) {
    foreach ($line in $result.Errors) { [Console]::Error.WriteLine($line) }
    exit 1
}

# Write-Output would append the host's line ending and could be reshaped by a caller's formatting;
# this writes exactly the version plus one newline, which is what the shell callers strip.
[Console]::Out.Write($result.Version + "`n")
exit 0
