# Capture

.NET 8 / Avalonia desktop app for document capture: import, batch/document separation, OCR,
zonal/pattern/barcode/AI-based indexing, post-indexing redaction, and export to CSV or a
Therefore Online repository.

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
- **Tesseract OCR** — required for OCR of scanned pages with no embedded PDF text.
  `TesseractCliOcrEngine` resolves a `tesseract` binary from `CAPTURE_TESSERACT`, then `PATH`, then a
  few well-known install locations. This is currently the one dependency **not** bundled with the
  app — install it separately:
  - macOS: `brew install tesseract`
  - Windows: [UB-Mannheim's installer](https://github.com/UB-Mannheim/tesseract/wiki)
  - Linux: `apt install tesseract-ocr` (or your distro's equivalent)
- **macOS only: Xcode Command Line Tools** (`xcode-select --install`) — `Capture.App.csproj` builds
  the `CaptureScanHelperMac` native scan helper (a small Swift binary) via `swiftc` on every build,
  automatically, on macOS. No action needed if the Command Line Tools are already installed; skipped
  entirely on other platforms.

Everything else (PDF rendering, SQLite, barcode decoding, image processing, local AI inference via
LLamaSharp, and — once set up below — Presidio) is bundled via NuGet with no separate install.

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

## Redaction: setting up the Presidio sidecar

PII detection for redaction runs against a self-contained, bundled Presidio executable — the app
launches it itself as a local child process; there's nothing for the *end user* to install. See
[`Fybre/presidio-app`](https://github.com/Fybre/presidio-app) for how that executable is built.

That package (`Capture.Presidio.Binaries`) isn't published to a real feed yet, so for local
development it's consumed from a local folder:

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

Without a matching `.nupkg` present, `Capture.App.csproj` skips the `PackageReference` entirely
(checked via an MSBuild file-existence condition), so a clean clone builds and runs fine — just
without the sidecar. No `win-arm64` build of the package exists (only `linux-x64`, `osx-arm64`,
`osx-x64`, `win-x64`), so on Windows-on-ARM you need an x64 build (see above) for the sidecar to be
available at all.

`nuget.config` at the repo root already points a `CapturePresidioLocal` source at `local-nuget-feed/`
alongside `nuget.org` — no further configuration needed once the file is in place.

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
