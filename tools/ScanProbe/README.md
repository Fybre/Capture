# ScanProbe

Tries Capture's Windows scanner sources (WIA and TWAIN) outside the app and prints what each step
does — for testing a scanner driver without clicking through the UI. Windows only.

```
dotnet run --project tools/ScanProbe -- list
dotnet run --project tools/ScanProbe -- scan "twain:CANON R50" --feeder --duplex --dpi 300
dotnet run --project tools/ScanProbe -- twain-caps "CANON R50"
```

- `list` — whether WIA and TWAIN are available, then every scanner with its id and what it reports it
  can do (flatbed, feeder, duplex, colour, greyscale, resolutions). Use the id with `scan`.
- `scan <id>` — scans with the same code the app uses, printing each page as it arrives (size, dpi,
  file size, time) and saving the pages to `--out` (default `scanprobe-<time>` in the current folder).
  Options: `--dpi N` (300), `--feeder` (default flatbed), `--duplex`, `--gray`, `--ui` (show the
  driver's own settings window first), `--stop-after N` (cancel after N pages, to test stopping).
  Ctrl+C stops the scan; the pages already scanned are kept.
- `twain-caps "<product name>"` — opens a TWAIN driver directly and prints every capability it
  supports, with its current and allowed values. Use the product name from `list`, without the
  `twain:` prefix. This is what to look at when a setting doesn't take.
