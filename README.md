# VoiceWink

Voice-to-text for Windows 11 — press a hotkey, speak, and the transcribed text is pasted
at your cursor. Built with .NET 8 + WinUI 3 by DV Studio, inspired by
[VoiceInk](https://github.com/Beingpax/VoiceInk) (macOS).

Product information and downloads: <https://voicewink.app>

## What this repository is

This is the **corresponding source** (GPL v3 §6) for released versions of VoiceWink.
Each release is published here as a single snapshot commit tagged `v<version>`;
development happens in a private repository, so this history intentionally carries one
commit per release rather than day-to-day work.

## License

VoiceWink is licensed under the [GNU General Public License v3.0](LICENSE)
(GPL-3.0-only). It is a derivative work of
[VoiceInk](https://github.com/Beingpax/VoiceInk), which is licensed under GPL v3.
Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Copyright © 2025–2026 Dieter Verlaeckt / DV Studio.

## Building from source

Requirements: Windows 11, a .NET 8 SDK (any `8.0.x` satisfies the pin in `global.json`).

The supported install-from-source route is the two commands below, in order (the second
requires Git Bash and PowerShell 7; its release-asset checks read the first one's build
output):

```
dotnet build src/VoiceWink/VoiceWink.csproj -c Release -p:Platform=x64
bash installer/build-portable.sh --config Release
```

This produces `installer/Output/VoiceWink-<version>-Portable.zip`; extract the ZIP and
run the bundled `VoiceWinkSetup.ps1` (or `Install.bat`) to install.

Official distributed packages are built from this same source with
`dotnet publish -c Release -r win-x64 --self-contained -p:Platform=x64
-p:PublishReadyToRun=true`, then signed and packaged for the update channel. The
signing identity, update-feed credentials, and crash-reporting DSN are publisher
infrastructure (configuration, not source) and are not included; builds without them
are functionally equivalent, with update checking and crash reporting inert.

## Issues and contributions

Issues and patches are welcome. Because releases are published as snapshots, pull
requests cannot merge here directly — maintainers apply accepted patches to the private
development tree, and they ship in a subsequent tagged release.
