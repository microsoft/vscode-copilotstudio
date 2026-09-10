# Reference — `.docx` submissions

How the redline engine handles a Word `.docx` upload compared against the
template baseline.

## What is read

`scripts/redline.py` → `read_submission()` opens the submission as a zip, reads
`word/document.xml`, and returns both:

- a flat ordered **list of words** from top-level paragraphs (for the body
  diff), and
- the submission `<w:body>` element, so its **tables** can be diffed against the
  template's tables.

## How it diffs

**Body paragraphs:** the submission paragraph words are compared against the
template's flattened paragraph words with a single `difflib.SequenceMatcher`
pass. Each template word is mapped back to its source paragraph, so only changed
paragraphs are rebuilt; unchanged paragraphs are kept **byte-for-byte**.

**Tables:** template tables are aligned to submission tables by position
(table → row → cell) and each cell's text is diffed word-by-word. Tracked
changes are injected directly into the template cell and its `<w:tcPr>` (width,
borders, shading) is preserved.

## Fidelity

High. The word-level diff is robust and localized; unchanged paragraphs and
unchanged cells are preserved untouched, so styling is fully retained except
inside content that changed.

## Known limitations

- Inside a *changed* paragraph or cell, intra-run **character** formatting (e.g.
  one bold word) is simplified to the base run formatting. Unchanged paragraphs
  and cells keep all formatting exactly.
- **Tables are aligned by position.** Inserted/deleted rows or columns are not
  tracked as such, nested tables are not diffed, and multiple paragraphs in one
  cell collapse into a single rebuilt paragraph.
- A brand-new paragraph in the submission is tracked as inserted **words** at
  the nearest template position; a hard paragraph break may not be recreated.
- Curly quotes / apostrophes are compared literally, so `'` vs `’` reads as a
  change. Normalize the inputs first if that matters.

## Run

```
python scripts/redline.py <submission.docx>                                   # bundled template
python scripts/redline.py --template <template.dotx|.docx> <submission.docx>  # explicit template
```
