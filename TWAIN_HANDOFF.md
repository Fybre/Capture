# TWAIN scanning — handoff for testing on Windows

Branch `twain-scanning`. Written on macOS, where TWAIN can't run: everything below builds and the
unit tests pass, but **none of the TWAIN code has run against a real driver yet**. This file is for
the Claude session in the Windows VM that tests it; delete it before merging.

## Why

On Windows, Capture only used WIA. Production document scanners — Canon imageFORMULA (the user's
Canon R50, a network feeder scanner), Ricoh/Fujitsu fi, Kodak Alaris, Epson DS — are built around
TWAIN and often have no WIA driver or only a basic one. The user's R50 shows in IRIS PowerScan
(TWAIN) but not in Capture.

## What was added

- `src/Capture.Scanner/TwainScanSource.cs` — `IScanSource` over TWAIN using **NTwain 4.0.0-beta.4**
  (MIT). v4 is the only NTwain targeting plain `net8.0` (v3 needs `net6.0-windows`, and
  Capture.Scanner is shared with the macOS build). NTwain runs TWAIN's message pump and the transfer
  loop on threads of its own and marshals every triplet to the pump thread, so the source can be
  called from any thread.
  - **Native transfers** (`ICAP_XFERMECH = NATIVE`): a DIB per page, which NTwain turns into a BMP
    stream; it's decoded with SkiaSharp and saved as a PNG in %TEMP%. NTwain 4's memory transfer
    is unfinished (a TODO in its source), so don't switch to it.
  - Settings applied before enabling: feeder on/off (must succeed, or the scan fails with "no
    feeder/flatbed"), `CAP_AUTOFEED`, duplex (must succeed if asked for), pixel type RGB/GRAY, 8-bit,
    X/Y resolution, `CAP_XFERCOUNT = -1`. Failures of the others are ignored and the driver's value
    is used.
  - Pages stream out as they arrive (Channel → `IAsyncEnumerable`), like the macOS helper. A timeout
    resets on each page (max(120 s, dpi × 0.5 s); 30 min for the first page when the driver window
    is shown). Cancelling disables the source if nothing is transferring, otherwise asks the next
    `TransferReady` to stop the feeder gracefully (`CancelType.Graceful`); pages already scanned are
    kept by the app.
  - Device ids are `twain:` + the driver's product name. `ListDevicesAsync` opens each source
    briefly to read its capabilities; one that won't open is listed with defaults.
  - Available only when `twaindsm.dll` (the 64-bit TWAIN DSM, installed by scanner drivers, not
    Windows) is in System32 or next to the app.
- `src/Capture.Scanner/CompositeScanSource.cs` — lists WIA and TWAIN devices together, labelled
  "(WIA)" / "(TWAIN)", and routes each scan to the source that listed the device. Registered on
  Windows in `src/Capture.App/Hosting/ServiceConfiguration.cs`.
- `ScanOptions.ShowDriverSettings` and Settings ▸ Scanning "Show the scanner's own settings before
  each scan" (only shown for TWAIN devices) — enables the source with the driver's UI.
- `tools/ScanProbe` — console tool to list, scan and dump TWAIN capabilities (see its README).
- Tests: `tests/Capture.Tests/CompositeScanSourceTests.cs` (routing, labels, a failing source).

## What to test on Windows (in order)

Prerequisites: the R50's Canon TWAIN driver installed and working (IRIS PowerScan can scan), VM
networking that reaches the scanner, .NET 8 SDK.

1. `dotnet run --project tools/ScanProbe -- list`
   - TWAIN available: True; the R50 listed as `Canon R50 (TWAIN)` (exact product name may differ)
     with feeder=True, duplex=True, flatbed=False and sensible resolutions.
   - Any WIA scanners still listed. Note how long listing takes — it opens every TWAIN source.
2. `... -- twain-caps "<product name>"` — save the output; check `CAP_FEEDERENABLED`,
   `CAP_DUPLEXENABLED`, `ICAP_PIXELTYPE`, `ICAP_XRESOLUTION`, `ICAP_XFERMECH` behave as assumed.
3. `... -- scan "twain:<name>" --feeder --dpi 300 --gray` with 3–5 sheets: every page arrives,
   correct size/dpi, readable images, no driver window (a progress indicator is OK).
4. Same with `--duplex` (twice the pages, back sides present) and colour.
5. `--stop-after 2` with ~10 sheets: stops cleanly, 2+ pages kept, the scanner isn't left busy (a
   second scan works straight after).
6. Empty feeder: a clear error, not a hang until the timeout.
7. Paper jam / open the cover mid-stack if practical: pages before it are kept, a clear message.
8. `--ui`: the Canon driver window opens; scanning from it works.
9. The app (`dotnet run --project src/Capture.App`): Settings ▸ Scanning lists the R50 (TWAIN),
   choose Feeder; Scan a stack → page count in the status bar, one document (or split by the
   profile), "Continue scanning?" prompt. Then a WIA flatbed if one is available, to check WIA
   still works.

## Likely problem areas

- **Product name matching**: ids use `ProductName.Trim()`; if a driver lists two sources with the
  same name, or the name changes between sessions, opening fails.
- **Capability negotiation order**: some drivers want pixel type before bit depth, or resolution
  before pixel type, or reject `CAP_XFERCOUNT = -1`. `twain-caps` output shows what's allowed.
- **`SKBitmap.Decode` on the BMP**: 1-bit/8-bit palettes and top-down DIBs should decode; if a
  driver sends a native TIFF instead (rare), decoding fails with "can't decode" — it would need
  handling.
- **No `SourceDisabled` / hang**: if a driver never ends the transfer loop, the scan only ends on the
  page timeout. NTwain calls `DisableSource` after its transfer loop, which raises `SourceDisabled`.
- **Listing cost**: opening every TWAIN source to read capabilities may be slow or show driver
  dialogs for offline network scanners. If so, consider listing without opening and reading
  capabilities only for the selected device.
- **Threading**: NTwain's events arrive on its transfer thread. `TwainAppSession` is created once
  and kept for the app's lifetime.

## Before merging

- Update `HelpWindow.axaml` (Scanning section: TWAIN scanners, the driver-settings option) and add a
  `VersionHistory.json` entry for the release that includes this.
- Delete this file.
