# Third-Party Notices

VoiceWink (Copyright © 2025-2026 Dieter Verlaeckt / DV Studio) is licensed under the **GNU General Public License v3 (GPL-3.0-only)**. See [`LICENSE`](LICENSE) for the full text.

VoiceWink incorporates third-party software components. Each component is the property of its respective copyright holders, and is distributed under the terms of its own licence as listed below.

This file is the manifest required for compliance with the attribution clauses of the MIT, Apache-2.0, BSD-3-Clause and similar permissive licences carried by the dependencies. The full licence text of each component is included in the binary distribution next to the corresponding `.dll` (via NuGet's standard `licenses` metadata), and is also retrievable from the linked project pages.

Versions last synced 2026-10-28 (.NET 10 and Windows App SDK 2.6: every direct row and every
listed transitive row re-read from the restored package graph; the shipped win-x64
`onnxruntime.dll` is unchanged and still reads 1.27.0, matching the notices file below).

---

## Direct dependencies

| Package | Version | Licence (SPDX) | Project URL |
|---|---|---|---|
| CommunityToolkit.Mvvm | 8.4.0 | MIT | https://github.com/CommunityToolkit/dotnet |
| H.NotifyIcon.WinUI | 2.1.0 | MIT | https://github.com/HavenDV/H.NotifyIcon |
| Microsoft.EntityFrameworkCore.Design | 10.0.14 | MIT | https://github.com/dotnet/efcore |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.14 | MIT | https://github.com/dotnet/efcore |
| Microsoft.Extensions.DependencyInjection | 10.0.14 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Http | 10.0.14 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Windows.CsWin32 | 0.3.106 | MIT | https://github.com/microsoft/CsWin32 |
| Microsoft.WindowsAppSDK | 2.6.4 | MIT | https://github.com/microsoft/WindowsAppSDK |
| NAudio | 2.2.1 | MIT | https://github.com/naudio/NAudio |
| Sentry.Serilog | 6.4.1 | MIT | https://github.com/getsentry/sentry-dotnet |
| Serilog | 4.1.0 | Apache-2.0 | https://github.com/serilog/serilog |
| Serilog.Sinks.Debug | 3.0.0 | Apache-2.0 | https://github.com/serilog/serilog-sinks-debug |
| Serilog.Sinks.File | 6.0.0 | Apache-2.0 | https://github.com/serilog/serilog-sinks-file |
| SharpHook | 5.3.8 | MIT | https://github.com/TolikPylypchuk/SharpHook |
| System.Drawing.Common | 10.0.14 | MIT | https://github.com/dotnet/runtime |
| System.Management | 10.0.14 | MIT | https://github.com/dotnet/runtime |
| System.Security.Cryptography.ProtectedData | 10.0.14 | MIT | https://github.com/dotnet/runtime |
| Velopack | 0.0.1298 | MIT | https://github.com/velopack/velopack |
| Whisper.net | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
| Whisper.net.Runtime | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
| Whisper.net.Runtime.Vulkan | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
| Whisper.net.Runtime.NoAvx | 1.9.1 | MIT | https://github.com/sandrohanea/whisper.net |
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

VoiceWink ships **`parakeet-server.exe`** (59,278,848 bytes, v0.6.1 win-vulkan-x64, in
`runtimes\win-x64\`) — the resident helper process the parakeet.cpp transcription backend
spawns. It is a statically linked build of:

| Project | Licence (SPDX) | Source |
|---|---|---|
| parakeet.cpp | **MIT** | https://github.com/mudler/parakeet.cpp |
| ggml (embedded by parakeet.cpp) | **MIT** | https://github.com/ggml-org/ggml |
| ced.cpp (embedded by parakeet.cpp) | **MIT** | https://github.com/localai-org/ced.cpp |
| voice-detect.cpp (embedded by parakeet.cpp) | **MIT** | https://github.com/localai-org/voice-detect.cpp |
| cpp-httplib 0.18.3 (embedded by parakeet.cpp's server) | **MIT** | https://github.com/yhirose/cpp-httplib |

parakeet.cpp is © Ettore Di Giacinto (mudler) and contributors; ggml is © Georgi Gerganov
and contributors; ced.cpp is © 2026 the ced.cpp authors; voice-detect.cpp is © 2026 the
voice-detect.cpp authors; cpp-httplib is © 2024 Yuji Hirose. All are distributed under the MIT
License — full text below. The binary's
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

## llama-server (bundled)

VoiceWink ships llama.cpp's **`llama-server`**, build **b11147** (win-vulkan-x64 release), in
`runtimes\win-x64\llama\` — the local engine behind AI enhancement on this PC. The directory
holds `llama-server.exe` and the libraries it loads: `llama-server-impl.dll`, `llama-common.dll`,
`llama.dll`, `mtmd.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-vulkan.dll`, fourteen
`ggml-cpu-*.dll` variants and `libomp.dll`, plus copies of the Vulkan loader and Visual C++
runtime DLLs listed elsewhere in this file.

The same build's native ARM64 release (win-opencl-adreno-arm64) ships in
`runtimes\win-arm64\llama\` and is what runs on an ARM64 PC whose processor supports it: `llama-server.exe`,
`llama-server-impl.dll`, `llama-common.dll`, `llama.dll`, `mtmd.dll`, `ggml.dll`,
`ggml-base.dll`, `ggml-cpu.dll`, `ggml-opencl.dll` and `libomp.dll`, plus ARM64 copies of two
Visual C++ runtime DLLs. The licences below cover both directories.

| Project | Licence (SPDX) | Source |
|---|---|---|
| llama.cpp (incl. ggml) | **MIT** | https://github.com/ggml-org/llama.cpp |
| LLVM OpenMP runtime (`libomp.dll`) | **Apache-2.0 WITH LLVM-exception** | https://github.com/llvm/llvm-project |
| BoringSSL 0.20260903.0 (in `llama-common.dll`, `llama-server-impl.dll`) | **Apache-2.0** | https://boringssl.googlesource.com/boringssl |
| cpp-httplib 0.57.1 (in `llama-common.dll`, `llama-server-impl.dll`) | **MIT** | https://github.com/yhirose/cpp-httplib |
| nlohmann/json 3.12.0 (in `llama-common.dll`) | **MIT** | https://github.com/nlohmann/json |
| rotate-bits (in `llama-common.dll`, `mtmd.dll`) | **MIT** | https://github.com/jb55/rotate-bits.h |
| llamafile SGEMM (in the `ggml-cpu*.dll` backends) | **MIT** | https://github.com/ggml-org/llama.cpp/blob/b11147/ggml/src/ggml-cpu/llamafile/sgemm.cpp |
| nerdamer-prime (in the web UI built into `llama-server-impl.dll`) | **MIT** | https://github.com/together-science/nerdamer-prime |
| decimal.js (in the web UI built into `llama-server-impl.dll`) | **MIT** | https://github.com/MikeMcl/decimal.js |

llama.cpp is Copyright (c) 2023-2026 The ggml authors, distributed under the MIT License — full
text below. llama.cpp's release build also includes the components listed after it. BoringSSL is
© The BoringSSL Authors, The OpenSSL Project Authors and the other holders named in its source
files, under the Apache License 2.0 — full text below. cpp-httplib is © 2017-2026 Yuji Hirose,
nlohmann/json is © 2013-2025 Niels Lohmann (with parts © 2008-2009 Björn Hoehrmann and 2009
Florian Loitsch, likewise MIT; Hedley © 2016-2021 Evan Nemerson, public domain under CC0-1.0; and
parts of Google Abseil © 2018 The Abseil Authors under Apache-2.0 — full text below), rotate-bits
is © 2021 William Casarin, llamafile SGEMM is © 2024 Mozilla Foundation, nerdamer-prime is © 2023 together-science
and decimal.js is © 2025 Michael Mclaughlin, each under the MIT License — full text below. The
build also includes public-domain code (stb_image, a SHA-256 implementation, sheredom's
subprocess.h, big-integer), which carries no attribution duty. `libomp.dll` is part
of the LLVM Project; its licence file, as shipped in the upstream llama.cpp release archive, is
reproduced verbatim in the next section.

## LLVM OpenMP runtime licence (libomp.dll)

```
==============================================================================
The LLVM Project is under the Apache License v2.0 with LLVM Exceptions:
==============================================================================

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


---- LLVM Exceptions to the Apache 2.0 License ----

As an exception, if, as a result of your compiling your source code, portions
of this Software are embedded into an Object form of such source code, you
may redistribute such embedded portions in such Object form without complying
with the conditions of Sections 4(a), 4(b) and 4(d) of the License.

In addition, if you combine or link compiled forms of this Software with
software that is licensed under the GPLv2 ("Combined Software") and if a
court of competent jurisdiction determines that the patent provision (Section
3), the indemnity provision (Section 9) or other Section of the License
conflicts with the conditions of the GPLv2, you may retroactively and
prospectively choose to deem waived or otherwise exclude such Section(s) of
the License, but only in their entirety and only with respect to the Combined
Software.

==============================================================================
Software from third parties included in the LLVM Project:
==============================================================================
The LLVM Project contains third party software which is under different license
terms. All such code will be identified clearly using at least one of two
mechanisms:
1) It will be in a separate directory tree with its own `LICENSE.txt` or
   `LICENSE` file at the top containing the specific license and restrictions
   which apply to that software, or
2) It will contain specific license and restriction terms at the top of every
   file.

==============================================================================
Legacy LLVM License (https://llvm.org/docs/DeveloperPolicy.html#legacy):
==============================================================================

The software contained in this directory tree is dual licensed under both the
University of Illinois "BSD-Like" license and the MIT license.  As a user of
this code you may choose to use it under either license.  As a contributor,
you agree to allow your code to be used under both.  The full text of the
relevant licenses is included below.

In addition, a license agreement from the copyright/patent holders of the
software contained in this directory tree is included below.

==============================================================================

University of Illinois/NCSA
Open Source License

Copyright (c) 1997-2019 Intel Corporation

All rights reserved.

Developed by:
    OpenMP Runtime Team
    Intel Corporation
    http://www.openmprtl.org

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal with
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

    * Redistributions of source code must retain the above copyright notice,
      this list of conditions and the following disclaimers.

    * Redistributions in binary form must reproduce the above copyright notice,
      this list of conditions and the following disclaimers in the
      documentation and/or other materials provided with the distribution.

    * Neither the names of Intel Corporation OpenMP Runtime Team nor the
      names of its contributors may be used to endorse or promote products
      derived from this Software without specific prior written permission.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
CONTRIBUTORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS WITH THE
SOFTWARE.

==============================================================================

Copyright (c) 1997-2019 Intel Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

==============================================================================

Intel Corporation

Software Grant License Agreement ("Agreement")

Except for the license granted herein to you, Intel Corporation ("Intel") reserves
all right, title, and interest in and to the Software (defined below).

Definition

"Software" means the code and documentation as well as any original work of
authorship, including any modifications or additions to an existing work, that
is intentionally submitted by Intel to llvm.org (http://llvm.org) ("LLVM") for
inclusion in, or documentation of, any of the products owned or managed by LLVM
(the "Work"). For the purposes of this definition, "submitted" means any form of
electronic, verbal, or written communication sent to LLVM or its
representatives, including but not limited to communication on electronic
mailing lists, source code control systems, and issue tracking systems that are
managed by, or on behalf of, LLVM for the purpose of discussing and improving
the Work, but excluding communication that is conspicuously marked otherwise.

1. Grant of Copyright License. Subject to the terms and conditions of this
   Agreement, Intel hereby grants to you and to recipients of the Software
   distributed by LLVM a perpetual, worldwide, non-exclusive, no-charge,
   royalty-free, irrevocable copyright license to reproduce, prepare derivative
   works of, publicly display, publicly perform, sublicense, and distribute the
   Software and such derivative works.

2. Grant of Patent License. Subject to the terms and conditions of this
   Agreement, Intel hereby grants you and to recipients of the Software
   distributed by LLVM a perpetual, worldwide, non-exclusive, no-charge,
   royalty-free, irrevocable (except as stated in this section) patent license
   to make, have made, use, offer to sell, sell, import, and otherwise transfer
   the Work, where such license applies only to those patent claims licensable
   by Intel that are necessarily infringed by Intel's Software alone or by
   combination of the Software with the Work to which such Software was
   submitted. If any entity institutes patent litigation against Intel or any
   other entity (including a cross-claim or counterclaim in a lawsuit) alleging
   that Intel's Software, or the Work to which Intel has contributed constitutes
   direct or contributory patent infringement, then any patent licenses granted
   to that entity under this Agreement for the Software or Work shall terminate
   as of the date such litigation is filed.

Unless required by applicable law or agreed to in writing, the software is
provided on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND,
either express or implied, including, without limitation, any warranties or
conditions of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
PARTICULAR PURPOSE.

==============================================================================
```

## llama-server web UI (bundled npm packages)

`llama-server-impl.dll` (both directories) carries llama.cpp's web UI, compiled from its
`tools/ui` at build b11147 into one JavaScript bundle and one stylesheet. VoiceWink starts
llama-server with the UI switched off (`--no-webui`), but the UI still ships inside the binary.
The first table lists the npm packages the UI's own code reaches at run time according to that
build's `package-lock.json`: the packages it imports and their dependencies, without build tools
beyond the small runtime they inject and without the server-side dependencies of the MCP SDK that
its browser client never imports. The second table lists packages that two of them carry already
compiled into their published JavaScript, which the lockfile does not show. The bundle may contain
fewer, since unused code is dropped when it is built. Licence texts are below (MIT, Apache-2.0,
ISC, BSD-2-Clause, BSD-3-Clause); a row naming two licences applies each to the holders marked
with it. `robust-predicates` is public domain (Unlicense). Of the Apache-2.0 packages only Dexie.js
ships a `NOTICE` file; its content is the copyright line in its row. The vendored nerdamer-prime,
decimal.js and big-integer are listed under llama-server above.

| Package | Version | Licence | Copyright |
|---|---|---|---|
| `@antfu/install-pkg` | 1.1.0 | MIT | © 2021 Anthony Fu |
| `@braintree/sanitize-url` | 7.1.2 | MIT | © 2017 Braintree |
| `@chevrotain/types` | 11.1.2 | Apache-2.0 | © Shahar Soel |
| `@floating-ui/core` | 1.7.5 | MIT | © 2021-present Floating UI contributors |
| `@floating-ui/dom` | 1.7.6 | MIT | © 2021-present Floating UI contributors |
| `@floating-ui/utils` | 0.2.11 | MIT | © 2021-present Floating UI contributors |
| `@iconify/types` | 2.0.0 | MIT | © 2021 - 2022 Vjacheslav Trushkin / Iconify OÜ |
| `@iconify/utils` | 3.1.3 | MIT | © 2021-PRESENT Vjacheslav Trushkin |
| `@jridgewell/gen-mapping` | 0.3.13 | MIT | © 2024 Justin Ridgewell |
| `@jridgewell/remapping` | 2.3.5 | MIT | © 2024 Justin Ridgewell |
| `@jridgewell/resolve-uri` | 3.1.2 | MIT | © 2019 Justin Ridgewell |
| `@jridgewell/sourcemap-codec` | 1.5.5 | MIT | © 2024 Justin Ridgewell |
| `@jridgewell/trace-mapping` | 0.3.31 | MIT | © 2024 Justin Ridgewell |
| `@lucide/svelte` | 1.25.0 | ISC AND MIT | © 2026 Lucide Icons and Contributors (ISC); © 2013-present Cole Bemis, Feather icons (MIT) |
| `@mermaid-js/parser` | 1.1.1 | MIT | © 2023 Yokozuna59 |
| `@modelcontextprotocol/sdk` | 1.30.0 | MIT | © 2024 Anthropic, PBC |
| `@polka/url` | 1.0.0-next.29 | MIT | © Luke Edwards |
| `@standard-schema/spec` | 1.1.0 | MIT | © 2024 Colin McDonnell |
| `@sveltejs/acorn-typescript` | 1.0.10 | MIT | © 2022 Tyreal Hu; © 2025 The Svelte Team |
| `@sveltejs/kit` | 2.70.2 | MIT | © 2020 [these people](https://github.com/sveltejs/kit/graphs/contributors) |
| `@tailwindcss/forms` | 0.5.10 | MIT | © Tailwind Labs, Inc |
| `@tailwindcss/typography` | 0.5.16 | MIT | © Tailwind Labs, Inc |
| `@ungap/structured-clone` | 1.3.1 | ISC | © 2021, Andrea Giammarchi, @WebReflection |
| `@upsetjs/venn.js` | 2.0.0 | MIT | © 2013 Ben Frederickson; © 2021 Samuel Gratzl |
| `acorn` | 8.16.0 | MIT | © 2012-2022 by various contributors (see AUTHORS) |
| `ajv` | 8.20.0 | MIT | © 2015-2021 Evgeny Poberezkin |
| `ajv-formats` | 3.0.1 | MIT | © 2020 Evgeny Poberezkin |
| `aria-query` | 5.3.1 | Apache-2.0 | © 2020 A11yance |
| `axobject-query` | 4.1.0 | Apache-2.0 | © 2020 A11yance |
| `bail` | 2.0.2 | MIT | © 2015 Titus Wormer |
| `bits-ui` | 2.18.1 | MIT | © 2023 Hunter Johnston |
| `ccount` | 2.0.1 | MIT | © 2015 Titus Wormer |
| `character-entities` | 2.0.2 | MIT | © 2015 Titus Wormer |
| `character-entities-html4` | 2.1.0 | MIT | © 2015 Titus Wormer |
| `character-entities-legacy` | 3.0.0 | MIT | © 2015 Titus Wormer |
| `clsx` | 2.1.1 | MIT | © Luke Edwards (lukeed.com) |
| `comma-separated-tokens` | 2.0.3 | MIT | © 2016 Titus Wormer |
| `commander` | 7.2.0, 8.3.0 | MIT | © 2011 TJ Holowaychuk |
| `content-type` | 1.0.5 | MIT | © 2015 Douglas Christopher Wilson |
| `cookie` | 1.1.1 | MIT | © 2012-2014 Roman Shtylman; © 2015 Douglas Christopher Wilson |
| `cose-base` | 1.0.3, 2.2.0 | MIT | © 2019 - present, iVis@Bilkent |
| `cytoscape` | 3.34.0 | MIT | © 2016-2026, The Cytoscape Consortium |
| `cytoscape-cose-bilkent` | 4.1.0 | MIT | © 2016-2018, The Cytoscape Consortium |
| `cytoscape-fcose` | 2.2.0 | MIT | © 2018 - present, iVis-at-Bilkent |
| `d3` | 7.9.0 | ISC | © 2010-2023 Mike Bostock |
| `d3-array` | 2.12.1, 3.2.4 | BSD-3-Clause / ISC | © 2010-2020 Mike Bostock; © 2010-2023 Mike Bostock |
| `d3-axis` | 3.0.0 | ISC | © 2010-2021 Mike Bostock |
| `d3-brush` | 3.0.0 | ISC | © 2010-2021 Mike Bostock |
| `d3-chord` | 3.0.1 | ISC | © 2010-2021 Mike Bostock |
| `d3-color` | 3.1.0 | ISC | © 2010-2022 Mike Bostock |
| `d3-contour` | 4.0.2 | ISC | © 2012-2023 Mike Bostock |
| `d3-delaunay` | 6.0.4 | ISC | © 2018-2021 Observable, Inc; © 2021 Mapbox |
| `d3-dispatch` | 3.0.1 | ISC | © 2010-2021 Mike Bostock |
| `d3-drag` | 3.0.0 | ISC | © 2010-2021 Mike Bostock |
| `d3-dsv` | 3.0.1 | ISC | © 2013-2021 Mike Bostock |
| `d3-ease` | 3.0.1 | BSD-3-Clause | © 2010-2021 Mike Bostock; © 2001 Robert Penner |
| `d3-fetch` | 3.0.1 | ISC | © 2016-2021 Mike Bostock |
| `d3-force` | 3.0.0 | ISC | © 2010-2021 Mike Bostock |
| `d3-format` | 3.1.2 | ISC | © 2010-2026 Mike Bostock |
| `d3-geo` | 3.1.1 | ISC AND MIT | © 2010-2024 Mike Bostock (ISC); © 2008-2012 Charles Karney, GeographicLib (MIT) |
| `d3-hierarchy` | 3.1.2 | ISC | © 2010-2021 Mike Bostock |
| `d3-interpolate` | 3.0.1 | ISC | © 2010-2021 Mike Bostock |
| `d3-path` | 1.0.9, 3.1.0 | BSD-3-Clause / ISC | © 2015-2016 Mike Bostock; © 2015-2022 Mike Bostock |
| `d3-polygon` | 3.0.1 | ISC | © 2010-2021 Mike Bostock |
| `d3-quadtree` | 3.0.1 | ISC | © 2010-2021 Mike Bostock |
| `d3-random` | 3.0.1 | ISC | © 2010-2021 Mike Bostock |
| `d3-sankey` | 0.12.3 | BSD-3-Clause | © 2015, Mike Bostock |
| `d3-scale` | 4.0.2 | ISC | © 2010-2021 Mike Bostock |
| `d3-scale-chromatic` | 3.1.0 | ISC AND Apache-2.0 | © 2010-2024 Mike Bostock (ISC); © 2002 Cynthia Brewer, Mark Harrower, and The Pennsylvania State University, ColorBrewer (Apache-2.0) |
| `d3-selection` | 3.0.0 | ISC | © 2010-2021 Mike Bostock |
| `d3-shape` | 1.3.7, 3.2.0 | BSD-3-Clause / ISC | © 2010-2015 Mike Bostock; © 2010-2022 Mike Bostock |
| `d3-time` | 3.1.0 | ISC | © 2010-2022 Mike Bostock |
| `d3-time-format` | 4.1.0 | ISC | © 2010-2021 Mike Bostock |
| `d3-timer` | 3.0.1 | ISC | © 2010-2021 Mike Bostock |
| `d3-transition` | 3.0.1 | ISC | © 2010-2021 Mike Bostock |
| `d3-zoom` | 3.0.0 | ISC | © 2010-2021 Mike Bostock |
| `dagre-d3-es` | 7.0.14 | MIT | © 2022-2024 Thibaut Lassalle, David Newell, Alois Klink, Sidharth Vinod and dagre-es contributors |
| `dayjs` | 1.11.21 | MIT | © 2018-present, iamkun |
| `debug` | 4.4.3 | MIT | © 2014-2017 TJ Holowaychuk; © 2018-2021 Josh Junon |
| `decode-named-character-reference` | 1.3.0 | MIT | © Titus Wormer |
| `delaunator` | 5.1.0 | ISC | © 2026, Mapbox |
| `dequal` | 2.0.3 | MIT | © Luke Edwards (lukeed.com) |
| `devalue` | 5.8.1 | MIT | © 2018-19 [these people](https://github.com/rich-harris/devalue/graphs/contributors) |
| `devlop` | 1.1.0 | MIT | © 2023 Titus Wormer |
| `dexie` | 4.4.3 | Apache-2.0 | © 2014-2017 David Fahlander (Dexie.js NOTICE) |
| `dompurify` | 3.4.13 | Apache-2.0 (elected; MPL-2.0 OR Apache-2.0) | © Dr.-Ing. Mario Heiderich, Cure53 |
| `entities` | 6.0.1 | BSD-2-Clause | © Felix Böhm |
| `es-toolkit` | 1.47.0 | MIT | © 2024 Viva Republica, Inc; © OpenJS Foundation and other contributors |
| `escape-string-regexp` | 5.0.0 | MIT | © Sindre Sorhus |
| `esm-env` | 1.2.2 | MIT | © 2022 Benjamin McCann |
| `esrap` | 2.2.11 | MIT | © 2023-2025 [these people](https://github.com/sveltejs/esrap/graphs/contributors) |
| `eventsource` | 3.0.7 | MIT | © EventSource GitHub organisation |
| `eventsource-parser` | 3.1.0 | MIT | © 2026 Espen Hovlandsdal |
| `extend` | 3.0.2 | MIT | © 2014 Stefan Thomas |
| `fast-deep-equal` | 3.1.3 | MIT | © 2017 Evgeny Poberezkin |
| `fast-uri` | 3.1.5 | BSD-3-Clause | © 2011-2021 Gary Court; © 2021-present The Fastify team |
| `fflate` | 0.8.3 | MIT | © 2026 Arjun Barrett |
| `hachure-fill` | 0.5.2 | MIT | © 2023 Preet Shihn |
| `hast-util-from-dom` | 5.0.1 | ISC | © Keith McKnight |
| `hast-util-from-html` | 2.0.3 | MIT | © 2022 Titus Wormer |
| `hast-util-from-html-isomorphic` | 2.0.0 | MIT | © 2023 Remco Haszing |
| `hast-util-from-parse5` | 8.0.3 | MIT | © Titus Wormer |
| `hast-util-is-element` | 3.0.0 | MIT | © 2016 Titus Wormer |
| `hast-util-parse-selector` | 4.0.0 | MIT | © 2016 Titus Wormer |
| `hast-util-to-html` | 9.0.5 | MIT | © Titus Wormer |
| `hast-util-to-text` | 4.0.2 | MIT | © 2019 Titus Wormer |
| `hast-util-whitespace` | 3.0.0 | MIT | © 2016 Titus Wormer |
| `hastscript` | 9.0.1 | MIT | © Titus Wormer |
| `highlight.js` | 11.11.1 | BSD-3-Clause | © 2006, Ivan Sagalaev |
| `html-void-elements` | 3.0.0 | MIT | © 2016 Titus Wormer |
| `iconv-lite` | 0.6.3 | MIT | © 2011 Alexander Shtuchkin |
| `import-meta-resolve` | 4.2.0 | MIT | © Titus Wormer; © Node.js contributors; © Joyent, Inc. and other Node contributors |
| `inline-style-parser` | 0.2.7 | MIT | © 2012 TJ Holowaychuk |
| `internmap` | 1.0.1, 2.0.3 | ISC | © 2021 Mike Bostock |
| `is-plain-obj` | 4.1.0 | MIT | © Sindre Sorhus |
| `is-reference` | 3.0.3 | MIT | © Rich Harris |
| `json-schema-traverse` | 1.0.0 | MIT | © 2017 Evgeny Poberezkin |
| `katex` | 0.16.47 | MIT | © 2013-2020 Khan Academy and other contributors |
| `khroma` | 2.1.0 | MIT | © 2019-present Fabio Spampinato, Andrew Maney |
| `kleur` | 4.1.5 | MIT | © Luke Edwards (lukeed.com) |
| `layout-base` | 1.0.2, 2.0.1 | MIT | © 2019 iVis@Bilkent |
| `locate-character` | 3.0.0 | MIT | © Rich Harris |
| `lodash-es` | 4.18.1 | MIT | © OpenJS Foundation and other contributors; based on Underscore.js, © Jeremy Ashkenas, DocumentCloud and Investigative Reporters & Editors |
| `longest-streak` | 3.1.0 | MIT | © 2015 Titus Wormer |
| `lowlight` | 3.3.0 | MIT | © Titus Wormer |
| `lz-string` | 1.5.0 | MIT | © 2013 pieroxy |
| `magic-string` | 0.30.21 | MIT | © 2018 Rich Harris |
| `markdown-table` | 3.0.4 | MIT | © Titus Wormer |
| `marked` | 16.4.2 | MIT AND BSD-3-Clause | © 2018+ MarkedJS (MIT); © 2011-2018 Christopher Jeffrey (MIT); © 2004 John Gruber, Markdown (BSD-3-Clause) |
| `mdast-util-find-and-replace` | 3.0.2 | MIT | © Titus Wormer |
| `mdast-util-from-markdown` | 2.0.3 | MIT | © Titus Wormer |
| `mdast-util-gfm` | 3.1.0 | MIT | © Titus Wormer |
| `mdast-util-gfm-autolink-literal` | 2.0.1 | MIT | © 2020 Titus Wormer |
| `mdast-util-gfm-footnote` | 2.1.0 | MIT | © Titus Wormer |
| `mdast-util-gfm-strikethrough` | 2.0.0 | MIT | © 2020 Titus Wormer |
| `mdast-util-gfm-table` | 2.0.0 | MIT | © 2020 Titus Wormer |
| `mdast-util-gfm-task-list-item` | 2.0.0 | MIT | © 2020 Titus Wormer |
| `mdast-util-math` | 3.0.0 | MIT | © 2020 Titus Wormer |
| `mdast-util-newline-to-break` | 2.0.0 | MIT | © 2017 Titus Wormer |
| `mdast-util-phrasing` | 4.1.0 | MIT | © 2017 Titus Wormer; © 2017 Victor Felder |
| `mdast-util-to-hast` | 13.2.1 | MIT | © 2016 Titus Wormer |
| `mdast-util-to-markdown` | 2.1.2 | MIT | © Titus Wormer |
| `mdast-util-to-string` | 4.0.0 | MIT | © 2015 Titus Wormer |
| `mermaid` | 11.15.0 | MIT | © 2014 - 2022 Knut Sveidqvist |
| `micromark` | 4.0.2 | MIT | © Titus Wormer |
| `micromark-core-commonmark` | 2.0.3 | MIT | © Titus Wormer |
| `micromark-extension-gfm` | 3.0.0 | MIT | © 2020 Titus Wormer |
| `micromark-extension-gfm-autolink-literal` | 2.1.0 | MIT | © 2020 Titus Wormer |
| `micromark-extension-gfm-footnote` | 2.1.0 | MIT | © 2021 Titus Wormer |
| `micromark-extension-gfm-strikethrough` | 2.1.0 | MIT | © 2020 Titus Wormer |
| `micromark-extension-gfm-table` | 2.1.1 | MIT | © Titus Wormer |
| `micromark-extension-gfm-tagfilter` | 2.0.0 | MIT | © 2020 Titus Wormer |
| `micromark-extension-gfm-task-list-item` | 2.1.0 | MIT | © 2020 Titus Wormer |
| `micromark-extension-math` | 3.1.0 | MIT | © 2020 Titus Wormer |
| `micromark-factory-destination` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-factory-label` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-factory-space` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-factory-title` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-factory-whitespace` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-character` | 2.1.1 | MIT | © Titus Wormer |
| `micromark-util-chunked` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-classify-character` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-combine-extensions` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-decode-numeric-character-reference` | 2.0.2 | MIT | © Titus Wormer |
| `micromark-util-decode-string` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-encode` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-html-tag-name` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-normalize-identifier` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-resolve-all` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-sanitize-uri` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-subtokenize` | 2.1.0 | MIT | © Titus Wormer |
| `micromark-util-symbol` | 2.0.1 | MIT | © Titus Wormer |
| `micromark-util-types` | 2.0.2 | MIT | © Titus Wormer |
| `mode-watcher` | 1.1.0 | MIT | © 2024 Hunter Johnston; © 2024 Svecosystem |
| `mrmime` | 2.0.1 | MIT | © Luke Edwards |
| `ms` | 2.1.3 | MIT | © 2020 Vercel, Inc |
| `package-manager-detector` | 1.6.0 | MIT | © 2020-PRESENT Anthony Fu |
| `parse5` | 7.3.0 | MIT | © 2013-2019 Ivan Nikulin |
| `path-data-parser` | 0.1.0 | MIT | © 2020 Preet Shihn |
| `pdfjs-dist` | 5.4.54 | Apache-2.0 | © 2024 Mozilla Foundation |
| `pkce-challenge` | 5.0.1 | MIT | © 2019 crouchcd |
| `points-on-curve` | 0.2.0 | MIT | © 2020 Preet Shihn |
| `points-on-path` | 0.2.1 | MIT | © 2020 Preet |
| `property-information` | 7.2.0 | MIT | © Titus Wormer |
| `rehype-highlight` | 7.0.2 | MIT | © Titus Wormer |
| `rehype-katex` | 7.0.1 | MIT | © Junyoung Choi |
| `rehype-stringify` | 10.0.1 | MIT | © Titus Wormer |
| `remark` | 15.0.1 | MIT | © 2014 Titus Wormer |
| `remark-breaks` | 4.0.0 | MIT | © 2017 Titus Wormer |
| `remark-gfm` | 4.0.1 | MIT | © Titus Wormer |
| `remark-math` | 6.0.0 | MIT | © Junyoung Choi |
| `remark-parse` | 11.0.0 | MIT | © 2014 Titus Wormer |
| `remark-rehype` | 11.1.2 | MIT | © Titus Wormer |
| `remark-stringify` | 11.0.0 | MIT | © 2014 Titus Wormer |
| `require-from-string` | 2.0.2 | MIT | © Vsevolod Strukchinsky (github.com/floatdrop) |
| `robust-predicates` | 3.0.3 | Unlicense | © Vladimir Agafonkin |
| `roughjs` | 4.6.6 | MIT | © 2019 Preet Shihn |
| `runed` | 0.23.4, 0.25.0, 0.28.0, 0.35.1 | MIT | © 2024 Hunter Johnston; © 2024 Thomas G. Lopes |
| `rw` | 1.3.3 | BSD-3-Clause | © 2014-2016, Michael Bostock |
| `safer-buffer` | 2.1.2 | MIT | © 2018 Nikita Skovoroda |
| `set-cookie-parser` | 3.1.0 | MIT | © 2015 Nathan Friedly |
| `sirv` | 3.0.2 | MIT | © Luke Edwards |
| `space-separated-tokens` | 2.0.2 | MIT | © 2016 Titus Wormer |
| `stringify-entities` | 4.0.4 | MIT | © 2015 Titus Wormer |
| `style-to-object` | 1.0.14 | MIT | © 2017 Menglin "Mark" Xu |
| `stylis` | 4.4.0 | MIT | © 2016-present Sultan Tarimo |
| `svelte` | 5.56.1 | MIT | © 2016-2025 Svelte Contributors |
| `svelte-sonner` | 1.1.1 | MIT | © 2024 Robert Soriano |
| `svelte-toolbelt` | 0.10.6, 0.7.1 | MIT | © 2024 Hunter Johnston; © 2024 Thomas G. Lopes |
| `tabbable` | 6.4.0 | MIT | © 2015 David Clark |
| `tailwind-merge` | 3.6.0 | MIT | © 2021 Dany Castillo |
| `tailwind-variants` | 3.2.2 | MIT | © 2020 Tailwid Variants |
| `tailwindcss` | 4.3.0 | MIT | © Tailwind Labs, Inc |
| `tinyexec` | 1.2.4 | MIT | © 2024 Tinylibs |
| `totalist` | 3.0.1 | MIT | © Luke Edwards (lukeed.com) |
| `trim-lines` | 3.0.1 | MIT | © 2015 Titus Wormer |
| `trough` | 2.2.0 | MIT | © 2016 Titus Wormer |
| `ts-dedent` | 2.2.0 | MIT | © 2018 Tamino Martinius |
| `tw-animate-css` | 1.4.0 | MIT | © 2025 Wombosvideo |
| `unified` | 11.0.5 | MIT | © 2015 Titus Wormer |
| `unist-util-find-after` | 5.0.0 | MIT | © 2015 Titus Wormer |
| `unist-util-is` | 6.0.1 | MIT | © 2015 Titus Wormer |
| `unist-util-position` | 5.0.0 | MIT | © 2015 Titus Wormer |
| `unist-util-remove-position` | 5.0.0 | MIT | © 2016 Titus Wormer |
| `unist-util-stringify-position` | 4.0.0 | MIT | © 2016 Titus Wormer |
| `unist-util-visit` | 5.1.0 | MIT | © 2015 Titus Wormer |
| `unist-util-visit-parents` | 6.0.2 | MIT | © 2016 Titus Wormer |
| `uuid` | 13.0.2 | MIT | © 2010-2020 Robert Kieffer and other contributors |
| `vfile` | 6.0.3 | MIT | © 2015 Titus Wormer |
| `vfile-location` | 5.0.3 | MIT | © 2016 Titus Wormer |
| `vfile-message` | 4.0.3 | MIT | © Titus Wormer |
| `vite` | 7.3.6 | MIT | © 2019-present, VoidZero Inc. and Vite contributors; © 2024 Justin Ridgewell; © 2019 Justin Ridgewell; © 2024-present VoidZero Inc. & Contributors; © 2019 RollupJS Plugin Contributors; © 2019 Elan Shanker, Paul Miller |
| `web-namespaces` | 2.0.1 | MIT | © 2016 Titus Wormer |
| `zimmerframe` | 1.1.4 | MIT | © 2023 [these people](https://github.com/Rich-Harris/zimmerframe/graphs/contributors) |
| `zod` | 4.4.3 | MIT | © 2025 Colin McDonnell |
| `zod-to-json-schema` | 3.25.2 | ISC | © 2020, Stefan Terdell |
| `zwitch` | 2.0.4 | MIT | © 2016 Titus Wormer |

Packages carried pre-built inside other packages' published JavaScript:

| Package | Version | Licence | Copyright | Inside |
|---|---|---|---|---|
| `@chevrotain/gast` | 11.1.1 | Apache-2.0 | © Shahar Soel | @mermaid-js/parser |
| `@chevrotain/regexp-to-ast` | 11.1.1 | Apache-2.0 | © Shahar Soel | @mermaid-js/parser |
| `@chevrotain/utils` | 11.1.1 | Apache-2.0 | © Shahar Soel | @mermaid-js/parser |
| `chevrotain` | 11.1.1 | Apache-2.0 | © Shahar Soel | @mermaid-js/parser |
| `chevrotain-allstar` | 0.3.1 | MIT | © 2022 TypeFox GmbH | @mermaid-js/parser |
| `langium` | 4.2.0 | MIT | © 2021 TypeFox GmbH | @mermaid-js/parser |
| `lodash-es` | 4.17.23 | MIT | © OpenJS Foundation and other contributors; based on Underscore.js, © Jeremy Ashkenas, DocumentCloud and Investigative Reporters & Editors | @mermaid-js/parser |
| `vscode-jsonrpc` | 8.2.0 | MIT | © Microsoft Corporation | @mermaid-js/parser, mermaid |
| `vscode-languageserver-protocol` | 3.17.5 | MIT | © Microsoft Corporation | @mermaid-js/parser, mermaid |
| `vscode-languageserver-textdocument` | 1.0.12 | MIT | © Microsoft Corporation | @mermaid-js/parser |
| `vscode-languageserver-types` | 3.17.5 | MIT | © Microsoft Corporation | @mermaid-js/parser, mermaid |
| `vscode-uri` | 3.1.0 | MIT | © Microsoft | @mermaid-js/parser |
| `js-yaml` | 4.1.1 | MIT | © 2011-2015 Vitaly Puzrin | mermaid |

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
| Microsoft.CodeAnalysis.\* | 5.0.0 | MIT | https://github.com/dotnet/roslyn |
| Microsoft.Data.Sqlite.Core | 10.0.14 | MIT | https://github.com/dotnet/efcore |
| Microsoft.EntityFrameworkCore | 10.0.14 | MIT | https://github.com/dotnet/efcore |
| Microsoft.EntityFrameworkCore.\* | 10.0.14 | MIT | https://github.com/dotnet/efcore |
| Microsoft.Extensions.AI.Abstractions | 10.2.0 | MIT | https://github.com/dotnet/extensions |
| Microsoft.Extensions.\* (all others) | 10.0.14 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Web.WebView2 | 1.0.3719.77 | Proprietary — see Microsoft Software License Terms | https://aka.ms/webview2 |
| Microsoft.Win32.SystemEvents | 10.0.14 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Windows.SDK.\* | various | MIT (where applicable) | https://github.com/microsoft/win32metadata |
| Microsoft.WindowsAppSDK.\* (Base/DWrite/Foundation/InteractiveExperiences/Runtime/Search/Widgets/WinUI) | 2.6.4 | MIT | https://github.com/microsoft/WindowsAppSDK |
| Mono.TextTemplating | 3.0.0 | MIT | https://github.com/mono/t4 |
| NAudio.\* (Asio/Core/Midi/Wasapi/WinForms/WinMM) | 2.2.1 | MIT | https://github.com/naudio/NAudio |
| NuGet.Versioning | 6.14.0 | Apache-2.0 | https://github.com/NuGet/NuGet.Client |
| SQLitePCLRaw.\* (bundle_e_sqlite3/core/lib.e_sqlite3/provider.e_sqlite3) | 2.1.13 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| System.Composition.\* | 9.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Numerics.Tensors | 9.0.0 | MIT | https://github.com/dotnet/runtime |

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

## Whisper.cpp runtime (bundled via Whisper.net.Runtime + Whisper.net.Runtime.Vulkan + Whisper.net.Runtime.NoAvx)

VoiceWink ships pre-built `whisper.dll` native binaries via the `Whisper.net.Runtime` NuGet package, the GPU (Vulkan) build of the same runtime — including `ggml-vulkan-whisper.dll` — via `Whisper.net.Runtime.Vulkan`, and a build for processors without AVX via `Whisper.net.Runtime.NoAvx`. The underlying `whisper.cpp` project (Copyright © Georgi Gerganov and contributors) is distributed under the **MIT licence**: https://github.com/ggerganov/whisper.cpp/blob/master/LICENSE

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

Apache 2.0 §4(d) additionally requires that a `NOTICE` file, **where the work has one**, be reproduced in the redistribution. **sherpa-onnx publishes no `NOTICE` file**, so §4(d) has nothing to reproduce for it, nor for BoringSSL or the llama-server web UI's Apache-2.0 packages other than Dexie.js, whose `NOTICE` content is reproduced in its row; the ONNX Runtime dependency notices that *are* required ship in full — see the section above.
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

<!-- Maintainers: how to update this file. Kept in a comment because the in-app
     Third-party licenses dialog hides comments and must not show these instructions.
     From the repo root:

         dotnet list src/VoiceWink/VoiceWink.csproj package --include-transitive --format json > /tmp/packages.json

     Compare the package list to the tables above; add/remove/version-bump rows as needed.
     Re-confirm SPDX identifiers against each package's nupkg <license> element.

     Update this file whenever a new top-level dependency is added or an existing one bumps a
     major version. Transitive-only version bumps generally do not require an update unless the
     licence changes. -->

## BSD-2-Clause (applies to all packages marked "BSD-2-Clause" above)

```
Copyright (c) <year> <owner>

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of
   conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## BSD-3-Clause (applies to all packages marked "BSD-3-Clause" above)

Each package's own licence words condition 3 for its own name; read the text below with that
package's condition 3:

- `d3-array` 2.12.1, `d3-ease`, `d3-path` 1.0.9, `d3-sankey`, `d3-shape` 1.3.7: "Neither the name
  of the author nor the names of contributors may be used to endorse or promote products derived
  from this software without specific prior written permission."
- `fast-uri`: "The names of any contributors may not be used to endorse or promote products
  derived from this software without specific prior written permission."
- `highlight.js`: "Neither the name of the copyright holder nor the names of its contributors may
  be used to endorse or promote products derived from this software without specific prior
  written permission."
- `rw`: "The name Michael Bostock may not be used to endorse or promote products derived from this
  software without specific prior written permission."
- `marked` (its Markdown component): "Neither the name “Markdown” nor the names of its
  contributors may be used to endorse or promote products derived from this software without
  specific prior written permission."

```
Copyright (c) <year> <owner>

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of
   conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors may be used to
   endorse or promote products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## ISC (applies to all packages marked "ISC" above)

```
Copyright (c) <year> <owner>

Permission to use, copy, modify, and/or distribute this software for any purpose with or without
fee is hereby granted, provided that the above copyright notice and this permission notice appear
in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS
SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE
AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT,
NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE
OF THIS SOFTWARE.
```
