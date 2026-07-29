/**
 * Typeset the whitepaper's equations with KaTeX, at build time.
 *
 * The site ships no runtime JavaScript for maths: this writes the rendered
 * markup into site/whitepaper.html so the page stays static, renders with
 * JavaScript disabled, and never flashes raw LaTeX.
 *
 *   npm ci
 *   node scripts/build-whitepaper-math.mjs
 *
 * Source of truth is the data-tex attribute on each .eq element, seeded from
 * scripts/whitepaper-equations.json. Re-running is idempotent: the rendered
 * span is replaced from data-tex every time, so editing the LaTeX and
 * re-running is the whole workflow.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import katex from 'katex';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const PAGE = join(ROOT, 'site', 'whitepaper.html');

const decode = (s) => s.replace(/&amp;/g, '&').replace(/&lt;/g, '<')
                       .replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'");
const encode = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;')
                       .replace(/>/g, '&gt;').replace(/"/g, '&quot;');

const render = (tex, display) => {
  try {
    return katex.renderToString(tex, {
      displayMode: display, throwOnError: true, strict: 'ignore',
      // default output is htmlAndMathml: the visual spans are aria-hidden and
      // the MathML alongside them is what a screen reader actually reads.
      trust: false,
    });
  } catch (e) {
    console.error(`\n  LaTeX error in: ${tex}\n  ${e.message}\n`);
    process.exitCode = 1;
    return null;
  }
};

let html = readFileSync(PAGE, 'utf8');
let display = 0, inline = 0;

// Display equations: <div class="eq" data-tex="...">…rendered…</div>
html = html.replace(/<div class="eq" data-tex="([^"]*)">[\s\S]*?<\/div>\s*(?=<div class="eq"|<\/div>)/g,
  (m, tex) => {
    const out = render(decode(tex), true);
    if (out === null) return m;
    display++;
    return `<div class="eq" data-tex="${tex}">${out}</div>\n          `;
  });

// Inline maths: <span class="m" data-tex="...">…rendered…</span>
html = html.replace(/<span class="m" data-tex="([^"]*)">[\s\S]*?<\/span>/g, (m, tex) => {
  const out = render(decode(tex), false);
  if (out === null) return m;
  inline++;
  return `<span class="m" data-tex="${tex}">${out}</span>`;
});

writeFileSync(PAGE, html, 'utf8');
console.log(`typeset ${display} display equations and ${inline} inline expressions`);
if (process.exitCode) console.error('one or more expressions failed; page left partly unrendered');
