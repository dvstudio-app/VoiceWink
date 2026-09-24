# Third-Party Notices

VoiceWink (Copyright © 2025-2026 Dieter Verlaeckt / DV Studio) is licensed under the **GNU General Public License v3 (GPL-3.0-only)**. See [`LICENSE`](LICENSE) for the full text.

VoiceWink incorporates third-party software components. Each component is the property of its respective copyright holders, and is distributed under the terms of its own licence as listed below.

This file is the manifest required for compliance with the attribution clauses of the MIT, Apache-2.0, BSD-3-Clause and similar permissive licences carried by the dependencies. The full licence text of each component is included in the binary distribution next to the corresponding `.dll` (via NuGet's standard `licenses` metadata), and is also retrievable from the linked project pages.

Versions last synced 2026-08-23 (full re-verification: every direct row and every listed
transitive row matched `dotnet list package --include-transitive` exactly, and the shipped
win-x64 `onnxruntime.dll` ProductVersion still reads 1.27.0, matching the notices file below —
no content changes were needed).

---

## Direct dependencies

| Package | Version | Licence (SPDX) | Project URL |
|---|---|---|---|
| CommunityToolkit.Mvvm | 8.4.0 | MIT | https://github.com/CommunityToolkit/dotnet |
| H.NotifyIcon.WinUI | 2.1.0 | MIT | https://github.com/HavenDV/H.NotifyIcon |
| Microsoft.EntityFrameworkCore.Design | 8.0.11 | MIT | https://github.com/dotnet/efcore |
| Microsoft.EntityFrameworkCore.Sqlite | 8.0.11 | MIT | https://github.com/dotnet/efcore |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Http | 8.0.1 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Windows.CsWin32 | 0.3.106 | MIT | https://github.com/microsoft/CsWin32 |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| Microsoft.WindowsAppSDK | 1.6.250602001 | MIT | https://github.com/microsoft/WindowsAppSDK |
| NAudio | 2.2.1 | MIT | https://github.com/naudio/NAudio |
| Sentry.Serilog | 6.4.1 | MIT | https://github.com/getsentry/sentry-dotnet |
| Serilog | 4.1.0 | Apache-2.0 | https://github.com/serilog/serilog |
| Serilog.Sinks.Debug | 3.0.0 | Apache-2.0 | https://github.com/serilog/serilog-sinks-debug |
| Serilog.Sinks.File | 6.0.0 | Apache-2.0 | https://github.com/serilog/serilog-sinks-file |
| SharpHook | 5.3.8 | MIT | https://github.com/TolikPylypchuk/SharpHook |
| System.Diagnostics.EventLog | 8.0.2 | MIT | https://github.com/dotnet/runtime |
| System.Drawing.Common | 8.0.11 | MIT | https://github.com/dotnet/runtime |
| System.Management | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| Velopack | 0.0.1298 | MIT | https://github.com/velopack/velopack |
| Whisper.net | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
| Whisper.net.Runtime | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
| Whisper.net.Runtime.Vulkan | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
| org.k2fsa.sherpa.onnx | 1.13.4 | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| org.k2fsa.sherpa.onnx.runtime.win-x64 | 1.13.4 | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| WinUIEx | 2.5.0 | MIT | https://github.com/dotnet/WinUIEx |

## Bundled models

| Model | Version | Licence (SPDX) | Source |
|---|---|---|---|
| Silero VAD (ggml conversion, `Assets/Models/ggml-silero-v6.2.0.bin`) | v6.2.0 | MIT | https://github.com/snakers4/silero-vad (ggml conversion: https://huggingface.co/sandrohanea/whisper.net, `v4/vad/`) |
| Silero VAD (ONNX, `Assets/Models/silero_vad.onnx`) | — | MIT | https://github.com/snakers4/silero-vad (ONNX build: https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx) |

Both bundled Silero VAD models are distributed under the MIT License,
Copyright (c) 2020-present Silero Team — the SAME upstream project in two runtime
formats, so one attribution covers both. Integrity pins:

- `ggml-silero-v6.2.0.bin` — SHA-256
  `2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987`, 885,098 bytes
  (matches the upstream Hugging Face LFS pointer).
- `silero_vad.onnx` — SHA-256
  `9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6`, 643,854 bytes
  (downloaded and hashed locally, 2026-08-20; the release asset carries no LFS pointer
  to read a hash off, so it was computed from the file rather than quoted from a page).

The two are separate files because they serve different runtimes and neither can load the
other's format: the ggml conversion drives the Whisper.net no-speech gate, and the ONNX
build drives sherpa-onnx's segmentation of Parakeet decodes.
<!-- Maintainers: the pins are enforced by BundledVadModelTests and the release
     payload/portable-ZIP gates. Adding a third bundled model means all three. -->

### Parakeet TDT 0.6B v3 — downloaded on demand, not bundled

VoiceWink can download **NVIDIA Parakeet TDT 0.6B v3** (int8 ONNX export) as an optional local
transcription model. Unlike the Silero VAD model above it is **not bundled in the installer** —
the app fetches it at the user's request from DV Studio's model mirror (models.voicewink.app),
which serves a byte-identical, hash-verified copy of the upstream export named below — falling
back to the upstream repository itself when the mirror is unavailable or serves a file that fails verification.

| Model | Licence (SPDX) | Source |
|---|---|---|
| NVIDIA Parakeet TDT 0.6B v3 (int8 ONNX, sherpa-onnx export) | **CC-BY-4.0** | https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3 (export: https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8) |

The weights are © NVIDIA Corporation, released under the Creative Commons Attribution 4.0
International licence: https://creativecommons.org/licenses/by/4.0/ — which permits commercial use
with attribution, and is compatible with VoiceWink's GPL-3.0 distribution because the model is data
the app loads, not code linked into it.

Per-file integrity pins, enforced at install time by `ModelDownloadManager`:
<!-- Maintainers: pins verified 2026-08-03 against the upstream LFS metadata, with one file
     downloaded and hashed locally to confirm the metadata is the content hash. -->

| File | Bytes | SHA-256 |
|---|---|---|
| `encoder.int8.onnx` | 652,184,281 | `acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247` |
| `decoder.int8.onnx` | 11,845,275 | `179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e` |
| `joiner.int8.onnx` | 6,355,277 | `3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3` |
| `tokens.txt` | 93,939 | `d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d` |

## parakeet-server (bundled)

VoiceWink ships **`parakeet-server.exe`** (59,147,776 bytes, v0.5.0 win-vulkan-x64, in
`runtimes\win-x64\`) — the resident helper process the parakeet.cpp transcription backend
spawns. It is a statically linked build of:

| Project | Licence (SPDX) | Source |
|---|---|---|
| parakeet.cpp | **MIT** | https://github.com/mudler/parakeet.cpp |
| ggml (embedded by parakeet.cpp) | **MIT** | https://github.com/ggml-org/ggml |

parakeet.cpp is © Ettore Di Giacinto (mudler) and contributors; ggml is © Georgi Gerganov
and contributors. Both are distributed under the MIT License — full text below. The binary's
non-OS imports are the Microsoft Visual C++ runtime DLLs already shipped app-local (the
vcredist section of `VoiceWink.csproj`) and the Vulkan loader shipped beside it (next section).

## Vulkan loader (bundled)

VoiceWink ships **`vulkan-1.dll`** (1,685,432 bytes, Khronos Vulkan loader from the LunarG
Vulkan Runtime 1.4.357.0, in `runtimes\win-x64\` beside `parakeet-server.exe`, which statically
imports it). The loader is distributed under the **Apache License 2.0**:

- Vulkan Loader — https://github.com/KhronosGroup/Vulkan-Loader — **Apache-2.0**
- Copyright (c) 2015-2026 The Khronos Group Inc.
- Copyright (c) 2015-2026 LunarG, Inc.
- Copyright (c) 2015-2026 Valve Corporation

The upstream runtime archive ships no separate `NOTICE` file (audited 2026-09-01); its
attribution document, `VulkanRT-License.txt`, is reproduced verbatim in this repository at
`installer/runtime/vulkan-loader/VulkanRT-License.txt`. The full Apache 2.0 licence text
required by section 4(a) is included below (the sherpa-onnx section's copy — one licence text
serves every Apache-2.0 component in this file).

The corresponding GGUF model export is **downloaded on demand, not bundled** (the same rule
as the sherpa-onnx export above): `tdt-0.6b-v3-q8_0.gguf`, 940,663,680 bytes, SHA-256
`4d69a4a6683f4f2d952bad794c1357ca6eb628027695b4699c5a9ad4cd07d757`, fetched from DV Studio's
model mirror (models.voicewink.app) as a byte-identical copy of
https://huggingface.co/mudler/parakeet-cpp-gguf — with that upstream as automatic fallback
when the mirror is unavailable or serves a file that fails verification — and hash-verified at install time. The
weights inside it are the same © NVIDIA Parakeet TDT 0.6B v3 release under **CC-BY-4.0**
(see the section above) in a different container format.

## sherpa-onnx runtime (bundled via org.k2fsa.sherpa.onnx.runtime.win-x64)

VoiceWink ships two pre-built native binaries from that NuGet package — `onnxruntime.dll`
(17,363,968 bytes) and `sherpa-onnx-c-api.dll` (4,544,512 bytes), **20.9 MB together** in the
shipped payload.
<!-- Maintainers: sizes measured from a real RID-qualified self-contained win-x64 publish on
     2026-08-03. The meta-package pulls in ~317 MB of other-RID binaries during a plain build; the
     RID-qualified release publish filters them, which is why the shipped figure is two orders of
     magnitude smaller. -->
They are **two separate works under two different licences**:

| Binary | Work | Copyright | Licence |
|--------|------|-----------|---------|
| `sherpa-onnx-c-api.dll` | [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) | © 2022-2026 Xiaomi Corporation and the k2-fsa authors | **Apache License 2.0** — full text below |
| `onnxruntime.dll` | [ONNX Runtime](https://github.com/microsoft/onnxruntime) | © Microsoft Corporation | **MIT** — full text below |

**The NuGet package carries no licence or notice files of its own** — it ships a `README.md` and
the binaries, nothing else. Both obligations are therefore satisfied by *this* file, which ships at
the root of the installed application — MIT requires the copyright notice and permission notice to
accompany the binary, and Apache 2.0 §4(a) requires a copy of the licence.
<!-- Maintainers: package contents verified 2026-08-03; re-check when bumping the package. -->

### ONNX Runtime's own dependencies

`onnxruntime.dll` statically links a large set of third-party components — protobuf, Eigen, Abseil
and around sixty others — whose licences require their notices to travel with the binary. Microsoft
publishes them as one file, and **VoiceWink ships that file verbatim** rather than linking to it:

**`licenses/onnxruntime-1.27.0-ThirdPartyNotices.txt`** — 325,054 bytes, ~67 component blocks,
fetched 2026-08-03 from
`https://raw.githubusercontent.com/microsoft/onnxruntime/v1.27.0/ThirdPartyNotices.txt`. It ships
in the installed payload. These notices correspond to `onnxruntime.dll` 1.27.0, the version
actually redistributed.
<!-- Maintainers: the file's presence is enforced by scripts/check-publish-payload.ps1, and 1.27.0
     was read from the shipped onnxruntime.dll's own ProductVersion field, not from a build script;
     the two can disagree, and the notices only discharge the obligation if they match the binary
     actually redistributed. When bumping the sherpa-onnx package, re-read the new onnxruntime.dll's
     ProductVersion, fetch the notices for THAT tag, and rename the file to match. A stale notices
     file is worse than a missing one: it looks discharged. -->

## Transitive dependencies (selected)

| Package | Version | Licence (SPDX) | Project URL |
|---|---|---|---|
| H.GeneratedIcons.System.Drawing | 2.1.0 | MIT | https://github.com/HavenDV/H.GeneratedIcons |
| H.NotifyIcon | 2.1.0 | MIT | https://github.com/HavenDV/H.NotifyIcon |
| Humanizer.Core | 2.14.1 | MIT | https://github.com/Humanizr/Humanizer |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0 | MIT | https://github.com/dotnet/runtime |
| Microsoft.CodeAnalysis.\* | 4.5.0 | MIT | https://github.com/dotnet/roslyn |
| Microsoft.Data.Sqlite.Core | 8.0.11 | MIT | https://github.com/dotnet/efcore |
| Microsoft.EntityFrameworkCore | 8.0.11 | MIT | https://github.com/dotnet/efcore |
| Microsoft.EntityFrameworkCore.\* | 8.0.11 | MIT | https://github.com/dotnet/efcore |
| Microsoft.Extensions.\* | 8.0.x / 10.0.0 | MIT | https://github.com/dotnet/runtime |
| Microsoft.NETCore.Platforms | 3.1.0 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Web.WebView2 | 1.0.2651.64 | Proprietary — see Microsoft Software License Terms | https://aka.ms/webview2 |
| Microsoft.Win32.Registry | 4.7.0 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Win32.SystemEvents | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Windows.SDK.\* | various | MIT (where applicable) | https://github.com/microsoft/win32metadata |
| Mono.TextTemplating | 2.2.1 | MIT | https://github.com/mono/t4 |
| NAudio.\* (Asio/Core/Midi/Wasapi/WinForms/WinMM) | 2.2.1 | MIT | https://github.com/naudio/NAudio |
| NuGet.Versioning | 6.14.0 | Apache-2.0 | https://github.com/NuGet/NuGet.Client |

> **Note:** the list above is a representative subset; the complete transitive set is reproducible via `dotnet list package --include-transitive --format json`. SPDX identifiers above are derived from each package's NuGet `<license>` metadata.

---

## Whisper speech models (downloaded on demand, not bundled)

The optional Whisper local models (`ggml-*-q8_0.bin`, five sizes) are ggml conversions of the
OpenAI Whisper weights (Copyright © 2022 OpenAI, **MIT**) published by the whisper.cpp project
(Copyright © Georgi Gerganov and contributors, **MIT**): https://huggingface.co/ggerganov/whisper.cpp.
The app fetches them at the user's request from DV Studio's model mirror
(models.voicewink.app), which serves byte-identical copies of the upstream files at the pinned
revision named in the download path — falling back to the upstream repository itself when the
mirror is unavailable or serves a file that fails verification; each download is verified
against the SHA-256 pins in `PredefinedModels` at install time.

## Whisper.cpp runtime (bundled via Whisper.net.Runtime + Whisper.net.Runtime.Vulkan)

VoiceWink ships pre-built `whisper.dll` native binaries via the `Whisper.net.Runtime` NuGet package, and the GPU (Vulkan) build of the same runtime — including `ggml-vulkan-whisper.dll` — via `Whisper.net.Runtime.Vulkan`. The underlying `whisper.cpp` project (Copyright © Georgi Gerganov and contributors) is distributed under the **MIT licence**: https://github.com/ggerganov/whisper.cpp/blob/master/LICENSE

---

## OpenAI Whisper tokenizer data (bundled)

VoiceWink ships the Whisper BPE tokenizer rank files `multilingual.tiktoken` and `gpt2.tiktoken` (in `Assets/Tokenizer/`), copied verbatim from the OpenAI Whisper repository (Copyright © 2022 OpenAI), distributed under the **MIT licence**: https://github.com/openai/whisper/blob/main/LICENSE

---

## Microsoft.Web.WebView2

The WebView2 runtime is distributed under Microsoft Software License Terms. VoiceWink does not redistribute the WebView2 runtime itself; users are expected to have the Microsoft Edge WebView2 runtime installed via Windows Update, the Microsoft Web Installer, or another Microsoft-sanctioned channel.

See: https://aka.ms/webview2 and https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution

---

## MIT licence (applies to all packages marked "MIT" above)

```
MIT License

Copyright (c) <year> <copyright holders, see each package's NuGet metadata and project page>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

MIT requires that the copyright notice **and** the permission notice accompany every distribution.

The generic block above carries the permission notice; the copyright holders are named per package in the tables above. For the MIT works whose **native binaries VoiceWink actually redistributes**, the notice is reproduced in full below rather than pointed at.

```
MIT License

Copyright (c) Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

*(ONNX Runtime — `onnxruntime.dll`, shipped in the payload root. Text as published at https://github.com/microsoft/onnxruntime/blob/main/LICENSE.)*

---

## Apache 2.0 (applies to all packages marked "Apache-2.0" above)

Apache 2.0 §4(a) requires that any other recipient of the work receive **a copy of this License** — a link does not satisfy it. The full licence text is reproduced below. This file ships at the root of the installed application next to `LICENSE`.
<!-- Maintainers: scripts/check-publish-payload.ps1 fails the release if the licence text is
     missing from this file. The text below was diffed against
     https://www.apache.org/licenses/LICENSE-2.0.txt on 2026-08-03 and matches exactly
     (whitespace-normalised comparison, 10,221 characters). Re-verify the same way rather than
     eyeballing it if the block is ever edited: a licence reproduced from memory is not a licence
     reproduced. -->

Apache 2.0 §4(d) additionally requires that a `NOTICE` file, **where the work has one**, be reproduced in the redistribution. **sherpa-onnx publishes no `NOTICE` file**, so §4(d) has nothing to reproduce for it; the ONNX Runtime dependency notices that *are* required ship in full — see the section above.
<!-- Maintainers: if a future Apache-licensed dependency publishes a NOTICE, it must be added
     here; do not assume packaging carries it. -->

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```

---

## How to update this file

```pwsh
# From the repo root
dotnet list src/VoiceWink/VoiceWink.csproj package --include-transitive --format json > /tmp/packages.json
# Compare the package list to the tables above; add/remove/version-bump rows as needed.
# Re-confirm SPDX identifiers against each package's nupkg <license> element.
```

Update this file whenever a new top-level dependency is added or an existing one bumps a major version. Transitive-only version bumps generally do not require an update unless the licence changes.
