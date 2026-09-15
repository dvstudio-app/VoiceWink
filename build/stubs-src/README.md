# Stub-assembly source

Source for the two committed no-op assemblies in `build/stubs/`, which
`Directory.Build.props` points `AppxMSBuildToolsPath` at so unpackaged CLI builds never
need Visual Studio's packaging tools. The task classes mirror — name for name, property
for property, `[Output]` for `[Output]` — the surface the Windows App SDK's
`MrtCore.PriGen.targets` / Appx targets bind against; the payload tasks pass their
inputs through, everything else is `return true`.

The DLLs are committed (they must exist before any build starts), and this directory is
what makes them reproducible: run `rebuild-stubs.ps1` to rebuild both with
`DebugType=none` (no debug directory, so no build-machine path can be embedded) and
replace the committed copies after verifying exactly that.

These projects are deliberately outside `VoiceWink.sln` and shielded from the repo
root's `Directory.Build.*` by the empty ones beside this file.
