# Reference — `.pdf` submissions

How the redline engine handles a `.pdf` upload compared against the template
baseline.

## Principle: extract text, do NOT convert

A PDF is **never** converted to DOCX. Converting (rebuilding layout) is lossy
and injects formatting artifacts that pollute the diff — you would end up
redlining the converter's reconstruction, not the real content. The engine only
needs the submission's **text**, so the PDF path is just a different *reader*;
everything downstream is identical to the `.docx` path and the output is still a
redlined `.docx` built on the template. Testing.

## What is read

`scripts/redline.py` → `read_pdf_words()`:

1. Extracts text per page with **pdfplumber** (layout-aware); falls back to
   **pypdfium2** if pdfplumber fails. Both are available in the Copilot Studio
   sandbox. No conversion library (e.g. PyMuPDF / pdf2docx) is used.
2. Flattens every page to a single ordered **list of words** (reading order).

## Why word-level, not line/paragraph-level

PDFs wrap one logical paragraph across several visual lines with no blank line
between them, and text extractors emit one `\n` per line and drop blank-line
gaps. An earlier line-per-paragraph approach therefore produced **hundreds of
false paragraph mismatches** (one real paragraph split into many).

The engine instead flattens **both** sides to a single word stream and diffs
once with `difflib.SequenceMatcher`. Line-wrap and paragraph boundaries become
irrelevant — only real word differences are recorded. Template paragraph
structure and formatting are preserved by mapping each template word back to its
source paragraph and rebuilding only the paragraphs that actually changed.

## Tables

A Word table in the template renders in a PDF as flat inline text, which would
otherwise be flagged as inserted words. The engine:

- excludes `<w:tbl>` content from the template word list (tables pass through
  unchanged), and
- strips the template's table words from the PDF word list before diffing
  (`strip_table_words`, located via `SequenceMatcher`), so both sides balance.

This strip only fires when most of the table is clearly present in the PDF, to
avoid removing unrelated body text. If the submitter materially rewrote table
data, table changes are simply not tracked (tables are out of scope for v1).

## Fidelity / limitations

- **No formatting** comes from a PDF, so inserted text adopts the anchoring
  template paragraph's base formatting.
- A new paragraph added in the PDF is tracked as inserted **words** at the
  nearest template position; a hard paragraph break may not be recreated.
- Columns, headers/footers, and complex reading order can scramble extracted
  word order.

Treat PDF redlines as a best-effort draft to review, not a character-perfect
compare.

## Run

```
python scripts/redline.py <submission.pdf>                                   # bundled template
python scripts/redline.py --template <template.dotx|.docx> <submission.pdf>  # explicit template
```
