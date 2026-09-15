#!/bin/bash
# Build VoiceWink portable installer (no signing required)
# Produces a .zip with VoiceWinkSetup.ps1 + app files
# Requires: .NET 8 SDK
# Usage: build-portable.sh [--config Debug|Release]   (default: Release, case-insensitive)
set -e

usage() {
    cat <<EOF
Usage: $0 [--config Debug|Release]

Options:
  --config Debug|Release   Build configuration (default: Release). Case-insensitive.
  -h, --help               Show this message and exit.
EOF
}

CONFIG="Release"
while [ $# -gt 0 ]; do
    # Lowercase flag name (but NOT the value) for case-insensitive flag matching.
    arg_lower="$(echo "$1" | tr '[:upper:]' '[:lower:]')"
    case "$arg_lower" in
        --config)
            if [ $# -lt 2 ] || [ -z "$2" ]; then
                echo "ERROR: --config requires a value (Debug or Release)"
                usage
                exit 1
            fi
            CONFIG="$2"
            shift 2
            ;;
        --config=*)
            CONFIG="${1#*=}"
            if [ -z "$CONFIG" ]; then
                echo "ERROR: --config requires a value (Debug or Release)"
                usage
                exit 1
            fi
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "ERROR: unknown argument: $1"
            usage
            exit 1
            ;;
    esac
done

# Canonicalize to Debug or Release (accept any casing).
case "$(echo "$CONFIG" | tr '[:upper:]' '[:lower:]')" in
    debug) CONFIG="Debug" ;;
    release) CONFIG="Release" ;;
    *)
        echo "ERROR: --config must be Debug or Release (got: $CONFIG)"
        exit 1
        ;;
esac

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$ROOT_DIR/src/VoiceWink/VoiceWink.csproj"
STAGE_DIR="$SCRIPT_DIR/stage/VoiceWink"
OUTPUT_DIR="$SCRIPT_DIR/Output"

is_windows_dotnet() {
    local info
    if ! info=$("$1" --info 2>/dev/null | tr -d '\r'); then
        return 1
    fi

    [[ "$info" == *"OS Platform: Windows"* ]] || [[ "$info" == *"RID: win-"* ]]
}

resolve_dotnet() {
    # Prefer the user-profile dotnet if it has the SDK pinned by global.json —
    # the global dotnet.exe only sees SDKs under its own root, so when a build
    # machine has (say) 8.0.420 globally and 8.0.419 at user-profile, only the
    # user-profile binary can resolve the pin. Mirrors scripts/check-vw-toolchain.ps1.
    local user_dotnet="$HOME/.dotnet/dotnet.exe"
    if [ -x "$user_dotnet" ] && [ -f "$ROOT_DIR/global.json" ]; then
        local expected_sdk
        expected_sdk="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$ROOT_DIR/global.json" | head -1)"
        if [ -n "$expected_sdk" ] && [ -d "$HOME/.dotnet/sdk/$expected_sdk" ]; then
            if is_windows_dotnet "$user_dotnet"; then
                DOTNET_CMD="$user_dotnet"
                DOTNET_MODE="native"
                echo "Using user-profile dotnet ($user_dotnet) — has pinned SDK $expected_sdk"
                return 0
            fi
        fi
    fi

    if command -v dotnet >/dev/null 2>&1; then
        if is_windows_dotnet dotnet; then
            DOTNET_CMD="dotnet"
            DOTNET_MODE="native"
            return 0
        fi

        echo "WARNING: ignoring non-Windows dotnet on PATH; falling back to Windows dotnet.exe" >&2
    fi

    if [ -x "/c/Program Files/dotnet/dotnet.exe" ] || [ -x "/mnt/c/Program Files/dotnet/dotnet.exe" ]; then
        DOTNET_CMD="C:\\Program Files\\dotnet\\dotnet.exe"
        DOTNET_MODE="powershell"
        return 0
    fi

    echo "ERROR: Windows dotnet not found on PATH and no fallback found at C:/Program Files/dotnet/dotnet.exe"
    exit 1
}

resolve_dotnet

# pwsh (PowerShell 7+) is required: the VC++ runtime gate and (on Release) the
# legal-asset gate run via pwsh, NOT the System32 Windows PowerShell 5.1 that Git
# Bash resolves (its mangled PSModulePath breaks cmdlet auto-load + ANSI-mis-parses
# UTF-8 scripts). Git Bash spawned from elsewhere may not have pwsh on PATH; probe
# the known install locations and prepend the first that has a callable pwsh.exe,
# mirroring installer/release-update.sh.
if ! command -v pwsh >/dev/null 2>&1; then
    for candidate_win in \
        "C:/Program Files/PowerShell/7" \
        "${LOCALAPPDATA:-}/Microsoft/WindowsApps" \
        "${LOCALAPPDATA:-}/Programs/PowerShell/7"; do
        candidate="$(cygpath -u "$candidate_win" 2>/dev/null)"
        if [ -n "$candidate" ] && [ -x "$candidate/pwsh.exe" ]; then
            PATH="$candidate:$PATH"
            echo "Discovered pwsh at $candidate (added to PATH for this run)."
            break
        fi
    done
fi
if ! command -v pwsh >/dev/null 2>&1; then
    echo "ERROR: pwsh (PowerShell 7+) not found on PATH. It is required for the VC++"
    echo "       runtime gate and the Release legal-asset gate. Install it:"
    echo "         winget install Microsoft.PowerShell"
    exit 1
fi

to_windows_path() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    elif command -v wslpath >/dev/null 2>&1; then
        wslpath -w "$1"
    else
        echo "ERROR: no path conversion tool available (expected cygpath or wslpath)"
        exit 1
    fi
}

resolve_nuget_home() {
    local local_home="$HOME/.nuget/packages"
    local local_msix_dir="$local_home/microsoft.windowsappsdk/$WINAPPSDK_VER/tools/Msix/win10-x64"
    if [ -d "$local_msix_dir" ]; then
        printf '%s\n' "$local_home"
        return 0
    fi

    if [ -d "$local_home" ] && [ ! -d "$local_msix_dir" ]; then
        echo "   WARNING: $local_home exists but does not contain Windows App SDK $WINAPPSDK_VER" >&2
    fi

    local windows_home=""
    windows_home=$(powershell.exe -NoProfile -Command '$env:USERPROFILE' 2>/dev/null | tr -d '\r')
    if [ -n "$windows_home" ] && command -v wslpath >/dev/null 2>&1; then
        local wsl_home
        wsl_home=$(wslpath -u "$windows_home")
        local windows_packages="$wsl_home/.nuget/packages"
        local windows_msix_dir="$windows_packages/microsoft.windowsappsdk/$WINAPPSDK_VER/tools/Msix/win10-x64"
        if [ -d "$windows_msix_dir" ]; then
            printf '%s\n' "$windows_packages"
            return 0
        fi

        if [ -d "$windows_packages" ]; then
            echo "   WARNING: $windows_packages exists but does not contain Windows App SDK $WINAPPSDK_VER" >&2
        fi
    fi

    if [ -d "$local_home" ]; then
        printf '%s\n' "$local_home"
        return 0
    fi

    printf '%s\n' "$local_home"
}

run_dotnet() {
    if [ "$DOTNET_MODE" = "powershell" ]; then
        local ps_cmd="& '$DOTNET_CMD'"
        local arg
        for arg in "$@"; do
            arg=${arg//\'/\'\'}
            ps_cmd="$ps_cmd '$arg'"
        done
        powershell.exe -NoProfile -Command "$ps_cmd"
    else
        "$DOTNET_CMD" "$@"
    fi
}

to_dotnet_path() {
    if [ "$DOTNET_MODE" = "powershell" ]; then
        to_windows_path "$1"
    else
        printf '%s\n' "$1"
    fi
}

# Same resolver as the release chain (scripts/lib/release-version.ps1, through its CLI wrapper) — a
# duplicate or commented-out <Version> here would name the portable ZIP after the wrong declaration
# while `dotnet publish` built another, shipping a mislabeled sideload package.
#
# NO PIPE in the capture, and THIS script is why the rule exists: it sets `-e` but NOT
# `-o pipefail`, so `if ! VAR=$(cmd | tr -d '\r')` reports `tr`'s zero status and sails past a failed
# resolve — measured, it produced a ZIP named after a partial version and still exited 0. The
# trailing CR pwsh emits is stripped with a builtin instead.
#
# The path is converted like every other pwsh call in this script: pwsh cannot resolve the `/c/...`
# form Git Bash may hand it.
if ! VERSION=$(pwsh -NoProfile -ExecutionPolicy Bypass \
        -File "$(to_windows_path "$ROOT_DIR/scripts/resolve-release-version.ps1")" \
        -Project "$(to_windows_path "$PROJECT")"); then
    exit 1
fi
VERSION="${VERSION%$'\r'}"
if [ -z "$VERSION" ]; then
    echo "ERROR: the version resolver exited 0 but printed nothing — refusing to continue." >&2
    exit 1
fi
echo "   Version: $VERSION (from VoiceWink.csproj)"

echo "=== VoiceWink Portable Installer Build ==="
echo ""

# Step 1: Publish (framework-dependent — uses user-level dotnet install, no admin)
echo "[1/3] Publishing framework-dependent $CONFIG build..."
rm -rf "$SCRIPT_DIR/publish-portable"
PROJECT_FOR_DOTNET=$(to_dotnet_path "$PROJECT")
PUBLISH_OUTPUT_FOR_DOTNET=$(to_dotnet_path "$SCRIPT_DIR/publish-portable")
run_dotnet publish "$PROJECT_FOR_DOTNET" \
    -c "$CONFIG" \
    -r win-x64 \
    --no-self-contained \
    -p:Platform=x64 \
    -p:PublishReadyToRun=true \
    -o "$PUBLISH_OUTPUT_FOR_DOTNET" \
    -v q

# Remove the .exe (won't work unsigned on enterprise machines)
rm -f "$SCRIPT_DIR/publish-portable/VoiceWink.exe"
rm -f "$SCRIPT_DIR/publish-portable/createdump.exe"

SIZE=$(du -sh "$SCRIPT_DIR/publish-portable" | cut -f1)
COUNT=$(find "$SCRIPT_DIR/publish-portable" -type f | wc -l)
echo "   Published: $SIZE ($COUNT files)"

# Step 2: Stage the zip layout
echo "[2/3] Staging package..."
rm -rf "$STAGE_DIR"
mkdir -p "$STAGE_DIR/app"

# App files
cp -r "$SCRIPT_DIR/publish-portable/"* "$STAGE_DIR/app/"

# Install scripts
cp "$SCRIPT_DIR/portable/VoiceWinkSetup.ps1" "$STAGE_DIR/"
cp "$SCRIPT_DIR/portable/Install.bat" "$STAGE_DIR/"

# GPL v3 §6 source-availability — bundle LICENSE + README so the EULA's claim
# that "the LICENSE file is included next to this binary" actually holds.
# Fail loud if either is missing — the EULA contradicts the ZIP otherwise.
for required_root_file in LICENSE README.md; do
    if [ ! -f "$ROOT_DIR/$required_root_file" ]; then
        echo "ERROR: $required_root_file missing from repo root ($ROOT_DIR/$required_root_file)"
        echo "       The EULA claims the GPL v3 LICENSE is bundled with the binary; refusing to produce a ZIP that contradicts that claim."
        exit 1
    fi
    cp "$ROOT_DIR/$required_root_file" "$STAGE_DIR/"
done

# Windows App SDK MSIX packages (needed on machines without the runtime)
# Version must match PackageReference in src/VoiceWink/VoiceWink.csproj
WINAPPSDK_VER=$(sed -n 's/.*Microsoft.WindowsAppSDK.*Version="\([^"]*\)".*/\1/p' "$PROJECT")
if [ -z "$WINAPPSDK_VER" ]; then
    WINAPPSDK_VER="1.6.250602001"
    echo "   WARNING: Could not read WindowsAppSDK version from csproj, using fallback $WINAPPSDK_VER"
fi
NUGET_HOME=$(resolve_nuget_home)
MSIX_DIR="$NUGET_HOME/microsoft.windowsappsdk/$WINAPPSDK_VER/tools/Msix/win10-x64"
if [ -d "$MSIX_DIR" ]; then
    mkdir -p "$STAGE_DIR/runtime"
    cp "$MSIX_DIR"/*.msix "$STAGE_DIR/runtime/"
    echo "   Bundled Windows App SDK MSIX packages"
else
    echo "   WARNING: Windows App SDK MSIX packages not found at $MSIX_DIR"
fi

# App-local VC++ runtime gate — prove the four MSVC DLLs (vcruntime140 /
# vcruntime140_1 / msvcp140 / vcomp140) shipped beside whisper.dll in the staged
# app/. They flow in via the csproj <Content><Link> just like the Velopack path,
# but the portable build can't run the full check-publish-payload gate (no
# self-contained payload, no WindowsAppRuntimeInstall-x64.exe), so we run the
# shared, fail-closed VC++-only check here against app/runtimes/win-x64/. Runs
# for both Debug and Release — the DLLs are committed, so a failure means a real
# regression (broken csproj copy / deleted DLLs), not still-DRAFT assets. Without
# them, a clean-VM portable install fails native Whisper load with PInvokeError 0.
# Invoke via pwsh (PowerShell 7+), NOT powershell.exe: Git Bash resolves the
# System32 Windows PowerShell 5.1 with a mangled PSModulePath where module
# auto-loading is broken (Get-FileHash / Get-AuthenticodeSignature unavailable),
# which would false-fail this gate. pwsh has correct module handling and is the
# same host check-publish-payload.ps1 + the release scripts run under. Paths are
# converted to Windows form so the script's -LiteralPath calls resolve.
echo "   Verifying app-local runtime payload (VC++ DLLs + parakeet-server) in staged app/ ..."
if ! pwsh -NoProfile -ExecutionPolicy Bypass \
    -File "$(to_windows_path "$ROOT_DIR/scripts/check-vcredist-payload.ps1")" \
    -Dir "$(to_windows_path "$STAGE_DIR/app")"; then
    echo ""
    echo "ERROR: app-local runtime payload check FAILED — see output above for WHICH file."
    echo "       Either the staged app/ is missing the MSVC DLLs beside whisper.dll (native"
    echo "       Whisper crashes on a clean VM; committed at installer/runtime/vcredist/ —"
    echo "       if absent, run scripts/fetch-vcredist.ps1), or parakeet-server.exe is"
    echo "       absent/mismatched (every install silently falls back to the old engine;"
    echo "       committed at installer/runtime/parakeet/). Refusing to produce the ZIP."
    exit 1
fi

mkdir -p "$OUTPUT_DIR"

# Step 3: Create zip (use .NET ZipFile for Windows-compatible zip)
echo "[3/3] Creating zip..."
ZIPFILE="$OUTPUT_DIR/VoiceWink-${VERSION}-Portable.zip"
rm -f "$ZIPFILE"
# Convert local paths to Windows paths for PowerShell/.NET
WIN_SRC=$(to_windows_path "$SCRIPT_DIR/stage/VoiceWink")
WIN_ZIP=$(to_windows_path "$ZIPFILE")
powershell.exe -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::CreateFromDirectory('$WIN_SRC', '$WIN_ZIP')"

# Cleanup
rm -rf "$SCRIPT_DIR/stage" "$SCRIPT_DIR/publish-portable"

# Release-asset gate: only enforce on Release config so Debug iteration isn't
# blocked by still-DRAFT legal assets. The Release path validates legal
# placeholders, bin-output LICENSE bundling, privacy custom-endpoint
# disclosure, and the freshly-created ZIP's GPL §6 source-release artefacts.
if [ "$CONFIG" = "Release" ]; then
    echo ""
    echo "=== Release-asset validation gate ==="
    WIN_REPO_ROOT=$(to_windows_path "$ROOT_DIR")
    # Invoke via pwsh, NOT powershell.exe. Pre-existing fragility (unrelated to
    # the VC++ work): Git Bash resolves the System32 Windows PowerShell 5.1, which
    # both mangles PSModulePath (breaking cmdlet auto-load) AND reads this UTF-8
    # script as ANSI, mis-parsing a non-ASCII char at check-release-assets.ps1:66
    # ("Missing ')' in method call"). pwsh parses + runs it correctly — and the
    # Velopack path (release-update.*) already invokes this same gate via pwsh.
    if ! pwsh -NoProfile -ExecutionPolicy Bypass \
        -File "$(to_windows_path "$ROOT_DIR/scripts/check-release-assets.ps1")" \
        -Configuration Release \
        -RepoRoot "$WIN_REPO_ROOT" \
        -PortableZip "$(to_windows_path "$ZIPFILE")"; then
        echo ""
        echo "ERROR: Release-asset validation FAILED — see output above."
        echo "       The portable ZIP at $ZIPFILE was produced but should NOT ship as-is."
        exit 1
    fi
fi

echo ""
echo "=== Done ==="
ls -lh "$ZIPFILE"
echo ""
echo "To install: extract zip, then run:"
echo "  powershell -ExecutionPolicy Bypass -File VoiceWink\\VoiceWinkSetup.ps1"
