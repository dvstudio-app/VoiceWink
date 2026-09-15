#!/usr/bin/env pwsh
# Build-time generator for VoiceWink's update-feed config.
#
# Emits a compiled constants class (UpdateFeedConfig) from the non-secret channel passed as -Channel
# and - for a GATED channel only - the Cloudflare Access service-token credentials read from a
# GITIGNORED creds file (NEVER passed on the command line, so the secret cannot leak into process
# args / build logs). Invoked from VoiceWink.csproj's GenerateUpdateFeedConfig target before compile.
#
# GATED vs PUBLIC is decided HERE, from the channel name, through the repo's ONE predicate
# (scripts/lib/release-channel.ps1, Test-IsPublicReleaseChannel: any '-stable' suffix is public):
#   - a tester ring (win-x64-beta) sits behind Cloudflare Access, so its build bakes the token and is
#     inert (IsConfigured == false) when the credentials are missing;
#   - a PUBLIC channel is ungated and bakes NO credentials at all - the creds file is not even opened,
#     so a present file cannot leak into a public installer (public-channel change, 2026-09-09).
# Dev builds (no channel) emit empty constants and stay inert regardless of the creds file.
#
# ASCII-ONLY: MSBuild's <Exec> runs this under Windows PowerShell 5.1, which reads a UTF-8-no-BOM
# .ps1 in the legacy codepage. A non-ASCII char (em-dash, arrow) would mangle and can break the
# parse. Keep this file ASCII; the user-invoked release-update.* scripts run under pwsh/bash.
# The dot-sourced predicate file carries non-ASCII in COMMENTS only; -SelfTest runs under both shells
# in CI precisely so a 5.1 parse fault there surfaces on a PR rather than inside a release build.
#
# -SelfTest: table-driven check of the emitted constants. Exit 0 = every row passed, 1 = a row failed.
[CmdletBinding(DefaultParameterSetName = 'Generate')]
param(
    [Parameter(ParameterSetName = 'Generate')][string]$Channel = "",
    [Parameter(ParameterSetName = 'Generate')][string]$CredsFile = "",
    [Parameter(ParameterSetName = 'Generate', Mandatory = $true)][string]$OutFile,
    [Parameter(ParameterSetName = 'SelfTest', Mandatory = $true)][switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

# What kind of feed does this channel have - 'Public' (ungated), 'Gated' (Cloudflare Access; the exact
# names Get-GatedReleaseChannels lists) or 'Unknown'? The predicate is evaluated in a CHILD scope: its
# file sets StrictMode Latest, which must not leak into this script. The closed-set guard is
# load-bearing (the PR #836 lesson): under PowerShell an unexpected result - $null if the function were
# ever missing, an object, an empty string - would otherwise be coerced into SOME class silently.
function Get-FeedChannelClass([string]$ChannelName) {
    $lib = Join-Path $PSScriptRoot 'lib\release-channel.ps1'
    $class = & { . $lib; Get-ReleaseChannelClass -Channel $ChannelName }
    if (($class -isnot [string]) -or ($class -notin @('Public', 'Gated', 'Unknown'))) {
        throw 'generate-update-feed-config: channel-class predicate returned an unexpected result.'
    }
    return $class
}

# Tolerate values wrapped in single or double quotes in the creds file.
function Remove-WrappingQuotes([string]$v) {
    if ($v.Length -ge 2 -and (($v[0] -eq '"' -and $v[-1] -eq '"') -or ($v[0] -eq "'" -and $v[-1] -eq "'"))) {
        return $v.Substring(1, $v.Length - 2)
    }
    return $v
}

# Fail fast on malformed values. A CR/LF/control char would compile into the constant and then
# make HttpRequestHeaders.Add throw at runtime (or enable header injection). Reject here so a broken
# creds file breaks the build loudly.
function Assert-CredentialClean([string]$value, [string]$name) {
    # Trim already removed edge whitespace; reject ANY remaining whitespace (incl. internal spaces)
    # or control char - Cloudflare service-token credentials contain none, so this is malformed.
    if ($value -match '[\s\x00-\x1F\x7F]') {
        throw "generate-update-feed-config: '$name' contains whitespace or a control character. Cloudflare service-token credentials have none - fix installer/.update-feed-credentials."
    }
}

# Read both credentials (trimmed, unquoted, asserted clean). A whitespace-only value collapses to
# empty (so IsConfigured == false / inert) rather than baking blank-but-"present" credentials that
# Cloudflare would reject at runtime.
function Read-FeedCredentials([string]$Path) {
    $clientId = ""
    $clientSecret = ""
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*CF_ACCESS_CLIENT_ID\s*=\s*(.+?)\s*$') { $clientId = $Matches[1] }
        elseif ($line -match '^\s*CF_ACCESS_CLIENT_SECRET\s*=\s*(.+?)\s*$') { $clientSecret = $Matches[1] }
    }
    $clientId = (Remove-WrappingQuotes $clientId).Trim()
    $clientSecret = (Remove-WrappingQuotes $clientSecret).Trim()
    Assert-CredentialClean $clientId 'CF_ACCESS_CLIENT_ID'
    Assert-CredentialClean $clientSecret 'CF_ACCESS_CLIENT_SECRET'
    return @{ ClientId = $clientId; ClientSecret = $clientSecret }
}

# Escape for a C# verbatim string literal (@"..."): only the double-quote needs doubling.
function ConvertTo-CSharpVerbatim([string]$s) { return ($s -replace '"', '""') }

# The generated C# for a channel + creds file. Pure: nothing is written here.
function New-UpdateFeedConfigContent([string]$ChannelName, [string]$CredsPath) {
    $ChannelName = $ChannelName.Trim()
    if ($ChannelName -and ($ChannelName -notmatch '^[A-Za-z0-9._-]+$')) {
        throw "generate-update-feed-config: channel '$ChannelName' has invalid characters (allowed: A-Z a-z 0-9 . _ -)."
    }
    $class = Get-FeedChannelClass $ChannelName
    if ($ChannelName -and ($class -eq 'Unknown')) {
        # Refuse at BUILD time, not just in the release script: since 2026-09-09 Cloudflare Access covers
        # only the listed tester prefixes, so a bake for any other named channel would put the tester
        # token on an unprotected prefix, or into a public pack whose name lacks the '-stable' suffix.
        throw "generate-update-feed-config: channel '$ChannelName' is neither a public '-stable' channel nor a known gated tester ring (Get-GatedReleaseChannels in scripts/lib/release-channel.ps1). Add it there in the same change that adds its Cloudflare Access path."
    }
    # An EMPTY channel (dev build) classes as Unknown too and stays gated + credential-free: inert via
    # Channel.Length regardless of the creds file.
    $gated = ($class -ne 'Public')
    $clientId = ""
    $clientSecret = ""
    # Credentials are read ONLY for a gated channel that is actually set. A public channel never opens
    # the file; a dev build (no channel) never opens it either.
    if ($gated -and $ChannelName -and $CredsPath -and (Test-Path -LiteralPath $CredsPath)) {
        $c = Read-FeedCredentials $CredsPath
        $clientId = $c.ClientId
        $clientSecret = $c.ClientSecret
    }
    $gatedLiteral = if ($gated) { 'true' } else { 'false' }
    return @"
// <auto-generated/> Build-time update-feed config.
// Generated by scripts/generate-update-feed-config.ps1 on each build. DO NOT EDIT or commit.
// Credentials are read from a gitignored creds file at build time for GATED (tester) channels only;
// empty in dev builds and on PUBLIC channels.
namespace VoiceWink.Services.Updates;

internal static class UpdateFeedConfig
{
    public const string Channel = @"$(ConvertTo-CSharpVerbatim $ChannelName)";

    /// <summary>True when this build's feed sits behind Cloudflare Access (a tester ring). Public channels - any '-stable' suffix, per scripts/lib/release-channel.ps1 - are ungated and bake NO credentials.</summary>
    public const bool FeedIsGated = $gatedLiteral;

    public const string CfAccessClientId = @"$(ConvertTo-CSharpVerbatim $clientId)";
    public const string CfAccessClientSecret = @"$(ConvertTo-CSharpVerbatim $clientSecret)";

    /// <summary>True when this build can query its feed: a channel is baked, and - for a gated feed - both Cloudflare Access credentials are too.</summary>
    public static bool IsConfigured => Channel.Length > 0 && (!FeedIsGated || (CfAccessClientId.Length > 0 && CfAccessClientSecret.Length > 0));
}
"@
}

# Write only when the (newline-normalised) content changed, so we don't churn the file's timestamp
# and force an unnecessary recompile on every incremental build. Returns $true when it wrote.
function Write-UpdateFeedConfig([string]$Path, [string]$Content) {
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $existing = if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw } else { $null }
    $normExisting = if ($null -ne $existing) { $existing -replace "`r`n", "`n" } else { $null }
    $normContent = $Content -replace "`r`n", "`n"
    if ($normExisting -ne $normContent) {
        Set-Content -LiteralPath $Path -Value $Content -Encoding UTF8 -NoNewline
        return $true
    }
    return $false
}

function Invoke-SelfTest {
    $failures = 0
    $total = 0
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('vw-update-feed-selftest-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        # Fixture creds files. The values are FAKES with the shape of the real ones; nothing here is a secret.
        $goodCreds = Join-Path $tmp 'creds-good'
        [IO.File]::WriteAllText($goodCreds, "CF_ACCESS_CLIENT_ID=fakeid0123456789.access`nCF_ACCESS_CLIENT_SECRET=`"fakesecretABCDEF`"`n")
        $blankCreds = Join-Path $tmp 'creds-blank'
        [IO.File]::WriteAllText($blankCreds, "CF_ACCESS_CLIENT_ID=   `nCF_ACCESS_CLIENT_SECRET=`n")
        $badCreds = Join-Path $tmp 'creds-bad'
        [IO.File]::WriteAllText($badCreds, "CF_ACCESS_CLIENT_ID=has a space`nCF_ACCESS_CLIENT_SECRET=x`n")
        $missingCreds = Join-Path $tmp 'creds-missing'   # deliberately never created

        $idLine = 'CfAccessClientId = @"fakeid0123456789.access";'
        $secretLine = 'CfAccessClientSecret = @"fakesecretABCDEF";'
        $emptyId = 'CfAccessClientId = @"";'
        $emptySecret = 'CfAccessClientSecret = @"";'
        $configuredExpr = 'public static bool IsConfigured => Channel.Length > 0 && (!FeedIsGated || (CfAccessClientId.Length > 0 && CfAccessClientSecret.Length > 0));'

        # Rows. Keys avoid Hashtable member names (Count, Keys, Values, Contains...) on purpose.
        $rows = @(
            @{ N = '1 no channel + creds present: inert, nothing baked';           Ch = '';               Creds = $goodCreds;    Throws = $false; Expect = @('Channel = @"";', 'FeedIsGated = true;', $emptyId, $emptySecret) },
            @{ N = '2 beta + creds present: baked and gated';                      Ch = 'win-x64-beta';   Creds = $goodCreds;    Throws = $false; Expect = @('Channel = @"win-x64-beta";', 'FeedIsGated = true;', $idLine, $secretLine) },
            @{ N = '3 beta + creds absent: gated, empty (inert)';                  Ch = 'win-x64-beta';   Creds = $missingCreds; Throws = $false; Expect = @('FeedIsGated = true;', $emptyId, $emptySecret) },
            @{ N = '4 stable + creds present: file ignored, ungated';              Ch = 'win-x64-stable'; Creds = $goodCreds;    Throws = $false; Expect = @('Channel = @"win-x64-stable";', 'FeedIsGated = false;', $emptyId, $emptySecret); Forbid = @('fakeid0123456789', 'fakesecretABCDEF') },
            @{ N = '5 stable + creds absent: ungated';                             Ch = 'win-x64-stable'; Creds = $missingCreds; Throws = $false; Expect = @('FeedIsGated = false;', $emptyId, $emptySecret) },
            @{ N = '6 STABLE in upper case + creds present: public is case-insensitive'; Ch = 'WIN-X64-STABLE'; Creds = $goodCreds; Throws = $false; Expect = @('FeedIsGated = false;', $emptyId, $emptySecret) },
            @{ N = '7 stable + MALFORMED creds: file never parsed, so no throw';   Ch = 'win-x64-stable'; Creds = $badCreds;     Throws = $false; Expect = @('FeedIsGated = false;', $emptyId, $emptySecret) },
            @{ N = '8 beta + whitespace-only creds: empty, inert';                 Ch = 'win-x64-beta';   Creds = $blankCreds;   Throws = $false; Expect = @('FeedIsGated = true;', $emptyId, $emptySecret) },
            @{ N = '9 beta + malformed creds (internal space): throws';            Ch = 'win-x64-beta';   Creds = $badCreds;     Throws = $true },
            @{ N = '10 bad channel charset: throws';                               Ch = 'bad channel!';   Creds = $goodCreds;    Throws = $true },
            @{ N = '11 beta: the widened IsConfigured expression is emitted verbatim';   Ch = 'win-x64-beta';   Creds = $goodCreds; Throws = $false; Expect = @($configuredExpr) },
            @{ N = '12 stable: the widened IsConfigured expression is emitted verbatim'; Ch = 'win-x64-stable'; Creds = $goodCreds; Throws = $false; Expect = @($configuredExpr) },
            @{ N = '15 unknown tester name (win-x64-alpha): refused - no Access prefix covers it';  Ch = 'win-x64-alpha';  Creds = $goodCreds; Throws = $true },
            @{ N = '16 case variant of the gated name (win-x64-Beta): refused - Access paths are exact'; Ch = 'win-x64-Beta'; Creds = $goodCreds; Throws = $true },
            @{ N = '17 future tester name (win-arm64-beta) not yet listed: refused until its Access path exists'; Ch = 'win-arm64-beta'; Creds = $goodCreds; Throws = $true }
        )
        foreach ($r in $rows) {
            $total++
            $ok = $true
            $why = ''
            $content = $null
            $threw = $false
            try { $content = New-UpdateFeedConfigContent $r.Ch $r.Creds } catch { $threw = $true; $why = $_.Exception.Message }
            if ($r.Throws) {
                if (-not $threw) { $ok = $false; $why = 'expected a throw, got content' }
            } elseif ($threw) {
                $ok = $false
            } else {
                foreach ($s in @($r.Expect)) {
                    if ($content.IndexOf($s, [StringComparison]::Ordinal) -lt 0) { $ok = $false; $why = "missing: $s" }
                }
                if ($r.ContainsKey('Forbid')) {
                    foreach ($s in @($r.Forbid)) {
                        if ($content.IndexOf($s, [StringComparison]::Ordinal) -ge 0) { $ok = $false; $why = "must not contain: $s" }
                    }
                }
            }
            if ($ok) { Write-Host "  [PASS] $($r.N)" } else { Write-Host "  [FAIL] $($r.N) - $why" -ForegroundColor Red; $failures++ }
        }

        # 13: the change-detecting writer writes once and leaves an unchanged file untouched.
        $total++
        $out = Join-Path $tmp 'UpdateFeedConfig.g.cs'
        $content = New-UpdateFeedConfigContent 'win-x64-stable' $goodCreds
        $first = Write-UpdateFeedConfig $out $content
        $stamp = (Get-Item -LiteralPath $out).LastWriteTimeUtc
        Start-Sleep -Milliseconds 1200
        $second = Write-UpdateFeedConfig $out $content
        $stamp2 = (Get-Item -LiteralPath $out).LastWriteTimeUtc
        if ($first -and -not $second -and ($stamp -eq $stamp2)) {
            Write-Host "  [PASS] 13 writer: the first call writes, an identical second call leaves the file untouched"
        } else {
            Write-Host "  [FAIL] 13 writer: first=$first second=$second stampsEqual=$($stamp -eq $stamp2)" -ForegroundColor Red
            $failures++
        }
        # 14: changed content DOES rewrite.
        $total++
        $third = Write-UpdateFeedConfig $out (New-UpdateFeedConfigContent 'win-x64-beta' $goodCreds)
        if ($third) { Write-Host "  [PASS] 14 writer: changed content rewrites" } else { Write-Host "  [FAIL] 14 writer: changed content did not rewrite" -ForegroundColor Red; $failures++ }
    }
    finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($failures -eq 0) {
        Write-Host "generate-update-feed-config self-test: $total/$total rows passed (PowerShell $($PSVersionTable.PSVersion))"
        return 0
    }
    Write-Host "generate-update-feed-config self-test: $failures of $total rows FAILED (PowerShell $($PSVersionTable.PSVersion))" -ForegroundColor Red
    return 1
}

if ($SelfTest) {
    Write-Host "generate-update-feed-config.ps1 self-test" -ForegroundColor Cyan
    exit (Invoke-SelfTest)
}

$generated = New-UpdateFeedConfigContent $Channel $CredsFile
Write-UpdateFeedConfig $OutFile $generated | Out-Null
