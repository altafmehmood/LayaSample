# Sample documents

Small documents for manually testing `POST /api/documents/analyze`. Each one exercises a different classification path.

All files are generated, so there are no third-party licensing questions. Most come from the test fixtures in [`tests/LayaSample.Tests/DocumentFixtures.cs`](../../tests/LayaSample.Tests/DocumentFixtures.cs). The `-ocr` samples are rendered with real text so OCR has something to read.

## Trying them

1. Start the API: `dotnet run --project src/LayaSample.Api` (listens on http://localhost:5091).
2. Send the samples using one of these:
   - [`samples.http`](samples.http) in VS Code (REST Client extension), Visual Studio or Rider.
   - Scalar at http://localhost:5091/scalar/v1.
   - curl:

     ```sh
     curl -F "file=@docs/samples/pdf/scanned-ocr.pdf" "http://localhost:5091/api/documents/analyze?dispatch=false"
     ```

The API chooses how to prepare each document from its content, page by page: markdown from a usable text layer, a
page image where only a render shows the content (forms, annotations), a page image plus OCR text for scans and
faxes, or only the machine-readable data for dynamic XFA forms. The choice is reported in `classification.strategy`
(`PerPage` when pages differ) and `classification.pages[].strategy`.

Query parameters:

- `dispatch=false` skips the agent call.
- `includeData=false` leaves page images and original bytes out of the response; parts keep their metadata and text.

Errors are [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details (`application/problem+json`) with the
reason in `detail`.

## Expected results

| File | Kind | `classification.strategy` | Parts returned |
|---|---|---|---|
| `pdf/text.pdf` | TextPdf | Markdown | One markdown part per page (2) |
| `pdf/scanned-blank.pdf` | ScannedPdf | Hybrid | Page image only (OCR finds nothing) |
| `pdf/scanned-ocr.pdf` | ScannedPdf | Hybrid | Page image + OCR text of an invoice |
| `pdf/mixed.pdf` | MixedPdf | PerPage | Page 1 markdown, page 2 image (blank, so no OCR text) |
| `pdf/form-filled.pdf` | FormPdf | RenderToPng | Page image with the field drawn + field values as JSON |
| `pdf/annotated-fill-and-sign.pdf` | AnnotatedPdf | RenderToPng | Page image showing the FreeText annotation |
| `pdf/xfa-dynamic.pdf` | XfaPdf | StructuredData | XFA datasets XML only (the page is a viewer placeholder) |
| `pdf/e-invoice-with-xml.pdf` | TextPdf | Markdown | Page markdown + embedded `invoice.xml` (`source` set) |
| `pdf/signed.pdf` | TextPdf | Markdown | Page markdown; `signatures` shows signer "Test Signer", integrity valid, covers the whole file |
| `images/fax-ocr.tif` | Image (2 fax pages) | Hybrid | Image + OCR text per page; 204x98 dpi pages stretched to square pixels |
| `images/fax-blank.tif` | Image (2 fax pages) | Hybrid | Two page images |
| `images/scan-ocr.png` | Image | Hybrid | Image + OCR text |
| `images/blank.png` | Image | Hybrid | Image only |
| `office/orders.xlsx` | Spreadsheet | Markdown | Sheet as a markdown table |
| `office/report.docx` | WordDocument | Markdown | Paragraphs as markdown |
| `unsupported/notes.txt` | — | — | 415: unsupported type (content is sniffed, the name is ignored) |
| `unsupported/legacy.doc` | — | — | 415: legacy or password-protected Office file |

The signature in `pdf/signed.pdf` comes from a throwaway self-signed certificate: integrity is checked, trust is
not. Tampered and later-updated signed files are covered by `DocumentClassifierTests`.

Not covered here: broken text encodings, layout-heavy pages and fax images inside PDFs. These are hard to generate
faithfully. Their rules are unit-tested in `PdfClassificationRulesTests`.

## Regenerating

```sh
dotnet run docs/samples/generate.cs
```

The script is a .NET 10 file-based app that references the test project. Edit it or the fixtures to add samples.
