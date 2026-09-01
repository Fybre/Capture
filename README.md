# Capture

.NET 8 / Avalonia desktop app for document capture: import, batch/document separation, OCR,
zonal/pattern/barcode/AI-based indexing, post-indexing redaction, and export to CSV or a
Therefore Online repository.

**Download the latest release:**
[macOS (.dmg)](https://github.com/Fybre/Capture/releases/latest/download/Capture.dmg) ·
[Windows (.exe installer)](https://github.com/Fybre/Capture/releases/latest/download/CaptureSetup.exe)

## Features

- **Import & batching** — watch folders or manual import, with barcode/blank-page/page-count
  document separation and configurable batch profiles.
- **Indexing** — zonal, key/value, regex, barcode, lookup, and AI-extracted fields, plus manual
  text entry. AI extraction runs against either a cloud OpenAI-compatible endpoint or a local,
  fully offline model (see below) — selectable per install.
- **Scanning** — direct scan-to-import on macOS (via a bundled native helper,
  `native/CaptureScanHelperMac`) and Windows (WIA).
- **Redaction** — automatic PII detection via a bundled Presidio sidecar, fields explicitly marked
  *Sensitive*, or manually-drawn regions; PII detection can be turned off per profile to redact only
  Sensitive fields with no NLP involved.
- **Export** — CSV, and direct document creation in a Therefore Online repository (configure the
  connection once in Settings; category/field mapping is per export definition).
- **Debug logging** — an opt-in activity log (imports, exports, watch-folder activity, errors) for
  troubleshooting, toggled in Settings.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- **macOS only: Xcode Command Line Tools** (`xcode-select --install`) — `Capture.App.csproj` builds
  the `CaptureScanHelperMac` native scan helper (a small Swift binary) via `swiftc` on every build,
  automatically, on macOS. No action needed if the Command Line Tools are already installed; skipped
  entirely on other platforms.

Everything else (PDF rendering, SQLite, barcode decoding, image processing, local AI inference via
LLamaSharp, and — once set up below — Tesseract and Presidio) is bundled via NuGet with no separate
install.

## Build & run

```bash
dotnet build Capture.sln
dotnet run --project src/Capture.App
```

To build for a specific architecture (e.g. testing x64 under emulation on Windows-on-ARM), pass
`-a`/`--arch` to the individual project, not the solution — `dotnet build Capture.sln -a x64` fails
with `NETSDK1134` (a solution build can't take a `RuntimeIdentifier`):

```bash
dotnet build src/Capture.App/Capture.App.csproj -a x64
```

## Tests

```bash
dotnet test tests/Capture.Tests/Capture.Tests.csproj
```

## Local, offline AI extraction

AI field extraction can run entirely on-device instead of calling a cloud endpoint — no document
text leaves the machine. This is opt-in per install: switch **Settings → AI extraction → Provider**
to *Local*, then click **Download model** (a one-time ~2GB download, cached under the app's data
directory). It runs on CPU via [LLamaSharp](https://github.com/SciSharp/LLamaSharp) (llama.cpp) with
grammar-constrained decoding, so no GPU or extra setup is required, but it is slower and less
accurate than the cloud provider — reasonable for privacy-sensitive or offline use, not a drop-in
replacement for it. See `src/Capture.LocalAi` for the implementation.

## Native binary packages (Tesseract, Presidio)

Both Tesseract and Presidio ship as native NuGet packages (`Capture.Tesseract.Binaries`,
`Capture.Presidio.Binaries`), built from source per-platform by their own repo's CI, rather than
published to nuget.org. `nuget.config` at the repo root configures two ways to get them, either is
fine, and both can be used together:

- **`CaptureLocalFeed` → `local-nuget-feed/`** — download a `.nupkg` by hand (a workflow artifact, see
  each package's section below) and drop it in this gitignored folder (tracked as an empty directory
  via `.gitkeep`). No auth needed. `dotnet restore` picks it up automatically once the version there
  matches the `PackageReference` in `src/Capture.App/Capture.App.csproj`.
- **`FybreGitHubPackages` → GitHub Packages** — each repo's CI also pushes every built package to
  `https://nuget.pkg.github.com/Fybre/index.json`, so it's permanently available there without
  re-downloading a workflow artifact (whose retention is time-limited). GitHub Packages requires an
  authenticated pull even for a public repo: set `GITHUB_PACKAGES_USERNAME` (your GitHub username) and
  `GITHUB_PACKAGES_TOKEN` (a [PAT](https://github.com/settings/tokens) with `read:packages`) as
  environment variables before restoring.

Both `PackageReference`s are conditional — present only when a matching local `.nupkg` exists **or**
`GITHUB_PACKAGES_TOKEN` is set — so a clean, uncredentialed clone with neither configured still builds;
it just runs without that package (see each section's degraded-mode behavior below).

## OCR: bundled Tesseract

OCR of scanned pages with no embedded PDF text uses a bundled Tesseract executable; there is nothing
for the end user to install on `win-x64` or `osx-arm64`. See
[`Fybre/tesseract-app`](https://github.com/Fybre/tesseract-app) for the native source build. The
package contains English language data from `tessdata_fast` only.

For local development, either configure GitHub Packages restore (see above — the low-effort, no
manual-download option) or download a `.nupkg` by hand:

1. Trigger **Build Tesseract package** in `Fybre/tesseract-app`, or download an existing artifact with
   `gh run download <run-id> --repo Fybre/tesseract-app -n Capture.Tesseract.Binaries`.
2. Put the `.nupkg` in `local-nuget-feed/`.
3. Match its version to the `Capture.Tesseract.Binaries` reference in
   `src/Capture.App/Capture.App.csproj`, then run `dotnet restore`.

Resolution
uses `CAPTURE_TESSERACT` first, then the bundled executable, then `PATH`, then the existing well-known
install paths. A sibling `tessdata/` directory is supplied to the child process automatically. If no
Tesseract exists anywhere, OCR reports a clear `InvalidOperationException`; other app features remain
available. Linux continues to use a system installation. Windows-on-ARM uses the `win-x64` app and
Tesseract package under emulation; there is no separate `win-arm64` bundle.

To verify the package, build or publish for a supported RID and check that the output contains
`tesseract` (`tesseract.exe` on Windows) and `tessdata/eng.traineddata`:

```bash
dotnet publish src/Capture.App/Capture.App.csproj --runtime osx-arm64 --self-contained false
# Windows: use --runtime win-x64
```

For an end-to-end check, unset `CAPTURE_TESSERACT`, temporarily move any system Tesseract aside, then
import a scanned PDF or image and confirm that OCR/indexing succeeds.

## Redaction: setting up the Presidio sidecar

PII detection for redaction runs against a self-contained, bundled Presidio executable — the app
launches it itself as a local child process; there's nothing for the *end user* to install. See
[`Fybre/presidio-app`](https://github.com/Fybre/presidio-app) for how that executable is built.

That package (`Capture.Presidio.Binaries`) isn't published to nuget.org, so for local development
either configure GitHub Packages restore (see "Native binary packages" above) or download a `.nupkg`
by hand:

1. Build or download the package. Either:
   - Trigger the `Build sidecar package` workflow in `Fybre/presidio-app` (GitHub Actions → Run
     workflow) and download the resulting `Capture.Presidio.Binaries` artifact, or
   - Grab it from an existing successful run: `gh run download <run-id> --repo Fybre/presidio-app -n Capture.Presidio.Binaries`
2. Place the `.nupkg` in `local-nuget-feed/` at the repo root (already tracked as an empty directory
   via `.gitkeep` — the `.nupkg` itself is git-ignored, since the package is a few hundred MB of
   frozen Python/spaCy binaries per platform).
3. Make sure the version in `src/Capture.App/Capture.App.csproj`'s
   `<PackageReference Include="Capture.Presidio.Binaries" .../>` matches the `.nupkg`'s version
   (rename the file or edit the csproj if they differ), then `dotnet restore`.

Without a matching `.nupkg` present and no `GITHUB_PACKAGES_TOKEN` configured, `Capture.App.csproj`
skips the `PackageReference` entirely, so a clean clone builds and runs fine — just without the
sidecar. No `win-arm64` build of the package exists (only `linux-x64`, `osx-arm64`, `osx-x64`,
`win-x64`), so on Windows-on-ARM you need an x64 build (see above) for the sidecar to be available at
all.

**Redaction is a no-op without this** — `PresidioSidecarLauncher.IsAvailable` simply returns false if
the executable isn't present, so the app builds and runs fine either way. Fields marked *Sensitive*
still get redacted regardless, since that path doesn't need Presidio at all — only the automatic PII
*detection* is affected. A profile's Redaction settings can also disable PII detection explicitly
(**Detect PII automatically** checkbox) to redact only Sensitive fields on purpose, independent of
whether the sidecar is even installed.

### Verifying the sidecar actually works

`Capture.Presidio.Binaries` ships RID-specific native assets, which NuGet only resolves and copies to
the output directory for a build that has a concrete `RuntimeIdentifier` — a portable build (the
default for a plain `dotnet build`/`dotnet run` with no runtime specified) silently gets none of it,
so the sidecar looks "unavailable" with no error at all. `Capture.App.csproj` defaults
`RuntimeIdentifier` to the current SDK's own RID (`$(NETCoreSdkRuntimeIdentifier)`) specifically so a
plain `dotnet build`/`dotnet run`/IDE debug session gets it automatically — no flag to remember. Pass
an explicit `--runtime` only if you're deliberately cross-publishing for a different platform:

```bash
dotnet publish src/Capture.App/Capture.App.csproj --runtime osx-arm64   # or win-x64 / linux-x64 / osx-x64
```

The output directory (`src/Capture.App/bin/<Configuration>/net8.0/<rid>/`) should contain
`presidio-sidecar` (`.exe` on Windows) plus a sibling `presidio-sidecar-data/` folder — both are
required at runtime. **The very first launch of a freshly-installed copy of the binary can take a
genuinely long time** (tens of seconds) before it even prints its `READY` line — macOS/Windows
verifying several hundred bundled `.dylib`/`.dll` files for the first time, well before Python/spaCy
have done anything. `PresidioSidecarLauncher`'s timeouts are sized for this cold-start case; later
launches are fast once the OS has cached those checks. You can sanity-check the binary directly,
independent of the app (expect the first invocation to sit quietly for a while before `READY` appears):

```bash
./presidio-sidecar --port 0
# wait for "READY <port>" on stdout, then in another terminal:
curl http://127.0.0.1:<port>/health
curl -X POST http://127.0.0.1:<port>/analyze \
  -H "Content-Type: application/json" \
  -d '{"text": "Contact Jane Doe at jane.doe@example.com.", "language": "en"}'
```

## Packaging & distribution

`.github/workflows/package.yml` builds an end-user-installable package for macOS (a signed, notarized
`.dmg`) and Windows (an unsigned `CaptureSetup-<version>.exe` installer — no code-signing certificate
yet; Windows will show a one-time SmartScreen "unknown publisher" prompt until one exists). Trigger it
via **Actions → Package Capture → Run workflow** (optionally overriding the version), or push a
`vX.Y.Z` tag to also create a GitHub Release with both artifacts attached.

### macOS: `packaging/macos/build-dmg.sh`

Publishes a self-contained, single-file `osx-arm64` build, assembles a real `Capture.app` (icon from
`Assets/Brand/Capture.icns`, `CaptureScanHelperMac.app` rebuilt into it since its own MSBuild target
only hooks `Build`, not `Publish`), code-signs every nested binary individually (`--deep` chokes on
Presidio's PyInstaller payload — see the script's own comments), and packages it into a `.dmg`.
Notarizes and staples automatically if Apple credentials are present; otherwise produces an ad-hoc
signed build good enough for a local sanity check but not for handing to anyone else (Gatekeeper will
still block it).

Local run: `./packaging/macos/build-dmg.sh <version>`. A few non-obvious things this script works
around, in case you're debugging it — all confirmed by reproducing each independently:
- **Build outside any iCloud-Drive-synced folder.** If your checkout lives under `~/Documents` (or
  anywhere else iCloud syncs), the file-provider daemon asynchronously touches freshly-written files
  and intermittently breaks `codesign` with `resource fork, Finder information, or similar detritus not
  allowed`. The script always builds in a fresh `mktemp -d` under `/tmp` regardless of where the repo
  lives, so this shouldn't bite you, but it's the reason that choice is there.
- **`-p:PublishSingleFile=true` and `-p:DebugType=none`** aren't just size optimizations — codesign
  refuses to seal a bundle containing loose PE-format `.dll`/`.pdb` files directly in `Contents/MacOS`
  ("code object is not signed at all"); collapsing managed assemblies into the one Mach-O apphost and
  dropping debug symbols removes that class of file entirely.
- **`presidio-sidecar-data/` and `tessdata/` move to `Contents/Resources/`**, with a symlink left at
  their original `Contents/MacOS/` path. `Contents/MacOS` must contain only genuinely signable code —
  Presidio's ~300MB PyInstaller data tree trips the same "must be signed" check even for plain
  non-code files (Python package metadata, stray source files bundled as package data). `Resources` is
  exempt. The symlink means `PresidioSidecarLauncher`/`TesseractCliOcrEngine`'s existing
  `Path.Combine(AppContext.BaseDirectory, ...)` lookups keep working unmodified.
- **`packaging/macos/entitlements.plist`** grants `allow-jit`/`allow-unsigned-executable-memory` —
  without them, hardened runtime (required for notarization) blocks CoreCLR's JIT outright and the app
  fails to start with `Failed to create CoreCLR, HRESULT: 0x80070008`.

Apple secrets the CI workflow needs (none of these can be generated on your behalf — they require your
own Apple ID/Developer account):
- `APPLE_CERT_P12_BASE64` + `APPLE_CERT_PASSWORD` — a **Developer ID Application** certificate exported
  from Keychain Access as a `.p12` (`base64 -i cert.p12 | pbcopy`).
- `APPLE_TEAM_ID` — from the Apple Developer portal's Membership page.
- `APPLE_API_KEY_ID`, `APPLE_API_ISSUER_ID`, `APPLE_API_KEY_P8_BASE64` — an **App Store Connect API
  key** (Users and Access → Integrations → Keys, "Developer" role) for `notarytool`, chosen over an
  Apple ID + app-specific password since it doesn't hit 2FA prompts in CI.

### Windows: `packaging/windows/installer.iss`

An [Inno Setup](https://jrsoftware.org/isinfo.php) script — CI installs it via Chocolatey
(`choco install innosetup`) and compiles against a self-contained, single-RID `win-x64` publish:

```powershell
dotnet publish src/Capture.App/Capture.App.csproj --runtime win-x64 --self-contained true --configuration Release -p:Version=1.2.3 --output publish
& "C:\Program Files (x86)\Inno Setup 6\iscc.exe" /DVersion=1.2.3 /DPublishDir=publish packaging\windows\installer.iss
```

Produces `packaging/windows/out/CaptureSetup-<version>.exe`. No signing step yet — add a `SignTool=`
line to `installer.iss`'s `[Setup]` section once a certificate exists; nothing else about the pipeline
needs to change.

### CI secrets

Both jobs restore `Capture.Tesseract.Binaries`/`Capture.Presidio.Binaries` from GitHub Packages, which
needs `CAPTURE_PACKAGES_PAT` (repo secret) — the same `read:packages`-scoped PAT already used for local
development (see "Native binary packages" above). The macOS job additionally needs the five Apple
secrets listed above; without them it still builds and produces an ad-hoc signed `.dmg` (useful for
verifying the pipeline itself still works, not for distribution).

## License

Apache License, Version 2.0 — see [`LICENSE`](LICENSE). Every bundled third-party component (Avalonia,
Tesseract/Leptonica, Presidio/spaCy, LLamaSharp, and the rest) is under a compatible permissive license
(MIT/BSD/Apache/zlib/public domain); the in-app **About Capture** dialog lists each one with a link to
its actual license text.
