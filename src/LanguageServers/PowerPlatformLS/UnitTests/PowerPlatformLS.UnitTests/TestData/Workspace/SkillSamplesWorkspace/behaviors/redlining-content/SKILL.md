---
name: redlining-content
description: Use when the user asks to redline, track changes, or compare differences in a file. Compares an uploaded .docx or .pdf against a provided .dotx/.docx template and returns a redlined .docx where every textual difference is a Word tracked change (insertion/deletion) authored by "Copilot Studio AI". No visual conversion — built directly on the template.
---

# Redlining Content Skill

Reimplements "Word Compare" for the new Copilot Studio orchestrator. The
template is already a perfect Word document, so the output **is** the template
body with revisions injected as OOXML `<w:ins>` / `<w:del>` elements. Accepting
all changes yields the submission's wording; rejecting all keeps the template.

Changes are authored by **`Copilot Studio AI`** and the output has Track
Changes turned on so Word keeps tracking any further human edits.

## Requirements

- `lxml` (always)
- `pdfplumber` (PDF submissions; `pypdfium2` used as a fallback)

All are present in the Copilot Studio sandbox — no `pip install` required.
`.docx`/`.dotx` handling is pure `lxml` + standard library.

## Inputs

1. **Template** — the canonical baseline. Bundled with this skill in
   `assets/` (any single `.dotx`/`.docx` file — the name doesn't matter) and
   used automatically. A different template (`.dotx` or `.docx`) may be
   supplied explicitly to override it.
2. **Submission** — the user-uploaded file to compare against the template,
   either a `.docx` or a `.pdf`.

## Steps

1. Run from this skill's directory:
   `python scripts/redline.py <submission.docx|.pdf> [output.docx]`
   The bundled template in `assets/` is auto-discovered and used as the
   baseline (whatever its file name).
   To override the template explicitly, pass it via `--template`:
   `python scripts/redline.py --template <template.dotx|.docx> <submission.docx|.pdf> [output.docx]`
2. Return the output `.docx` to the user. Output defaults to the submission
   name with a `_redlined.docx` suffix.

## How it works

One shared engine, two input readers — the only thing that differs by file
type is how the submission's words are read:

- `.docx` / `.dotx` → `read_docx_words()` (paragraph text from
  `word/document.xml`).
- `.pdf` → `read_pdf_words()` (**text extraction only — never converted** to
  DOCX; uses pdfplumber, then pypdfium2).

Both produce a single flat **word list** fed into the same pipeline:

1. **Word-level diff over the whole document.** The template's words and the
   submission's words are each flattened into one stream and compared once with
   `difflib.SequenceMatcher`. PDF line-wrapping and paragraph boundaries are
   therefore irrelevant — only real word differences matter.
2. Each template word is mapped back to its source paragraph. Paragraphs with no
   changes are kept **byte-for-byte** (all formatting preserved); only changed
   paragraphs are rebuilt, with differing words wrapped in `<w:ins>` / `<w:del>`.
3. **Tables.** For `.docx` submissions, template tables are diffed against the
   submission's tables **cell by cell** (aligned by position: table → row →
   cell), with `<w:ins>` / `<w:del>` injected directly into each cell while its
   `<w:tcPr>` (width, borders, shading) is preserved. For `.pdf` submissions
   there is no table structure to align, so template tables pass through
   untouched and their words are stripped from the extracted text so they aren't
   flagged as insertions.
4. Writes the output zip: replaces `word/document.xml`, adds `<w:trackChanges/>`
   to `settings.xml`, and converts the `.dotx` main-part content type to the
   `.docx` one so the result opens as a normal document.

## Per-input guidance

See the focused reference docs:

- `references/docx-submissions.md` — `.docx` handling, high-fidelity path.
- `references/pdf-submissions.md` — `.pdf` handling, why text is extracted (not
  converted), the word-level diff, and table handling.

## v1 limitations

- Inside a *changed* paragraph, intra-run **character** formatting (bold/italic
  on individual words) is simplified to the paragraph's base formatting.
  Unchanged paragraphs keep all formatting exactly.
- **Tables**: `.docx` table cells are diffed and redlined (aligned by position);
  cells are matched by position, so inserted/deleted rows or columns and nested
  tables are not tracked, and multiple paragraphs in one cell collapse to one.
  `.pdf` tables are not diffed (passed through unchanged).
- A brand-new paragraph in the submission is tracked as inserted **words** at
  the nearest template position; a hard paragraph break may not be recreated.
- Matching is by visible text; curly quotes/apostrophes are compared literally.
