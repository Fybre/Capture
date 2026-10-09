# Capture Privacy Policy

**Effective date:** 9 October 2026

This policy explains how Capture, a desktop application for capturing, indexing, reviewing, redacting
and exporting documents published by Fybre, handles your information. It applies to every edition of
Capture, including the Microsoft Store version and the installers published on GitHub.

## Summary

- Capture runs on your computer. Fybre does not operate a server for Capture, and Capture has no
  Fybre account.
- Capture does not include analytics, telemetry, advertising or tracking, and Fybre does not collect,
  receive, sell or share your documents or any information about how you use the app.
- Your documents leave your computer only when you send them somewhere: an AI service, a document
  management system, a web service, or a folder that you configure.

## Information stored on your computer

To do its job, Capture stores the following in its data folder on your computer:

- documents you scan or import, and the page images made from them
- text recognised from those pages (OCR), extracted index values, redaction results and batch information
- your settings, capture profiles, redaction sets and, if you enable it, a troubleshooting log

This information stays on your computer. Credentials you enter in Settings (the AI service API key and
the Therefore password or token) are protected using your operating system's secure storage. These are
Windows Data Protection, the macOS Keychain, or the Linux secret service. A bearer token entered for a
REST export is saved in that capture profile's file on your computer. You can remove documents in the
app, and you can delete Capture's data folder at any time. In the Microsoft Store version, Windows
removes the data folder when you uninstall the app.

OCR (Tesseract) and the detection of personal information for redaction (Microsoft Presidio) run
locally on your computer. Presidio runs as a helper process that only accepts connections from your
own computer.

## When information leaves your computer

Capture sends information over the network only for the features below, and only to the destination
you configure or request:

- **AI field extraction (optional).** If you configure a cloud AI service in Settings, such as OpenAI
  or another OpenAI-compatible endpoint, Capture sends that service the recognised text of a document
  (up to a configurable length) and the names of the fields to extract, using the API key you
  provide. That service's own terms and privacy policy apply to what it receives. If you choose the
  local AI option instead, extraction runs on your computer. The model is downloaded once from Hugging
  Face when you ask for it, and no document content is sent. AI extraction can be turned off.
- **Exports you set up.** Capture sends documents and their index values to the destinations you
  configure: a Therefore™ server, a REST web service, or files and folders you choose. Those systems
  are operated by you or your organisation, and their own policies apply.
- **Scripts you write or install.** Capture profiles can contain scripts, which are off unless you
  allow them in Settings. A script can send information wherever its author made it send it. Only
  allow scripts you trust.
- **Update check (GitHub installers only).** If enabled, Capture asks GitHub for the latest release
  number when it starts. Only that request is made. No document or usage data is sent, though GitHub
  receives the standard information in any web request, such as your IP address. The Microsoft Store
  version does not do this, because the Store handles updates.

## Children

Capture is a general-purpose business tool and is not directed at children. Fybre does not knowingly
collect information from anyone, including children.

## Changes to this policy

If Capture's handling of information changes, this policy will be updated and its effective date
changed. Its history is available in the
[Capture repository on GitHub](https://github.com/Fybre/Capture/commits/main/PRIVACY.md).

## Contact

For questions about this policy or Capture's handling of information, open an issue at
<https://github.com/Fybre/Capture/issues> or visit <https://fybre.me>.
