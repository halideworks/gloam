"""
Generate site/whitepaper.md from site/whitepaper.html.

The Markdown copy exists so agents, LLM crawlers and anyone piping the paper
into a tool can read it without parsing the page chrome. It is generated rather
than hand-maintained so the two cannot drift; re-run after editing the HTML:

    python scripts/build-whitepaper-md.py

Only the <main> content is converted, and only the tag vocabulary the whitepaper
actually uses. Anything unexpected raises rather than being silently dropped, so
a future markup change surfaces here instead of quietly losing a section.
"""

import html
import io
import os
import re
from html.parser import HTMLParser

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "site", "whitepaper.html")
DST = os.path.join(ROOT, "site", "whitepaper.md")
ORIGIN = "https://getgloam.org"

INLINE_OPEN = {
    # Emphasis uses * rather than _ because subscripts are written with _ below,
    # and this paper is full of an italic variable immediately followed by its
    # subscript: <em>Y</em><sub>rel</sub> would close as _Y__{rel}, whose double
    # underscore a Markdown reader takes for bold.
    "strong": "**", "b": "**", "em": "*", "i": "*", "cite": "*", "code": "`",
}
# Rendered as plain text. Markdown has no portable sub/sup, so <sub> and <sup>
# are NOT in here: they carry meaning this document cannot afford to drop.
# Flattening them turns m<sub>1</sub> into "m1", which reads as a variable named
# m1 rather than as m subscript 1, and x<sup>y</sup> into "xy". They are written
# in the _ and ^ notation instead, which is what a reader piping this into a
# tool will expect.
PASSTHROUGH = {"span", "abbr", "small", "br", "nav", "svg", "text", "g", "path", "rect", "line", "polyline", "circle"}


class Extractor(HTMLParser):
    """Pulls the <main> subtree out as a light tag tree."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.depth = 0
        self.capture = False
        self.out = []
        self.skip_depth = 0

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "main":
            self.capture = True
            return
        if not self.capture:
            return
        # Drop the in-page contents nav and any inline svg diagrams.
        if tag in ("nav", "svg") or a.get("class") == "toc":
            self.skip_depth += 1
            return
        if self.skip_depth:
            return
        self.out.append(("start", tag, a))

    def handle_endtag(self, tag):
        if tag == "main":
            self.capture = False
            return
        if not self.capture:
            return
        if self.skip_depth:
            if tag in ("nav", "svg"):
                self.skip_depth -= 1
            return
        self.out.append(("end", tag, None))

    def handle_data(self, data):
        if self.capture and not self.skip_depth:
            self.out.append(("text", data, None))


def collapse(s):
    return re.sub(r"\s+", " ", s)


def render(tokens):
    """Walk the token stream and emit Markdown blocks."""
    md = []
    buf = []
    stack = []
    # Open <sub>/<sup> elements, as (tag, index into buf where the body starts).
    scripts = []
    list_stack = []
    in_formula = False
    formula_lines = []
    table = None

    def flush(prefix=""):
        text = collapse("".join(buf)).strip()
        buf.clear()
        if text:
            md.append(prefix + text)
            md.append("")

    for kind, a, attrs in tokens:
        if kind == "text":
            if in_formula:
                formula_lines.append(a)
            else:
                buf.append(a)
            continue

        tag = a
        if kind == "start":
            if tag in ("h1", "h2", "h3", "h4"):
                flush()
                stack.append(tag)
            elif tag == "p":
                flush()
            elif tag == "div" and attrs.get("class") == "formula":
                flush()
                in_formula = True
                formula_lines = []
            elif tag == "br":
                if in_formula:
                    formula_lines.append("\n")
                else:
                    buf.append(" ")
            elif tag in ("ul", "ol"):
                flush()
                list_stack.append("ol" if tag == "ol" else "ul")
            elif tag == "li":
                flush()
            elif tag == "table":
                flush()
                table = {"rows": [], "cur": None, "head": False}
            elif tag == "tr" and table is not None:
                table["cur"] = []
            elif tag in ("td", "th") and table is not None:
                buf.clear()
                if tag == "th":
                    table["head"] = True
            elif tag == "a":
                if not in_formula:
                    buf.append("[")
                    stack.append(("a", attrs.get("href", "")))
            elif tag in ("sub", "sup"):
                # Unlike emphasis, these are written even inside a formula: the
                # code fence is exactly where losing them does the most damage.
                # Formula text accumulates in its own buffer, so remember which
                # one this body lands in. It is rewritten on the closing tag.
                target = formula_lines if in_formula else buf
                scripts.append((tag, target, len(target)))
            elif tag in INLINE_OPEN:
                # Formula bodies are emitted verbatim in a code fence, so inline
                # emphasis inside them must not leak markers into the text buffer.
                if not in_formula:
                    buf.append(INLINE_OPEN[tag])
            elif tag in PASSTHROUGH or tag in ("article", "section", "div", "header", "footer", "aside", "figure", "figcaption", "tbody", "thead", "tfoot", "colgroup", "col", "caption", "details", "summary", "dl", "dt", "dd", "blockquote", "hr", "img", "kbd", "var", "samp", "u", "s", "mark", "time"):
                pass
            else:
                raise ValueError("unhandled start tag: " + tag)

        else:  # end
            if tag in ("sub", "sup"):
                if scripts and scripts[-1][0] == tag:
                    _, target, start = scripts.pop()
                    inner = "".join(target[start:]).strip()
                    del target[start:]
                    if inner:
                        mark = "_" if tag == "sub" else "^"
                        # Braces only where they disambiguate, so the common
                        # single-character case stays readable: m_1, not m_{1}.
                        target.append(mark + (inner if len(inner) == 1 else "{" + inner + "}"))
            elif tag in ("h1", "h2", "h3", "h4"):
                level = int(tag[1])
                flush("#" * level + " ")
                if stack and stack[-1] == tag:
                    stack.pop()
            elif tag == "p":
                flush()
            elif tag == "div" and in_formula:
                in_formula = False
                raw = "".join(formula_lines)
                lines = [collapse(x).strip() for x in raw.split("\n")]
                lines = [x for x in lines if x]
                md.append("```")
                md.extend(lines)
                md.append("```")
                md.append("")
            elif tag in ("ul", "ol"):
                if list_stack:
                    list_stack.pop()
                md.append("")
            elif tag == "li":
                marker = "- " if (not list_stack or list_stack[-1] == "ul") else "1. "
                text = collapse("".join(buf)).strip()
                buf.clear()
                if text:
                    md.append(marker + text)
            elif tag in ("td", "th") and table is not None:
                table["cur"].append(collapse("".join(buf)).strip())
                buf.clear()
            elif tag == "tr" and table is not None:
                table["rows"].append(table["cur"])
                table["cur"] = None
            elif tag == "table" and table is not None:
                rows = [r for r in table["rows"] if r]
                if rows:
                    md.append("| " + " | ".join(rows[0]) + " |")
                    md.append("| " + " | ".join("---" for _ in rows[0]) + " |")
                    for r in rows[1:]:
                        md.append("| " + " | ".join(r) + " |")
                    md.append("")
                table = None
            elif tag == "a":
                if not in_formula:
                    href = ""
                    if stack and isinstance(stack[-1], tuple) and stack[-1][0] == "a":
                        href = stack.pop()[1]
                    if href.startswith("#"):
                        href = ORIGIN + "/whitepaper.html" + href
                    elif href.endswith(".html") or href.startswith("assets/"):
                        href = ORIGIN + "/" + href.lstrip("/")
                    buf.append("](" + href + ")")
            elif tag in INLINE_OPEN:
                if not in_formula:
                    buf.append(INLINE_OPEN[tag])

    flush()
    return md


def main():
    src = io.open(SRC, encoding="utf-8").read()
    ex = Extractor()
    ex.feed(src)
    md = render(ex.out)

    body = "\n".join(md)
    body = re.sub(r"\n{3,}", "\n\n", body)
    # Inline markers that ended up wrapping nothing. The empty-code-span rule
    # must not match inside a ``` fence, hence the lookaround.
    body = re.sub(r"\*\*\s*\*\*", "", body)
    body = re.sub(r"(?<!`)``(?!`)", "", body)
    body = re.sub(r"[ \t]+\n", "\n", body)
    body = re.sub(r"\[\s*\]\([^)]*\)", "", body)

    header = (
        "<!-- Generated from whitepaper.html by scripts/build-whitepaper-md.py.\n"
        "     Edit the HTML, then re-run the script. Do not edit this file. -->\n\n"
        f"> Canonical HTML version: {ORIGIN}/whitepaper.html\n\n"
    )

    io.open(DST, "w", encoding="utf-8", newline="\n").write(header + body.strip() + "\n")
    words = len(re.sub(r"[#*_`|>-]", " ", body).split())
    print(f"wrote {DST} ({words} words, {len(body.splitlines())} lines)")


if __name__ == "__main__":
    main()
