# Roadmap

Planned work, roughly in order. Not commitments; each item gets designed properly when it's picked up.

## SharePoint export

Export documents and their index values to a SharePoint document library, as a fourth export type
alongside CSV, Therefore and REST (`ExportType` has room for it; a new `IExportWriter`).

- Microsoft Graph, signed in as the user (Entra ID app registration; device code or browser sign-in).
- Choose the site, library and folder; map index fields to library columns.
- Large files uploaded in parts (Graph upload sessions, needed over 4 MB).
- The export history records the file's SharePoint address.
- A business OneDrive is a SharePoint library underneath and uses the same Graph calls, so it can stand
  in for development until a SharePoint site is ready.

Waiting on: a SharePoint site to test against.

## Email input

Import attachments from a mailbox, the way a watch folder imports files: each message's attachments
become one input, processed by the folder's capture profile.

- Microsoft 365 mailboxes through Graph (sharing the SharePoint sign-in), and IMAP for others.
- The sender, subject and received date available to fields and the file name pattern.
- Processed messages moved to a folder (or marked read), so nothing is imported twice.
