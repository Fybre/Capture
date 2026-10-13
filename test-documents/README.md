# Test documents

Synthetic sample documents for trying capture profiles, AI document separation and OCR. All names,
numbers and addresses in them are made up.

## Expected document boundaries

For measuring AI separation: the pages (1-based) where a new document starts. Every other page
continues the document before it.

| File | Pages | New document starts on | Contents |
|---|---|---|---|
| `Combined Sample Files.pdf` | 8 | 1, 3, 5, 8 | Loan application (2 pages), medical intake form (2), background check authorisation (3), order receipt (1) |
| `sample_invoices.pdf` | 14 | 1, 2, 3, 5, 6, 7, 9, 10, 12, 13 | Ten invoices on the same template, each with its own invoice number; pages 4, 8, 11 and 14 are line-item continuation pages |
| `mixed_document_samples.pdf` | 8 | 1–8 (every page) | Invoice, legal letter, services agreement, purchase order, statement of account, delivery docket, certificate of currency, remittance advice |

The single-document PDFs (`01_loan_application.pdf`, `02_medical_intake_form.pdf`,
`03_background_check_authorization.pdf`, `ORDER RECEIPT.pdf`) are the parts of
`Combined Sample Files.pdf` on their own.

## Handwriting

`0001-1-cropped.tif`, `0001-2-cropped.tif` and `0001-3-cropped.tif` are handwritten pages with no
text layer — for OCR (built-in or Azure Document Intelligence). Each TIFF also carries an embedded
thumbnail, which Capture skips, and no resolution, so Capture assumes 96 dpi.
