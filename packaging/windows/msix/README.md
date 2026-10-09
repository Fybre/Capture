# Microsoft Store (MSIX) package

The `Package Capture` workflow builds an unsigned `Capture-<version>.0-x64.msix` alongside the
Inno Setup installer and uploads it as the `Capture-windows-store` artifact. It is not attached to
the GitHub release, because an unsigned MSIX can't be installed directly. Microsoft signs it when
the Store certifies it.

## One-time setup

1. In Partner Center, reserve the app name, then open **Product management ▸ Product identity**.
2. In the GitHub repo (**Settings ▸ Secrets and variables ▸ Actions ▸ Variables**), set these to the
   values shown there, copied exactly:
   - `MSIX_IDENTITY_NAME`: *Package/Identity/Name*
   - `MSIX_PUBLISHER`: *Package/Identity/Publisher* (starts with `CN=`)
   - `MSIX_PUBLISHER_DISPLAY_NAME`: *Package/Properties/PublisherDisplayName*

   Until these are set, the package builds with placeholder values, which the Store will reject.

## Each release

1. Tag the release as usual (`vX.Y.Z`).
2. When the workflow finishes, download the `Capture-windows-store` artifact from the run.
3. In Partner Center, create a submission and upload the `.msix` under **Packages**.

The Store requires the fourth version part to be `0`. The script derives `X.Y.Z.0` from the tag, so
every Store upload needs a new tag. Partner Center rejects a package version it has already seen.

## Notes for the submission

- **runFullTrust**: Capture is a standard desktop app. It needs full trust to run its bundled OCR
  (Tesseract) and PII-detection (Presidio) helpers, access scanners, and read and write the folders
  the user chooses for import, watch and export.
- **Updates**: the Store build hides Capture's own GitHub update check (Settings, first-run wizard,
  About) and leaves updates to the Store.
- **Data location**: the Store build keeps its data in
  `%LocalAppData%\Packages\<package family name>\LocalState\CaptureV2`, not in
  `%LocalAppData%\CaptureV2`. Windows removes it when the app is uninstalled. To move from the
  installer version, export settings and capture profiles there and import them in the Store version.

## Testing a package locally

Windows only installs signed packages. To try one before submitting:

```powershell
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=3119EB48-FC16-416D-9AD5-46D05E7A2B06" -KeyUsage DigitalSignature `
  -FriendlyName "Capture MSIX test" -CertStoreLocation Cert:\CurrentUser\My `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
Export-PfxCertificate -Cert $cert -FilePath capture-test.pfx -Password (Read-Host -AsSecureString)
# Trust it (admin PowerShell): import capture-test.pfx into Local Machine > Trusted People.
signtool sign /fd SHA256 /f capture-test.pfx /p <password> Capture-0.8.13.0-x64.msix
```

The certificate subject must match the package's `Publisher`, which is the `MSIX_PUBLISHER` repository
variable. After that, double-click the `.msix` to install it.

To regenerate the tile and logo images after changing the app icon, run
`python3 packaging/windows/msix/generate-assets.py` (needs Pillow) and commit the results.
