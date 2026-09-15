<#
.SYNOPSIS
Rebuild the two no-op stub assemblies in build/stubs/ from the source beside this script.

.DESCRIPTION
build/stubs/ holds the stub task assemblies that Directory.Build.props points
AppxMSBuildToolsPath at, so unpackaged CLI builds never need Visual Studio's packaging
tools (root CLAUDE.md, WinUI constraint 6). The DLLs are committed because they must
exist BEFORE any build starts — they cannot be an output of the build that needs them.

This script exists so the committed binaries are reproducible from committed source.
The projects build with DebugType=none, so the rebuilt DLLs carry no debug directory
and no build-machine path can be embedded; each DLL is verified for exactly that BEFORE
it is replaced. Verification is per project, not transactional across both: a failure
on the second project leaves the first, already-verified replacement in place — benign
(the assemblies are independent, nothing commits itself, `git status` shows the state,
and rerunning converges).

.NOTES
Requires network on first run (NuGet: MSBuild 15.1 reference packages).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$srcRoot = $PSScriptRoot
$stubsDir = Join-Path (Split-Path -Parent $srcRoot) 'stubs'
$projects = @(
    @{ Name = 'Microsoft.Build.AppxPackage';          Types = 8 },
    @{ Name = 'Microsoft.Build.Packaging.Pri.Tasks';  Types = 18 }
)

Add-Type -AssemblyName System.Reflection.Metadata

foreach ($p in $projects) {
    $projDir = Join-Path $srcRoot $p.Name
    $outDir = Join-Path $projDir 'bin\Release'
    Write-Host "== building $($p.Name)"
    & dotnet build (Join-Path $projDir "$($p.Name).csproj") -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed for $($p.Name)" }

    $dll = Join-Path $outDir "$($p.Name).dll"
    if (-not (Test-Path -LiteralPath $dll)) { throw "expected output missing: $dll" }

    # Verify BEFORE replacing: no debug directory at all, and no user-profile path
    # anywhere in the bytes.
    $stream = [System.IO.File]::OpenRead($dll)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        # The CodeView (RSDS) entry is where a pdb PATH would live. A deterministic
        # build still emits the data-less 'Reproducible' marker, which carries nothing
        # and is fine; anything else path- or pdb-bearing is refused.
        $badEntries = @($pe.ReadDebugDirectory() | Where-Object { $_.Type -ne 'Reproducible' })
        if ($badEntries.Count -gt 0) {
            throw "$($p.Name): rebuilt DLL carries debug directory entries beyond the Reproducible marker ($(($badEntries | ForEach-Object Type) -join ', ')) - check DebugType=none took effect."
        }
        $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $publicTypes = 0
        foreach ($h in $md.TypeDefinitions) {
            $t = $md.GetTypeDefinition($h)
            if (($t.Attributes -band [System.Reflection.TypeAttributes]::Public) -ne 0) { $publicTypes++ }
        }
        if ($publicTypes -ne $p.Types) {
            throw "$($p.Name): rebuilt DLL exports $publicTypes public types, expected $($p.Types) - the task surface the SDK targets bind against must not shrink or grow silently."
        }
    } finally { $stream.Dispose() }

    $ascii = [System.Text.Encoding]::Latin1.GetString([System.IO.File]::ReadAllBytes($dll))
    if ($ascii -match '[A-Za-z]:[\\/]{1,2}Users[\\/]{1,2}' -or $ascii -match '\.pdb') {
        throw "$($p.Name): rebuilt DLL still contains a user path or pdb reference - refusing to replace."
    }

    Copy-Item -LiteralPath $dll -Destination (Join-Path $stubsDir "$($p.Name).dll") -Force
    Write-Host "   replaced $(Join-Path $stubsDir "$($p.Name).dll")"
}

Write-Host 'done - rebuild the app (dotnet build -c Debug -p:Platform=x64) to prove the stubs still satisfy the WinAppSDK targets.'
