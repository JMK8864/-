#!/usr/bin/env python3
"""docs/06_PLC-네트워크-래더프로그램.md → PDF (pandoc + Chromium/Playwright).

ASCII 래더 블록(<!-- LADDER:A/B --> 뒤의 <pre>)을 ladder_gen 의 SVG 래더로 바꿔 넣는다.
사용법: python3 docs/plc/build_pdf.py
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DOCS = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import ladder_gen  # noqa: E402

MD = os.path.join(DOCS, "06_PLC-네트워크-래더프로그램.md")
PDF = os.path.join(DOCS, "06_PLC-네트워크-래더프로그램.pdf")

CSS = """
@import url('https://fonts.googleapis.com/css2?family=Noto+Sans+KR:wght@400;500;700&family=Nanum+Gothic+Coding:wght@400;700&display=swap');
@page { size: A4; margin: 16mm 13mm 16mm 13mm; }
body { font-family: 'Noto Sans KR', sans-serif; font-size: 10pt; line-height: 1.55; color: #111827; }
h1 { font-size: 18pt; border-bottom: 3px solid #1d4ed8; padding-bottom: 6px; margin-top: 0; }
h2 { font-size: 14pt; color: #1d4ed8; border-bottom: 1px solid #cbd5e1; padding-bottom: 3px; margin-top: 22px; }
h2.newpage { break-before: page; }
h3 { font-size: 11.5pt; margin-top: 16px; }
table { border-collapse: collapse; width: 100%; margin: 8px 0; font-size: 9pt; break-inside: auto; }
th, td { border: 1px solid #cbd5e1; padding: 3px 6px; vertical-align: top; }
th { background: #eff6ff; }
tr { break-inside: avoid; }
code, pre { font-family: 'Nanum Gothic Coding', monospace; }
pre { background: #f8fafc; border: 1px solid #e2e8f0; padding: 8px 10px; font-size: 8.4pt; line-height: 1.35; white-space: pre-wrap; }
.mn { display: flex; flex-wrap: wrap; gap: 6px; }
.mn pre { margin: 0; width: calc(33.3% - 26px); break-inside: avoid; font-size: 8.4pt; }
code { font-size: 9pt; }
blockquote { margin: 8px 0; padding: 4px 12px; border-left: 4px solid #93c5fd; background: #f8fafc; color: #374151; }
.ladder { margin: 6px 0 14px; }
.ladder svg { display: block; width: 100%; height: auto; break-inside: avoid; margin-bottom: 2px; }
.legend { font-size: 8.5pt; color: #4b5563; margin-bottom: 6px; }
a { color: #1d4ed8; text-decoration: none; }
"""


def ladder_html(key):
    svgs = ladder_gen.program_svgs(key) + [ladder_gen.end_svg()]
    legend = ('<div class="legend">파란 글자: 디바이스 · 초록 글자: 디바이스 코멘트 · '
              '│↑│ 상승 펄스(LDP) · 사선 접점: b접점(LDI/ANI) · 스텝 번호는 참고값(실제는 GX Works3 변환 결과)</div>')
    return '<div class="ladder">' + legend + "\n".join(svgs) + "</div>"


def main():
    body = subprocess.run(["pandoc", MD, "-f", "gfm", "-t", "html5"],
                          capture_output=True, text=True, check=True).stdout
    for key in ("A", "B"):
        body, n = re.subn(r"<!-- LADDER:%s -->\s*<pre[^>]*>.*?</pre>" % key,
                          lambda m: ladder_html(key), body, count=1, flags=re.S)
        assert n == 1, key
    # 니모닉 블록은 3단 배치
    def mn(m):
        chunks = [c.strip("\n") for c in re.split(r"\n\s*\n", m.group(2)) if c.strip()]
        return m.group(1) + '<div class="mn">' + "".join(f"<pre>{c}</pre>" for c in chunks) + "</div>"
    body = re.sub(r'(<h3[^>]*>\d-3\. 니모닉[^<]*</h3>\s*)<pre><code>(.*?)</code></pre>', mn, body, flags=re.S)
    # 주요 장은 새 페이지에서 시작
    for t in ("2. 장치A", "3. 장치B", "7. GX Works3"):
        body = re.sub(r'<h2( id="[^"]*")>(%s)' % re.escape(t), r'<h2 class="newpage"\1>\2', body)
    doc = f'<!doctype html><html lang="ko"><head><meta charset="utf-8"><style>{CSS}</style></head><body>{body}</body></html>'
    html_path = os.path.join(HERE, "_build.html")
    with open(html_path, "w", encoding="utf-8") as f:
        f.write(doc)

    js = r"""
const { chromium } = require('playwright');
(async () => {
  const b = await chromium.launch();
  const p = await b.newPage();
  await p.goto('file://' + process.argv[1], { waitUntil: 'networkidle' });
  await p.evaluate(() => document.fonts.ready);
  await p.pdf({ path: process.argv[2], format: 'A4', printBackground: true,
    displayHeaderFooter: true, headerTemplate: '<span></span>',
    footerTemplate: '<div style="font-size:8px;width:100%;text-align:center;color:#6b7280">장치A·장치B 네트워크 래더 프로그램 — <span class="pageNumber"></span> / <span class="totalPages"></span></div>',
    margin: { top: '14mm', bottom: '16mm', left: '13mm', right: '13mm' } });
  await b.close();
})();
"""
    subprocess.run(["node", "-e", js, html_path, PDF], check=True)
    os.remove(html_path)
    print(PDF)


if __name__ == "__main__":
    main()
