"""Redline an uploaded Word .docx submission against a provided .dotx template.

Produces a .docx based on the template, where every textual difference between
the template and the submission is recorded as a Word **tracked change**
(insertion or deletion) authored by ``Copilot Studio AI``.

No PDF/visual conversion is performed: the template is already a perfect Word
document, so the output is the template body with revisions injected as OOXML
``<w:ins>`` / ``<w:del>`` elements. Accepting all changes yields the
submission's wording; rejecting all keeps the template.

Usage:
    python redline.py <submission.docx|.pdf> [output.docx]
    python redline.py --template <template.dotx|.docx> <submission.docx|.pdf> [output.docx]

If no template is given, the single .dotx/.docx bundled in assets/ is
auto-discovered and used as the baseline (its file name doesn't matter). PDF
submissions are read by extracting their text only (no conversion to DOCX).

The diff is **word-level over the whole document**: both sides are flattened to
a single stream of words and compared once, so PDF line-wrapping and paragraph
boundaries never create false differences. Template tables pass through
untouched and are excluded from the diff on both sides.

Pure lxml + standard library for .docx; .pdf additionally uses pdfplumber
(with a pypdfium2 fallback). All are available in the Copilot Studio sandbox. Testing
"""
import argparse
import copy
import datetime
import os
import re
import sys
import zipfile
from difflib import SequenceMatcher

from lxml import etree

# Directory holding the bundled template asset shipped with this skill.
ASSETS_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "assets",
)


def find_default_template():
    """Return the bundled template from assets/, regardless of its file name.

    The skill ships exactly one template in assets/; its name doesn't matter.
    We pick the single .dotx/.docx there (preferring .dotx), so the template
    can be swapped or renamed without touching this script.
    """
    if not os.path.isdir(ASSETS_DIR):
        raise FileNotFoundError("Assets folder not found: %s" % ASSETS_DIR)
    candidates = [
        os.path.join(ASSETS_DIR, name)
        for name in sorted(os.listdir(ASSETS_DIR))
        if name.lower().endswith((".dotx", ".docx"))
        and not name.startswith("~$")
    ]
    if not candidates:
        raise FileNotFoundError(
            "No .dotx/.docx template found in %s" % ASSETS_DIR
        )
    # Prefer a .dotx template if both a .dotx and .docx are present.
    candidates.sort(key=lambda p: (not p.lower().endswith(".dotx"), p))
    return candidates[0]


# --- OOXML namespaces ------------------------------------------------------
W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
CT = "http://schemas.openxmlformats.org/package/2006/content-types"
XML = "http://www.w3.org/XML/1998/namespace"

AUTHOR = "Copilot Studio AI"
DATE = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

DOC_PART = "/word/document.xml"
CT_TEMPLATE = "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml"
CT_DOCUMENT = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"

_rev_id = 0


def next_id():
    global _rev_id
    _rev_id += 1
    return str(_rev_id)


def wq(name):
    """Qualified name in the wordprocessingml namespace, e.g. wq('w:p')."""
    return "{%s}%s" % (W, name.split(":", 1)[1])


def el(name):
    return etree.Element(wq(name))


# --- text helpers ----------------------------------------------------------

def para_text(p):
    """Concatenate the visible text of a <w:p>, preserving tabs/breaks."""
    parts = []
    for node in p.iter():
        if node.tag == wq("w:t"):
            parts.append(node.text or "")
        elif node.tag == wq("w:tab"):
            parts.append("\t")
        elif node.tag in (wq("w:br"), wq("w:cr")):
            parts.append("\n")
    return "".join(parts)


def tokenize(text):
    """Split into word and whitespace tokens so text reconstructs exactly."""
    return re.findall(r"\S+|\s+", text)


def words(text):
    """Split text into non-whitespace word tokens (whitespace discarded)."""
    return re.findall(r"\S+", text)


def tbl_text(tbl):
    """Concatenate the text of a <w:tbl>, one space between cell paragraphs."""
    return " ".join(para_text(p) for p in tbl.iter(wq("w:p")))


def first_rpr(p):
    """Return a copy of the first run-properties (<w:rPr>) found in a paragraph."""
    for r in p.iter(wq("w:r")):
        rpr = r.find(wq("w:rPr"))
        if rpr is not None:
            return copy.deepcopy(rpr)
    return None


# --- run / revision builders ----------------------------------------------

def make_run(text, rpr=None, deleted=False):
    r = el("w:r")
    if rpr is not None:
        r.append(copy.deepcopy(rpr))
    t = el("w:delText" if deleted else "w:t")
    t.set("{%s}space" % XML, "preserve")
    t.text = text
    r.append(t)
    return r


def wrap_revision(kind, runs):
    """Wrap runs in a <w:ins> or <w:del> revision authored by Copilot Studio AI."""
    e = el(kind)  # 'w:ins' or 'w:del'
    e.set(wq("w:id"), next_id())
    e.set(wq("w:author"), AUTHOR)
    e.set(wq("w:date"), DATE)
    for r in runs:
        e.append(r)
    return e


# --- global word-level diff engine -----------------------------------------
#
# Rationale (validated against real Copilot Studio runs): a paragraph-level
# diff is fragile because PDFs wrap a single logical paragraph across several
# lines with no blank line between them, so line-per-paragraph splitting
# produces hundreds of false paragraph mismatches. Instead we flatten BOTH
# sides to a single stream of words and diff once. Line-wrap and paragraph
# boundaries become irrelevant -- only real word differences matter. The
# template's paragraph structure and formatting are still preserved by mapping
# each template word back to its source paragraph and only rebuilding the
# paragraphs that actually changed.


def build_paragraph(ppr, base_rpr, tokens):
    """Rebuild a changed paragraph from (kind, word) tokens.

    ``kind`` is 'eq', 'del' or 'ins'. Consecutive same-kind words are merged
    into one run; every segment after the first carries a single leading space
    so that accepting/rejecting a revision keeps spacing balanced.
    """
    new_p = el("w:p")
    if ppr is not None:
        new_p.append(copy.deepcopy(ppr))

    # Merge consecutive tokens of the same kind into segments.
    segments = []
    for kind, word in tokens:
        if segments and segments[-1][0] == kind:
            segments[-1][1].append(word)
        else:
            segments.append((kind, [word]))

    first = True
    for kind, ws in segments:
        text = " ".join(ws)
        if not first:
            text = " " + text
        first = False
        if kind == "eq":
            new_p.append(make_run(text, base_rpr))
        elif kind == "del":
            new_p.append(wrap_revision("w:del", [make_run(text, base_rpr, deleted=True)]))
        else:  # ins
            new_p.append(wrap_revision("w:ins", [make_run(text, base_rpr)]))
    return new_p


def redline_body(tpl_body, sub_words, stats):
    """Inject tracked changes into ``tpl_body`` given a flat submission word list.

    ``sub_words`` is the ordered list of every word in the submission (table
    content already excluded). Tables and other non-paragraph blocks in the
    template pass through untouched and are excluded from the diff.
    """
    sequence = []        # ordered body items: ('p', idx) or ('other', element)
    paras = []           # per-paragraph: {'el','ppr','base'}
    tpl_words = []       # flat template word list (paragraph text only)
    word_para = []       # parallel: source paragraph index for each template word

    for child in tpl_body:
        if child.tag == wq("w:p"):
            pi = len(paras)
            paras.append({
                "el": child,
                "ppr": child.find(wq("w:pPr")),
                "base": first_rpr(child),
            })
            for w in words(para_text(child)):
                tpl_words.append(w)
                word_para.append(pi)
            sequence.append(("p", pi))
        else:
            sequence.append(("other", child))

    # Diff the two flat word streams once.
    status = ["eq"] * len(tpl_words)     # 'eq' or 'del' per template word
    inserts_at = {}                      # template-word index -> [inserted words]
    sm = SequenceMatcher(a=tpl_words, b=sub_words, autojunk=False)
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == "delete":
            for k in range(i1, i2):
                status[k] = "del"
        elif tag == "insert":
            inserts_at.setdefault(i1, []).extend(sub_words[j1:j2])
        elif tag == "replace":
            for k in range(i1, i2):
                status[k] = "del"
            inserts_at.setdefault(i1, []).extend(sub_words[j1:j2])

    # Distribute words + insertions back onto their source paragraphs.
    para_tokens = {pi: [] for pi in range(len(paras))}
    for k in range(len(tpl_words)):
        pi = word_para[k]
        if k in inserts_at:
            for w in inserts_at[k]:
                para_tokens[pi].append(("ins", w))
        para_tokens[pi].append(("del" if status[k] == "del" else "eq", tpl_words[k]))
    # Trailing insertions (anchored past the last template word).
    if paras and len(tpl_words) in inserts_at:
        for w in inserts_at[len(tpl_words)]:
            para_tokens[len(paras) - 1].append(("ins", w))

    stats["inserted_words"] = sum(len(v) for v in inserts_at.values())
    stats["deleted_words"] = sum(1 for s in status if s == "del")

    # Rebuild the body, keeping unchanged paragraphs byte-for-byte.
    new_children = []
    for kind, ref in sequence:
        if kind == "other":
            new_children.append(ref)
            continue
        toks = para_tokens[ref]
        if not toks or all(t[0] == "eq" for t in toks):
            new_children.append(paras[ref]["el"])      # unchanged -> preserve original
        else:
            new_children.append(build_paragraph(paras[ref]["ppr"], paras[ref]["base"], toks))
            stats["changed_paragraphs"] += 1

    for c in list(tpl_body):
        tpl_body.remove(c)
    for c in new_children:
        tpl_body.append(c)


def template_table_words(tpl_body):
    """All words that live inside template <w:tbl> elements (excluded from diff)."""
    out = []
    for child in tpl_body:
        if child.tag == wq("w:tbl"):
            out.extend(words(tbl_text(child)))
    return out


def strip_table_words(sub_words, table_words):
    """Remove the template's table content from a flat submission word list.

    A PDF renders a Word table as flat inline text, so its words would otherwise
    be flagged as insertions. We locate the table's contiguous span in the
    submission via SequenceMatcher and drop it. Returns (new_words, removed).
    Only strips when most of the table is clearly present, to avoid removing
    unrelated body text that happens to share words.
    """
    if not table_words:
        return sub_words, 0
    sm = SequenceMatcher(a=table_words, b=sub_words, autojunk=False)
    blocks = [b for b in sm.get_matching_blocks() if b.size > 0]
    matched = sum(b.size for b in blocks)
    if matched < 0.6 * len(table_words):
        return sub_words, 0
    lo = min(b.b for b in blocks)
    hi = max(b.b + b.size for b in blocks)
    return sub_words[:lo] + sub_words[hi:], hi - lo


# --- table-cell redlining (.docx submissions only) -------------------------
#
# A .docx submission preserves table structure, so we can align the template's
# tables to the submission's tables by position (table -> row -> cell) and diff
# each cell's text word-by-word, injecting tracked changes directly into the
# template cell while preserving its <w:tcPr> (width, borders, shading). A PDF
# has no table structure to align, so tables remain passthrough for PDFs.


def diff_tokens(tpl_words, sub_words):
    """Word-level diff of two word lists -> list of (kind, word) tokens.

    ``kind`` is 'eq', 'del' or 'ins'. For a replacement the deleted words are
    emitted before the inserted words at that position.
    """
    toks = []
    sm = SequenceMatcher(a=tpl_words, b=sub_words, autojunk=False)
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == "equal":
            toks.extend(("eq", w) for w in tpl_words[i1:i2])
        elif tag == "delete":
            toks.extend(("del", w) for w in tpl_words[i1:i2])
        elif tag == "insert":
            toks.extend(("ins", w) for w in sub_words[j1:j2])
        elif tag == "replace":
            toks.extend(("del", w) for w in tpl_words[i1:i2])
            toks.extend(("ins", w) for w in sub_words[j1:j2])
    return toks


def cell_words(tc):
    """All words in a <w:tc>, across its paragraphs (nested tables excluded)."""
    out = []
    for p in tc.findall(wq("w:p")):
        out.extend(words(para_text(p)))
    return out


def redline_cell(tpl_cell, sub_cell, stats):
    """Diff one template cell against a submission cell; inject ins/del in place.

    Returns True if the cell changed. The cell's <w:tcPr> and any non-paragraph
    block content are preserved; the paragraph text is collapsed into a single
    rebuilt paragraph carrying the tracked changes.
    """
    tw = cell_words(tpl_cell)
    sw = cell_words(sub_cell)
    if tw == sw:
        return False

    first_p = tpl_cell.find(wq("w:p"))
    ppr = first_p.find(wq("w:pPr")) if first_p is not None else None
    base = first_rpr(first_p) if first_p is not None else None
    toks = diff_tokens(tw, sw)
    new_p = build_paragraph(ppr, base, toks)

    for p in tpl_cell.findall(wq("w:p")):
        tpl_cell.remove(p)
    tpl_cell.append(new_p)        # tcPr (if any) stays first; cell still ends in <w:p>

    stats["inserted_words"] += sum(1 for k, _ in toks if k == "ins")
    stats["deleted_words"] += sum(1 for k, _ in toks if k == "del")
    return True


def redline_tables(tpl_body, sub_body, stats):
    """Redline template tables against submission tables, aligned by position."""
    tpl_tables = tpl_body.findall(wq("w:tbl"))
    sub_tables = sub_body.findall(wq("w:tbl"))
    for ti, tpl_tbl in enumerate(tpl_tables):
        if ti >= len(sub_tables):
            break
        tpl_rows = tpl_tbl.findall(wq("w:tr"))
        sub_rows = sub_tables[ti].findall(wq("w:tr"))
        for ri, tpl_row in enumerate(tpl_rows):
            if ri >= len(sub_rows):
                break
            tpl_cells = tpl_row.findall(wq("w:tc"))
            sub_cells = sub_rows[ri].findall(wq("w:tc"))
            for ci, tpl_cell in enumerate(tpl_cells):
                if ci >= len(sub_cells):
                    break
                if redline_cell(tpl_cell, sub_cells[ci], stats):
                    stats["changed_cells"] += 1


# --- packaging -------------------------------------------------------------

def read_part(path, part):
    with zipfile.ZipFile(path) as z:
        return z.read(part)


def enable_track_changes(settings_bytes):
    """Insert <w:trackChanges/> so Word keeps tracking further edits."""
    try:
        root = etree.fromstring(settings_bytes)
        if root.find(wq("w:trackChanges")) is None:
            root.insert(0, el("w:trackChanges"))
        return etree.tostring(root, xml_declaration=True, encoding="UTF-8", standalone=True)
    except Exception:
        return settings_bytes


def convert_content_types(ct_bytes):
    """Turn a .dotx main-part content type into a .docx one."""
    try:
        root = etree.fromstring(ct_bytes)
        for ov in root.findall("{%s}Override" % CT):
            if ov.get("PartName") == DOC_PART and ov.get("ContentType") == CT_TEMPLATE:
                ov.set("ContentType", CT_DOCUMENT)
        return etree.tostring(root, xml_declaration=True, encoding="UTF-8", standalone=True)
    except Exception:
        return ct_bytes


def write_output(template_path, out_path, new_doc_xml):
    with zipfile.ZipFile(template_path) as zin:
        infos = zin.infolist()
        data = {i.filename: zin.read(i.filename) for i in infos}

    data["word/document.xml"] = new_doc_xml
    if "word/settings.xml" in data:
        data["word/settings.xml"] = enable_track_changes(data["word/settings.xml"])
    if "[Content_Types].xml" in data:
        data["[Content_Types].xml"] = convert_content_types(data["[Content_Types].xml"])

    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as zout:
        for i in infos:
            zout.writestr(i, data[i.filename])


# --- submission readers (the only docx/pdf fork) ---------------------------
#
# Body paragraph text is always diffed as a flat word list. For .docx the
# submission body element is also returned so its tables can be diffed against
# the template's tables (see redline_tables). For .pdf there is no structure, so
# table content is instead stripped from the flat word list.

def read_docx_body(path):
    """Return the <w:body> element of a .docx/.dotx submission."""
    body = etree.fromstring(read_part(path, "word/document.xml")).find(wq("w:body"))
    if body is None:
        raise ValueError("Could not locate <w:body> in submission: %s" % path)
    return body


def body_paragraph_words(body):
    """Flat word list of a body's top-level paragraph text (tables excluded)."""
    out = []
    for child in body:
        if child.tag == wq("w:p"):
            out.extend(words(para_text(child)))
    return out


def read_pdf_words(path):
    """Extract a .pdf submission into a flat word list (no DOCX conversion).

    Uses pdfplumber (layout-aware) when available, falling back to pypdfium2.
    Line-wrap boundaries are irrelevant because the diff is word-level, so we
    simply collect every word across all pages in reading order.
    """
    pages = []
    try:
        import pdfplumber
        with pdfplumber.open(path) as pdf:
            for page in pdf.pages:
                pages.append(page.extract_text() or "")
    except Exception:
        import pypdfium2 as pdfium
        pdf = pdfium.PdfDocument(path)
        try:
            for i in range(len(pdf)):
                pages.append(pdf[i].get_textpage().get_text_range() or "")
        finally:
            pdf.close()
    out = []
    for raw in pages:
        out.extend(words(raw))
    return out


def read_submission(path):
    """Return (paragraph_words, sub_body, is_pdf) for a submission.

    ``sub_body`` is the submission's <w:body> element for .docx (enabling table
    diffing), or None for .pdf.
    """
    if path.rsplit(".", 1)[-1].lower() == "pdf":
        return read_pdf_words(path), None, True
    body = read_docx_body(path)
    return body_paragraph_words(body), body, False


# --- entry point -----------------------------------------------------------

def redline(submission_path, template_path=None, out_path=None):
    if not template_path:
        template_path = find_default_template()
    if not os.path.isfile(template_path):
        raise FileNotFoundError("Template not found: %s" % template_path)
    if not out_path:
        out_path = submission_path.rsplit(".", 1)[0] + "_redlined.docx"

    tpl_tree = etree.fromstring(read_part(template_path, "word/document.xml"))
    tpl_body = tpl_tree.find(wq("w:body"))
    if tpl_body is None:
        raise ValueError("Could not locate <w:body> in template: %s" % template_path)

    sub_words, sub_body, is_pdf = read_submission(submission_path)
    stats = {"changed_paragraphs": 0, "changed_cells": 0,
             "inserted_words": 0, "deleted_words": 0}

    removed = 0
    if is_pdf:
        # A PDF flattens the template's tables into inline text; strip them so
        # they aren't flagged as insertions (tables pass through unchanged).
        sub_words, removed = strip_table_words(sub_words, template_table_words(tpl_body))
    else:
        # .docx: diff tables cell-by-cell before the body is rebuilt.
        redline_tables(tpl_body, sub_body, stats)

    redline_body(tpl_body, sub_words, stats)

    new_doc_xml = etree.tostring(tpl_tree, xml_declaration=True, encoding="UTF-8", standalone=True)
    write_output(template_path, out_path, new_doc_xml)

    total = stats["inserted_words"] + stats["deleted_words"]
    print("Redline complete -> %s" % out_path)
    print("  author of changes  : %s" % AUTHOR)
    print("  changed paragraphs : %d" % stats["changed_paragraphs"])
    print("  changed table cells: %d" % stats["changed_cells"])
    print("  inserted words     : %d" % stats["inserted_words"])
    print("  deleted words      : %d" % stats["deleted_words"])
    if is_pdf and removed:
        print("  table words skipped: %d (template tables passed through)" % removed)
    if total == 0:
        print("  (no differences found - submission matches the template)")
    return out_path


if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        prog="redline.py",
        description="Redline a .docx/.pdf submission against a template as Word "
                    "tracked changes.",
    )
    parser.add_argument(
        "--template",
        metavar="PATH",
        default=None,
        help="Template (.dotx/.docx) to use as the baseline. Defaults to the "
             "single template bundled in assets/.",
    )
    parser.add_argument(
        "submission",
        help="The uploaded file to compare against the template (.docx or .pdf).",
    )
    parser.add_argument(
        "output",
        nargs="?",
        default=None,
        help="Output .docx path. Defaults to <submission>_redlined.docx.",
    )
    ns = parser.parse_args()

    redline(ns.submission, ns.template, ns.output)
