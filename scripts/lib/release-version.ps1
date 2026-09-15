# release-version.ps1 — the ONE answer to "which version is this release".
#
# Dot-sourced by scripts/resolve-release-version.ps1 (the CLI entry point the shell chains call),
# installer/release-update.ps1, scripts/bump-version.ps1 and scripts/check-uat-staleness.ps1.
# Pinned by scripts/test-release-version.ps1.
#
# ---------------------------------------------------------------------------------------------
# Why this parses XML instead of matching text
# ---------------------------------------------------------------------------------------------
# Three regex designs were tried here and each was wrong in a way that only shows up on a real
# csproj. Recording them because each looks obviously fine until executed:
#
#   1. `<Version>([^<]+)</Version>` counted PAIRS. `<Version></Version>`, `<Version />` and
#      `<Version Condition="…">` each read as ONE while MSBuild sees two assignments and applies
#      LAST-wins — so vpk packed the first while dotnet publish built the last.
#   2. Counting element STARTS fixed that but was still text: `<!-- <Version>9.9.999</Version> -->`
#      beside an active `<VersionPrefix>` resolved to 9.9.999 while MSBuild built something else
#      (fail-open), and a commented-out OLD version beside the real one read as a duplicate and
#      blocked a legitimate release (false fire). VoiceWink.csproj carries 39 XML comments, one of
#      which contains a literal `<Link>` element — this is not a hypothetical file shape.
#   3. `grep -c` in the shell twin counted matching LINES, and the csproj keeps its version
#      properties on ONE line, so a duplicate read as 1 and the guard could never fire.
#
# A comment is an XML concept, so only an XML parser can reliably ignore one. The shell twin was
# DELETED rather than taught the same trick: both shell chains already hard-require pwsh
# (`require_cmd pwsh`) and already call it for the legal gate, the UAT gate and the payload check,
# so a second implementation bought nothing but the drift its case table then had to police.
#
# ---------------------------------------------------------------------------------------------
# Four traps this file is deliberately shaped around (each one measured, not anticipated)
# ---------------------------------------------------------------------------------------------
#   * NEVER use PowerShell dot-notation (`$doc.Project.PropertyGroup.Version`). The csproj has four
#     PropertyGroups, so that expression returns an Object[] of 4 — one value and three nulls —
#     whose string cast carries stray spaces, so an equality test silently fails and .Trim() throws.
#   * ALWAYS select with `local-name()`. VoiceWink.csproj has no xmlns today (verified:
#     NamespaceURI is empty), so a bare `//Version` works — but on a legacy-format project carrying
#     the 2003 MSBuild namespace `//Version` returns ZERO nodes and raises no error, which is a
#     textbook fail-open.
#   * NEVER `return` a node list. PowerShell UNROLLS an enumerable on return: a one-node result
#     reached the caller as a bare XmlElement — whose `[0]` the XML type adapter reinterprets as
#     child-element access, so `.InnerText` was "not found" — and a zero-node result arrived as
#     `$null`, so `.Count` threw under StrictMode. Both failures pointed at the caller.
#   * A zero-node result is an ERROR, never a no-op. The one new failure XML parsing could
#     introduce is a silent empty match, so the count is asserted explicitly in both directions.
#
# Reading from a PATH uses XmlDocument.Load, which honours the file's own encoding. The csproj is
# BOM-less UTF-8 with 447 non-ASCII characters; under Windows PowerShell 5.1 a
# `[xml](Get-Content -Raw)` round-trip decodes `©` as `Â©`. Harmless when only reading an ASCII
# version, corrupting for anything that writes the file back — so the path API never goes through
# a string.

Set-StrictMode -Version Latest

function Get-ProjectVersionValues {
    <#
      .SYNOPSIS
      The text of every <Version> ELEMENT — comments excluded by the parser, conditional and
      self-closing forms included, namespace irrelevant. Always an array.

      .NOTES
      Scoped to PropertyGroup CHILDREN, not every `<Version>` in the document. An MSBuild PROPERTY
      only exists inside a PropertyGroup; a `<Version>` anywhere else is something else entirely —
      most commonly item metadata on a multi-line `<PackageReference>`, where it is that PACKAGE's
      version. Unscoped, a project written that way resolved the release version to a NuGet
      package's version, or counted one as a false duplicate. VoiceWink.csproj already contains two
      multi-line `<PackageReference>` elements, so that is one ordinary dependency edit away rather
      than a hypothetical.

      Returns VALUES rather than nodes, and returns them behind the `,` unroll guard. Both halves
      of that were bugs when this was written the obvious way — see the unroll trap in the header.
      Handing back values also keeps the XML type adapter out of the caller entirely, which is what
      stops anyone reaching for the dot-notation this file forbids.
    #>
    param([Parameter(Mandatory)][System.Xml.XmlDocument]$Document)

    $values = @()
    foreach ($node in $Document.SelectNodes("//*[local-name()='PropertyGroup']/*[local-name()='Version']")) {
        $values += $node.InnerText
    }
    return ,$values
}

function Get-ProjectMirrorValue {
    <#
      .SYNOPSIS
      The single <AssemblyVersion> / <FileVersion> value, or $null when absent or duplicated.

      .NOTES
      Duplicated returns $null so the caller reports "cannot read" rather than picking one — the
      same first-wins guess this whole file exists to remove.
    #>
    param(
        [Parameter(Mandatory)][System.Xml.XmlDocument]$Document,
        [Parameter(Mandatory)][string]$Name
    )

    $values = @()
    foreach ($node in $Document.SelectNodes("//*[local-name()='$Name']")) {
        $values += $node.InnerText
    }
    if ($values.Count -ne 1) { return $null }
    return $values[0].Trim()
}

function Test-ProjectHasCommentedVersion {
    <#
      .SYNOPSIS
      Does any XML COMMENT contain a <Version> element?

      .DESCRIPTION
      Reading is immune to this — the parser drops comments. WRITING is not: scripts/bump-version.ps1
      rewrites with a global text replace, so a commented-out `<Version>1.40.300</Version>` above the
      live one made it READ 1.40.300, compute 1.40.301, and then rewrite BOTH — regressing the
      shipped version, which that script's own comment explains reaches users as "no update
      available". Callers that WRITE the file consult this and refuse; callers that only read ignore it.
    #>
    param([Parameter(Mandatory)][System.Xml.XmlDocument]$Document)

    foreach ($comment in $Document.SelectNodes('//comment()')) {
        if ($comment.Value -match '<Version[\s/>]') { return $true }
    }
    return $false
}

function ConvertTo-ProjectDocument {
    <#
      .SYNOPSIS
      Load a csproj from a path or from text.

      .OUTPUTS
      Hashtable: Ok, Document, Errors. NEVER throws — a malformed csproj is a release-blocking
      finding to be reported, not an unhandled exception in the middle of a signing run.
    #>
    [CmdletBinding(DefaultParameterSetName = 'Path')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Path')][string]$ProjectPath,
        [Parameter(Mandatory, ParameterSetName = 'Xml')][AllowEmptyString()][string]$ProjectXml,
        [string]$ProjectLabel = 'VoiceWink.csproj'
    )

    $doc = New-Object System.Xml.XmlDocument

    try {
        if ($PSCmdlet.ParameterSetName -eq 'Path') {
            if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
                return @{ Ok = $false; Document = $null; Errors = @("ERROR: $ProjectLabel not found at $ProjectPath.") }
            }
            $doc.Load($ProjectPath)
        } else {
            if ([string]::IsNullOrWhiteSpace($ProjectXml)) {
                return @{ Ok = $false; Document = $null; Errors = @("ERROR: $ProjectLabel is empty.") }
            }
            $doc.LoadXml($ProjectXml)
        }
    } catch {
        return @{
            Ok = $false; Document = $null
            Errors = @(
                "ERROR: $ProjectLabel is not well-formed XML, so its version cannot be read.",
                "       $($_.Exception.Message)"
            )
        }
    }

    return @{ Ok = $true; Document = $doc; Errors = @() }
}

function Resolve-ReleaseVersion {
    <#
      .SYNOPSIS
      Resolve the release version, checking any CLI override and the two mirror properties.

      .DESCRIPTION
      The CLI override is an ASSERTION, not an override: it must EQUAL the project's version or the
      run fails. It cannot be honoured as a true override because it reaches `vpk --packVersion` but
      never `dotnet publish` (zero `-p:Version=` on any path), so a differing value packs one number
      around a build of another. Making it genuinely reach the build would need `-p:AssemblyVersion`
      and `-p:FileVersion` too — both are declared explicitly in the csproj and MSBuild does not
      derive them — which means re-deriving the `.0` suffix here, the arithmetic root CLAUDE.md
      reserves for scripts/bump-version.ps1, and it would leave the csproj on disk disagreeing with
      the signed artifact. release.yml never passes a version, so the production path is unaffected.

      The mirrors are checked because vpk packs <Version> while every RUNTIME surface reads
      <AssemblyVersion> — the updater, the About page, Sentry and the support bundle. A hand edit
      that moves one and not the others ships a pack labelled X containing an app that calls itself
      Y, and the updater compares exactly those two numbers.

      .OUTPUTS
      Hashtable: Ok (bool), Version (string or $null), HasCommentedVersion (bool), Errors (string[]).
      Never throws and never writes host output — presentation belongs to the caller, so the same
      core is drivable from a case table.
    #>
    [CmdletBinding(DefaultParameterSetName = 'Path')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Path')][string]$ProjectPath,
        [Parameter(Mandatory, ParameterSetName = 'Xml')][AllowEmptyString()][string]$ProjectXml,
        [AllowEmptyString()][AllowNull()][string]$Override = '',
        [string]$ProjectLabel = 'VoiceWink.csproj',
        [switch]$SkipMirrorCheck
    )

    $loaded = if ($PSCmdlet.ParameterSetName -eq 'Path') {
        ConvertTo-ProjectDocument -ProjectPath $ProjectPath -ProjectLabel $ProjectLabel
    } else {
        ConvertTo-ProjectDocument -ProjectXml $ProjectXml -ProjectLabel $ProjectLabel
    }
    if (-not $loaded.Ok) {
        return @{ Ok = $false; Version = $null; HasCommentedVersion = $false; Errors = $loaded.Errors }
    }

    $doc = $loaded.Document
    $commented = Test-ProjectHasCommentedVersion -Document $doc
    $values = Get-ProjectVersionValues -Document $doc

    $fail = {
        param([string[]]$Lines)
        return @{ Ok = $false; Version = $null; HasCommentedVersion = $commented; Errors = $Lines }
    }

    if ($values.Count -eq 0) {
        return & $fail @(
            "ERROR: $ProjectLabel declares no <Version> element.",
            "       The release version has no source. Bump with scripts/bump-version.ps1."
        )
    }

    if ($values.Count -gt 1) {
        return & $fail @(
            "ERROR: $ProjectLabel declares <Version> $($values.Count) times.",
            "       vpk would pack the first and MSBuild would build the last, so the signed",
            "       artifact and its manifest would disagree. Remove the duplicate."
        )
    }

    $version = $values[0].Trim()
    if ([string]::IsNullOrWhiteSpace($version)) {
        return & $fail @(
            "ERROR: $ProjectLabel declares an empty <Version>.",
            "       A blank or self-closing element cannot name a release; write it plainly."
        )
    }

    if (-not $SkipMirrorCheck) {
        $expectedMirror = "$version.0"
        foreach ($name in 'AssemblyVersion', 'FileVersion') {
            $actual = Get-ProjectMirrorValue -Document $doc -Name $name
            if ($null -eq $actual) { continue }   # absent or duplicated: not this check's business
            if ($actual -cne $expectedMirror) {
                return & $fail @(
                    "ERROR: $ProjectLabel has <$name> '$actual' but <Version> '$version'.",
                    "       vpk packs <Version> while the updater, About page, Sentry and support",
                    "       bundle all read <$name> — so this would ship a pack labelled",
                    "       '$version' containing an app that calls itself '$actual'.",
                    "       Expected <$name>$expectedMirror</$name>. Bump with:",
                    "         pwsh -File scripts/bump-version.ps1 -Part build"
                )
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Override)) {
        $trimmed = $Override.Trim()
        # Ordinal, case-SENSITIVE: a version is not a word, so a pre-release suffix differing only
        # by case is a real difference and must stop the run rather than be normalised away.
        if (-not [string]::Equals($trimmed, $version, [StringComparison]::Ordinal)) {
            return & $fail @(
                "ERROR: version override '$trimmed' does not match $ProjectLabel ('$version').",
                "       The override cannot reach 'dotnet publish', so honouring it would pack",
                "       '$trimmed' around a build of '$version'. Bump the csproj instead:",
                "         pwsh -File scripts/bump-version.ps1 -Part build"
            )
        }
    }

    return @{ Ok = $true; Version = $version; HasCommentedVersion = $commented; Errors = @() }
}
