# Shared, PURE decisions about the bundled legal texts. Dot-sourced by
# scripts/check-release-assets.ps1 and driven by scripts/test-legal-disclosure.ps1.
#
# Kept separate from the gate script for the same reason scripts/lib/release-version.ps1 is:
# a verdict function that has never been executed against a malformed input is not ready to
# gate a release, and the gate script itself does too much filesystem work to be table-driven.

Set-StrictMode -Version Latest

<#
.SYNOPSIS
Resolve the highest-numbered versioned legal file (privacy-v*.md / eula-v*.md) in a directory.

.DESCRIPTION
Sorts NUMERICALLY on the version group, never lexically: a lexical sort puts v10 before v9, and
this is the input to a gate that compares versions. Returns $null when the directory is missing
or holds no matching file — callers treat that as a finding, never as a pass.
#>
function Resolve-HighestLegalVersionFile {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        # 'privacy' or 'eula' — the filename stem before '-v<N>.md'.
        [Parameter(Mandatory = $true)][string]$Stem
    )

    if (-not (Test-Path -LiteralPath $Directory)) { return $null }

    $pattern = "^$([regex]::Escape($Stem))-v(\d+)\.md$"
    $best = Get-ChildItem -LiteralPath $Directory -Filter "$Stem-v*.md" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $pattern } |
        Sort-Object { [int]([regex]::Match($_.Name, $pattern).Groups[1].Value) } -Descending |
        Select-Object -First 1

    if ($null -eq $best) { return $null }
    return $best.FullName
}

<#
.SYNOPSIS
Extract the numeric version from a versioned legal filename. 0 when unreadable.
#>
function Get-LegalFileVersion {
    param([string]$Path, [Parameter(Mandatory = $true)][string]$Stem)

    if ([string]::IsNullOrWhiteSpace($Path)) { return 0 }
    $m = [regex]::Match((Split-Path $Path -Leaf), "^$([regex]::Escape($Stem))-v(\d+)\.md$")
    if (-not $m.Success) { return 0 }
    return [int]$m.Groups[1].Value
}

<#
.SYNOPSIS
Does this legal text disclose the automatic-INSTALL behaviour, with its opt-out?

.DESCRIPTION
A tripwire, deliberately not a legal adequacy review. It exists because the public-release gate's
privacy-VERSION check cannot see this: the queued addenda item folds the automatic-install wording INTO the
same v5 the retention items require, so "v5 exists" is true whether or not anybody wrote the
sentence. Shipping auto-install on an undisclosed policy is the failure being prevented.

TWO independent anchors must both match, which is what stops incidental or negated prose from
passing on its own:

  * DISCLOSURE — the text says updates can be installed automatically.
  * OPT-OUT    — the text says that INSTALLATION specifically can be turned off. Bound to
                 install/installation by proximity within one sentence, NOT free-floating: both
                 bundled v4 texts already say the automatic CHECK can be turned off, so a
                 free-floating anchor was satisfied before this feature existed.

Requiring both matters because the queued addenda wording contains both in every affected section, and
each half catches a different realistic failure:

  * The DISCLOSURE half catches shipping with v4-era text, which never mentions installing at all.
  * The OPT-OUT half catches a partial fold — text that admits updates install themselves but never
    says you can stop it. That is why it is install-BOUND: measured against the real bundled v4
    texts (2026-08-08), a free-floating opt-out anchor was ALREADY satisfied by their existing
    "you can turn automatic update checks off" wording, so the partial fold would have passed.

Known limit, stated rather than implied: a sufficiently perverse text ("we never install updates
automatically; you cannot turn this off") would satisfy both anchors. This is a tripwire against
FORGETTING the fold, not a proof of adequacy, and it fails LOUDLY if counsel rewords past it —
which is the correct direction for a legal gate: a human then re-reads the text and updates the
anchor, rather than a release sailing past.
#>
function Test-AutoUpdateDisclosure {
    param([string]$Text)

    $result = [pscustomobject]@{
        HasDisclosure = $false
        HasOptOut     = $false
        Ok            = $false
    }

    if ([string]::IsNullOrWhiteSpace($Text)) { return $result }

    $opts = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor `
            [System.Text.RegularExpressions.RegexOptions]::Singleline

    # The install token used by BOTH anchors, with a negative lookbehind excluding MODEL
    # installation (Codex diff r7's demonstrated false-positive: "Automatic model installation can
    # be turned off at any time." satisfied both anchors — and this app's documents genuinely
    # discuss installing transcription MODELS, so that is not a contrived sentence).
    $install = '(?<!models?\s)install'

    # DISCLOSURE: "install … automatic" in either order within ONE sentence — AND that sentence
    # must be about UPDATES ("updat" somewhere in it, before or after the pair). Without the
    # update requirement, a sentence about automatic MODEL or component installation satisfied the
    # anchor. `[^.]` windows keep everything inside a sentence. The update stem is required on the
    # DISCLOSURE only: the queued privacy §4.4 opt-out sentence ("…the check and automatic
    # installation can be turned off at any time") legitimately does not contain "update", so
    # requiring it on both halves would make the queued wording itself unable to pass.
    $pair = "(?:automatic[^.]{0,60}$install|$install[^.]{0,60}automatic)"
    $disclosure = "updat\w*[^.]{0,120}$pair|$pair[^.]{0,120}updat\w*"

    # OPT-OUT: an off/opt-out/disable verb within one sentence of an UPDATE-SCOPED installation
    # subject, either order. Two rounds of narrowing, each closing a demonstrated fail-open:
    #   * Bound to installation rather than free-floating (r3): both v4 texts already say the
    #     automatic update CHECK can be turned off, so a free-floating anchor was satisfied before
    #     this feature existed.
    #   * Bound to UPDATE installation, not any installation (r8): with only the install binding,
    #     "Updates may be installed automatically. Automatic component installation can be turned
    #     off." passed — the disclosure and the opt-out matched DIFFERENT subjects.
    #   * The update stem is required in EVERY opt-out match (r9). The r8 fix carved out the
    #     literal phrase "automatic installation" so the then-queued §4.4 wording could pass —
    #     and that carve-out was itself an open-ended hole: "Automatic installation of optional
    #     components can be turned off." begins with the exact phrase.
    #   * The update word must QUALIFY the installation phrase itself, not merely share the
    #     sentence (r10): 40-char proximity accepted "Automatic update checking and optional
    #     component installation can be turned off" — "update" qualified CHECKING while the
    #     install token belonged to COMPONENTS. Accepted forms are tight: "update installation" /
    #     "updates installed", "installation of updates", "install(ing) updates". BOTH queued
    #     opt-out clauses now say "automatic update installation" explicitly (ours, unfolded —
    #     tightening our own wording beats maintaining a looser gate), recorded in
    #     03-post-send-addenda.md.
    # A bare `\boff\b` rather than "turn … off" with a fixed word gap: real wording puts an
    # arbitrary list between verb and particle ("turn automatic update checks AND automatic
    # installation off"), and every fixed gap is wrong for some legitimate sentence. `\b` keeps it
    # off "offers"/"offline".
    $offVerb = '(?:\boff\b|opt[\s-]?out|disable[d]?)'
    $optOutSubject = "(?:updates?\s+$install|$install\w*\s+of\s+updates?|$install\w*\s+updates?)"
    $optOut = "$offVerb[^.]{0,80}$optOutSubject|$optOutSubject[^.]{0,80}$offVerb"

    $result.HasDisclosure = [regex]::IsMatch($Text, $disclosure, $opts)
    $result.HasOptOut     = [regex]::IsMatch($Text, $optOut, $opts)
    $result.Ok            = $result.HasDisclosure -and $result.HasOptOut
    return $result
}
