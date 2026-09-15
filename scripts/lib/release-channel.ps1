# The ONE answer to "is this channel a public release?"
#
# Dot-source this rather than re-testing the channel string. Two gates now depend on the
# distinction — the legal-asset gate (`check-release-assets.ps1`, which requires privacy v5+ on
# a public release) and the UAT-coverage gate (`check-uat-staleness.ps1`) — and a release that
# is "public" to one and "a tester ring" to the other is the kind of disagreement nobody
# notices until a release ships past a check it was supposed to fail.

Set-StrictMode -Version Latest

function Test-IsPublicReleaseChannel {
    <#
    .SYNOPSIS
    Whether a Velopack channel ships to the PUBLIC, as opposed to a tester ring.

    .DESCRIPTION
    Matches any channel ending in `-stable`, ordinal and case-insensitive. Today that is
    exactly `win-x64-stable`; the suffix form is deliberate so a future `win-arm64-stable`
    is public the day it exists rather than the day someone remembers to update a list.

    The earlier form was `$Channel -eq 'win-x64-stable'` inside the legal gate. That is
    fail-OPEN on any second public channel — a new architecture would have skipped the
    privacy-version requirement silently — and the direction matters more than the
    likelihood: an unrecognised channel treated as a tester ring ships unchecked, while one
    treated as public merely asks for an override that already exists.

    An empty channel is NOT public. `release-update.ps1` defaults it to empty for local
    smoke builds, and those must stay ungated.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Channel)

    if ([string]::IsNullOrWhiteSpace($Channel)) { return $false }
    return $Channel.Trim().EndsWith('-stable', [StringComparison]::OrdinalIgnoreCase)
}

function Get-GatedReleaseChannels {
    <#
    .SYNOPSIS
    The channels Cloudflare Access actually covers, by EXACT name.

    .DESCRIPTION
    On 2026-09-09 the Access application was scoped to the path /win-x64-beta, so "not public" no
    longer means "gated": a channel that is neither a '-stable' public channel nor listed here has
    NO protected prefix, and a pack cut for it would carry the tester token onto a world-readable
    prefix (a tester name nobody added) or ship it to the public (a public name without the
    '-stable' suffix). Ordinal and case-SENSITIVE on purpose: the name becomes the R2 prefix and the
    Access path verbatim. Add a channel here in the SAME change that adds its Access path.
    #>
    return @('win-x64-beta')
}

function Get-ReleaseChannelClass {
    <#
    .SYNOPSIS
    'Public' | 'Gated' | 'Unknown' - what kind of update feed a Velopack channel is.

    .DESCRIPTION
    Public  = Test-IsPublicReleaseChannel (any '-stable' suffix): ungated, credential-free.
    Gated   = an exact member of Get-GatedReleaseChannels: behind Cloudflare Access, bakes the token.
    Unknown = everything else, INCLUDING an empty channel. Consumers decide what Unknown means for
    them: a dev build (empty channel) is inert whatever the class; a release build or a release
    refuses, because it cannot know which prefix Access covers. The legal and UAT gates keep using
    the two-way predicate above - for them the only question is "public or not".
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Channel)

    if ([string]::IsNullOrWhiteSpace($Channel)) { return 'Unknown' }
    if (Test-IsPublicReleaseChannel -Channel $Channel) { return 'Public' }
    if (@(Get-GatedReleaseChannels) -ccontains $Channel.Trim()) { return 'Gated' }
    return 'Unknown'
}

function Get-DefaultKeepMaxReleases {
    <#
    .SYNOPSIS
    How many releases the R2 feed keeps for a channel when nobody says otherwise.

    .DESCRIPTION
    Owner decision 2026-09-09: a public channel keeps the newest 10, a gated tester ring the newest 5,
    an unknown channel prunes nothing (0 - it cannot be signed anyway). THE numbers live here and only
    here; the release script, the workflow input's description, the /vw-release skill and the runbooks
    cite this function. Pruning is Velopack's `vpk upload s3 --keepMaxReleases`: releases older than the
    newest N are DELETED from the bucket after an upload - irreversible.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Channel)

    switch (Get-ReleaseChannelClass -Channel $Channel) {
        'Public' { return 10 }
        'Gated'  { return 5 }
        default  { return 0 }
    }
}

function Resolve-KeepMaxReleases {
    <#
    .SYNOPSIS
    The effective -KeepMaxReleases for an upload, and where it came from.

    .DESCRIPTION
    The contract shared by installer/release-update.ps1, the release.yml input and /vw-release:
      -1  = unset: the per-channel default (Get-DefaultKeepMaxReleases)   -> Source 'default'
       0  = keep everything, explicitly                                    -> Source 'keep-everything'
       1  = REFUSED. Keeping only the newest release deletes the previous FULL package - the one a
            client may be mid-download on - and leaves nothing on R2 to reinstall from if the newest
            release turns out bad. It is NOT about the next pack's delta base: vpk downloads the
            current latest release before generating deltas, so that is never missing.
      N>=2 = as given                                                      -> Source 'explicit'
    Anything below -1 is a caller bug and throws.
    #>
    param(
        [Parameter(Mandatory)][int]$Requested,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Channel
    )

    if ($Requested -lt -1) {
        throw "KeepMaxReleases must be -1 (per-channel default), 0 (keep everything) or 2 or more; got $Requested."
    }
    if ($Requested -eq 1) {
        throw 'KeepMaxReleases 1 is refused: it deletes the previous FULL package - the one a client may be mid-download on, and the only thing left on R2 to reinstall from if the newest release is bad. Use 2 or more, or 0 to keep everything.'
    }
    if ($Requested -eq -1) {
        return [pscustomobject]@{ Value = (Get-DefaultKeepMaxReleases -Channel $Channel); Source = 'default' }
    }
    if ($Requested -eq 0) {
        return [pscustomobject]@{ Value = 0; Source = 'keep-everything' }
    }
    return [pscustomobject]@{ Value = $Requested; Source = 'explicit' }
}
