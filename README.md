# VoiceWink

Voice-to-text for Windows 11. Press a hotkey, talk, and your words land at the cursor, in
any app you can type in. Built with .NET 10 + WinUI 3 by DV Studio, inspired by
[VoiceInk](https://github.com/Beingpax/VoiceInk) (macOS).

Product information and downloads: <https://voicewink.app/?src=gh>

[![Listed on Open Source Alternatives](https://www.opensourcealternatives.to/badge-osa.svg)](https://www.opensourcealternatives.to/item/voicewink)

![The VoiceWink home screen: the recording state and the last transcription](https://voicewink.app/assets/vw-main.webp)

## What it does

- **Dictation on your PC.** Speech is transcribed locally with Parakeet (25 European
  languages, detected automatically) or Whisper (100 languages). A graphics card is optional
  and makes it faster.
- **AI enhancement on your PC.** Built-in models remove filler words and rewrite what you
  said, with no API key and no account. They need at least 16 GB of memory.
- **App Mode.** A different enhancement per app, chosen automatically: an email in Outlook, a
  chat message in Slack.
- **Dictionary and audio files.** Add names and jargon, set replacement rules, and transcribe
  audio files.
- **Optional cloud features, with your own API keys:** cloud transcription, cloud AI
  enhancement and voice-to-image. Or use your own AI server.
- **Tap or hold.** Tap the hotkey once to keep listening, or hold it while you talk.

Recordings and history stay on your disk. The app goes online to check your license, look for
updates and download the models you choose. Crash reports are opt-in.

![The AI Enhancement page with the Built-in Models card](https://voicewink.app/assets/vw-ai.webp)

![App Mode: a transcription model, a language and an AI enhancement per app](https://voicewink.app/assets/vw-appmode.webp)

## Try it

Download the signed installer from the site and try every feature free for 7 days, with no
account or e-mail. A license is a one-time purchase and covers the signed build, updates when
released, and support. Or build it yourself from this repository, as described below.

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

Requirements: Windows 11, a .NET 10 SDK (any `10.0.x` satisfies the pin in `global.json`).

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
