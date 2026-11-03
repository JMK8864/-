#!/usr/bin/env python3
"""장치A/장치B 래더 프로그램 정의 → SVG 래더 도식, 니모닉, 디바이스 코멘트 CSV 생성.

래더 1개(렁)의 정의:
    title    : 렁 설명
    branches : 첫 구간의 병렬(OR) 회로. [[접점, ...], [접점, ...]]
    series   : 그 뒤 직렬 접점 [접점, ...]
    outputs  : 병렬 출력 [출력, ...]
접점: ("NO"|"NC"|"P"|"CMP", 디바이스 또는 비교식)
출력: ("OUT", 디바이스[, 설정값]) | ("FN", "명령 오퍼랜드...")

사용법: python3 docs/plc/ladder_gen.py   (docs/plc/ 아래에 결과를 쓴다)
"""
import csv
import html
import os

HERE = os.path.dirname(os.path.abspath(__file__))

# ───────────────────────── 디바이스 코멘트 ─────────────────────────
COMMENTS = {
    "A": {
        "X0": "운전 준비 신호", "X1": "장치 이상 신호", "X2": "장치A 운전 시작 신호",
        "X3": "장치 준비 완료 신호", "X4": "장치B 운전 시작 신호", "X5": "장치 생산수 카운트 신호",
        "Y70": "장치 운전 준비 완료 지시", "Y71": "장치B 운전 준비 완료 출력",
        "Y72": "운전 시작 지시(운전중)", "Y73": "장치B 운전중 출력",
        "Y40": "장치A 생산수 출력(K4Y40)", "Y50": "장치B 생산수 출력(K4Y50)",
        "M0": "장치A 운전 준비 완료", "M1": "네트워크 정상", "M10": "장치A 운전중",
        "M11": "장치B 연동 확인", "M20": "총 생산수 달성", "T0": "B2 ON 유지 1초",
        "B0": "장치B 운전 준비 지시", "B1": "장치B 운전 시작 지시", "B2": "총 생산수 달성",
        "B100": "장치B 운전 준비 완료", "B101": "장치B 운전중",
        "W0": "장치B 목표 생산수 지시", "W100": "장치B 생산수",
        "D0": "장치A 생산수", "D10": "총 생산수", "D20": "총 목표 생산수", "D21": "장치B 목표 생산수",
        "SB49": "자국 링크 상태", "SW74": "각국 전송 상태",
        "SM400": "항상 ON", "SM402": "RUN 후 1스캔 ON",
    },
    "B": {
        "X0": "장치 이상 신호", "X1": "장치 준비 완료 신호", "X2": "단독 운전 시작 신호",
        "X3": "생산수 카운트 신호", "Y70": "장치 운전 준비 완료 지시", "Y71": "운전 시작 지시(운전중)",
        "M0": "장치B 준비 완료", "M1": "네트워크 정상", "M2": "운전 준비 지시 수신",
        "M10": "장치B 운전중", "M12": "연동 운전 모드",
        "B0": "장치B 운전 준비 지시", "B1": "장치B 운전 시작 지시", "B2": "총 생산수 달성",
        "B100": "장치B 운전 준비 완료", "B101": "장치B 운전중",
        "W0": "장치B 목표 생산수 지시", "W100": "장치B 생산수",
        "D0": "장치B 생산수", "D20": "장치B 목표 생산수",
        "SB49": "자국 링크 상태", "SW74": "각국 전송 상태", "SM400": "항상 ON",
    },
}

# ───────────────────────── 프로그램 정의 ─────────────────────────
def R(title, branches, series=(), outputs=()):
    return {"title": title, "branches": branches, "series": list(series), "outputs": list(outputs)}

PROGRAM_A = [
    R("초기값 설정 (RUN 후 1스캔)", [[("NO", "SM402")]], [],
      [("FN", "MOV K1000 D20"), ("FN", "MOV K500 D21")]),
    R("네트워크 상태 인터록 (자국 + 국번2)", [[("NC", "SB49")]], [("NC", "SW74.1")], [("OUT", "M1")]),
    R("운전 준비 완료", [[("NO", "X0")]], [("NO", "X3"), ("NC", "X1")], [("OUT", "M0"), ("OUT", "Y70")]),
    R("장치A 운전 시작 (새 사이클)", [[("P", "X2")]], [("NO", "M0"), ("NC", "M10")],
      [("FN", "MOV K0 D0"), ("FN", "RST M20"), ("FN", "RST M11"), ("FN", "RST B2"), ("FN", "SET M10")]),
    R("이상·준비 해제 시 정지", [[("NO", "X1")], [("NC", "X0")]], [], [("FN", "RST M10")]),
    R("운전중 출력", [[("NO", "M10")]], [], [("OUT", "Y72")]),
    R("B0 장치B 운전 준비 지시 송신 (SET)", [[("NO", "M10")]], [("NO", "M1"), ("NC", "B100")], [("FN", "SET B0")]),
    R("B0 리셋 (B100 확인 / 정지 / 통신 이상)", [[("NO", "B100")], [("NC", "M10")], [("NC", "M1")]], [],
      [("FN", "RST B0")]),
    R("B1 장치B 운전 시작 지시 송신 (SET)", [[("NO", "M10")]],
      [("NO", "M1"), ("NO", "X4"), ("NO", "B100"), ("NC", "B101")], [("FN", "SET B1")]),
    R("B1 리셋 (B101 확인 / 정지 / 통신 이상)", [[("NO", "B101")], [("NC", "M10")], [("NC", "M1")]], [],
      [("FN", "RST B1")]),
    R("장치B 운전 준비 완료 출력", [[("NO", "B100")]], [("NO", "M1")], [("OUT", "Y71")]),
    R("장치B 운전중 출력", [[("NO", "B101")]], [("NO", "M1")], [("OUT", "Y73")]),
    R("장치B 목표 생산수 송신", [[("NO", "SM400")]], [], [("FN", "MOV D21 W0")]),
    R("장치A 생산수 카운트-업", [[("P", "X5")]], [("NO", "M10")], [("FN", "INC D0")]),
    R("장치B 새 사이클 운전 확인 (W100 유효)", [[("P", "B101")]], [("NO", "M10")], [("FN", "SET M11")]),
    R("총 생산수 = A + B", [[("NO", "M11")]], [], [("FN", "+ D0 W100 D10")]),
    R("총 생산수 = A (B 미연동)", [[("NC", "M11")]], [], [("FN", "MOV D0 D10")]),
    R("목표 달성 판정 → 운전 종료, B2 송신", [[("CMP", ">= D10 D20")]], [("NO", "M10")],
      [("FN", "SET M20"), ("FN", "SET B2"), ("FN", "RST M10")]),
    R("B2 ON 시간 확보 (B101 OFF 후 1초)", [[("NO", "B2")]], [("NC", "B101")], [("OUT", "T0", "K10")]),
    R("B2 리셋", [[("NO", "T0")]], [], [("FN", "RST B2")]),
    R("생산수 표시반 출력 (BCD)", [[("NO", "SM400")]], [], [("FN", "BCD D0 K4Y40"), ("FN", "BCD W100 K4Y50")]),
]

PROGRAM_B = [
    R("네트워크 상태 인터록 (자국 + 국번1)", [[("NC", "SB49")]], [("NC", "SW74.0")], [("OUT", "M1")]),
    R("장치B 준비 완료", [[("NO", "X1")]], [("NC", "X0")], [("OUT", "M0"), ("OUT", "Y70")]),
    R("B0 운전 준비 지시 수신 → 래치", [[("NO", "B0")]], [("NO", "M1")], [("FN", "SET M2")]),
    R("B100 운전 준비 완료 회신", [[("NO", "M2")]], [("NO", "M0")], [("OUT", "B100")]),
    R("목표 생산수 수신", [[("NO", "M1")]], [], [("FN", "MOV W0 D20")]),
    R("연동 운전 시작 (B1)", [[("NO", "B1")]], [("NO", "M1"), ("NO", "M2"), ("NO", "M0"), ("NC", "M10")],
      [("FN", "MOV K0 D0"), ("FN", "SET M10"), ("FN", "SET M12")]),
    R("단독 운전 시작 (X2)", [[("P", "X2")]], [("NO", "M0"), ("NC", "M10")],
      [("FN", "MOV K0 D0"), ("FN", "SET M10"), ("FN", "RST M12")]),
    R("총 생산수 달성 수신 → 준비 래치 해제", [[("NO", "B2")]], [("NO", "M1")], [("FN", "RST M2")]),
    R("총 생산수 달성 수신 → 연동 운전 종료", [[("NO", "B2")]], [("NO", "M1"), ("NO", "M12")], [("FN", "RST M10")]),
    R("이상 정지", [[("NO", "X0")]], [], [("FN", "RST M10"), ("FN", "RST M2")]),
    R("연동 운전 중 통신 이상 → 정지", [[("NC", "M1")]], [("NO", "M12"), ("NO", "M10")], [("FN", "RST M10")]),
    R("운전중 출력 / B101 송신", [[("NO", "M10")]], [], [("OUT", "Y71"), ("OUT", "B101")]),
    R("생산수 카운트-업", [[("P", "X3")]], [("NO", "M10")], [("FN", "INC D0")]),
    R("생산수 송신", [[("NO", "SM400")]], [], [("FN", "MOV D0 W100")]),
]

PROGRAMS = {"A": ("장치A (국번1)", PROGRAM_A), "B": ("장치B (국번2)", PROGRAM_B)}

# ───────────────────────── 니모닉 ─────────────────────────
FIRST = {"NO": "LD", "NC": "LDI", "P": "LDP"}
OR_ = {"NO": "OR", "NC": "ORI", "P": "ORP"}
AND_ = {"NO": "AND", "NC": "ANI", "P": "ANDP"}


def contact_inst(kind, dev, table):
    if kind == "CMP":
        op, *args = dev.split()
        pre = {"LD": "LD", "OR": "OR", "AND": "AND"}[table]
        return f"{pre}{op:<4}" + " ".join(args)
    t = {"LD": FIRST, "OR": OR_, "AND": AND_}[table]
    return f"{t[kind]:<6}{dev}"


def mnemonic(rungs):
    lines = []
    for i, r in enumerate(rungs):
        lines.append(f"; ({i}) {r['title']}")
        for bi, br in enumerate(r["branches"]):
            if len(br) != 1:
                raise ValueError("branch with >1 contact not supported")
            lines.append(contact_inst(br[0][0], br[0][1], "LD" if bi == 0 else "OR"))
        for k, d in r["series"]:
            lines.append(contact_inst(k, d, "AND"))
        for o in r["outputs"]:
            if o[0] == "OUT":
                lines.append(f"{'OUT':<6}{o[1]}" + (f" {o[2]}" if len(o) > 2 else ""))
            else:
                op, *args = o[1].split()
                lines.append(f"{op:<6}" + " ".join(args))
        lines.append("")
    lines.append("END")
    return "\n".join(lines) + "\n"

# ───────────────────────── SVG 래더 ─────────────────────────
CW, RH = 92, 58          # 셀 폭, 행 높이
LX = 30                  # 왼쪽 모선
NCOLS = 7                # 접점 열 수
OUTW = 210               # 출력 박스 폭
RX = LX + NCOLS * CW + OUTW + 30   # 오른쪽 모선
FONT = "'Noto Sans KR','Nanum Gothic Coding',sans-serif"
STROKE = "#1f2937"
DEV = "#1d4ed8"
CMT = "#047857"


def _esc(s):
    return html.escape(s, quote=True)


def base_dev(d):
    return d.split(".")[0]


def draw_contact(o, x, y, kind, dev, cm):
    """x: 셀 왼쪽, y: 회로 라인 높이."""
    cx = x + CW / 2
    if kind == "CMP":
        w = CW - 8
        o.append(f'<line x1="{x}" y1="{y}" x2="{x+4}" y2="{y}" stroke="{STROKE}" stroke-width="1.5"/>')
        o.append(f'<line x1="{x+CW-4}" y1="{y}" x2="{x+CW}" y2="{y}" stroke="{STROKE}" stroke-width="1.5"/>')
        o.append(f'<rect x="{x+4}" y="{y-11}" width="{w}" height="22" fill="#fff" stroke="{STROKE}" stroke-width="1.5"/>')
        o.append(f'<text x="{cx}" y="{y+4}" font-size="11.5" text-anchor="middle" fill="{DEV}" font-weight="600">{_esc(dev)}</text>')
        return
    l, r = cx - 9, cx + 9
    o.append(f'<line x1="{x}" y1="{y}" x2="{l}" y2="{y}" stroke="{STROKE}" stroke-width="1.5"/>')
    o.append(f'<line x1="{r}" y1="{y}" x2="{x+CW}" y2="{y}" stroke="{STROKE}" stroke-width="1.5"/>')
    o.append(f'<line x1="{l}" y1="{y-11}" x2="{l}" y2="{y+11}" stroke="{STROKE}" stroke-width="2"/>')
    o.append(f'<line x1="{r}" y1="{y-11}" x2="{r}" y2="{y+11}" stroke="{STROKE}" stroke-width="2"/>')
    if kind == "NC":
        o.append(f'<line x1="{l-3}" y1="{y+11}" x2="{r+3}" y2="{y-11}" stroke="{STROKE}" stroke-width="1.5"/>')
    if kind == "P":
        o.append(f'<line x1="{cx}" y1="{y+9}" x2="{cx}" y2="{y-8}" stroke="{STROKE}" stroke-width="1.3"/>')
        o.append(f'<path d="M{cx-4},{y-3} L{cx},{y-9} L{cx+4},{y-3}" fill="none" stroke="{STROKE}" stroke-width="1.3"/>')
    o.append(f'<text x="{cx}" y="{y-16}" font-size="12" text-anchor="middle" fill="{DEV}" font-weight="600">{_esc(dev)}</text>')
    c = cm.get(base_dev(dev), "")
    if c:
        o.append(f'<text x="{cx}" y="{y+24}" font-size="9.5" text-anchor="middle" fill="{CMT}">{_esc(c)}</text>')


def draw_output(o, x, y, out, cm):
    w = OUTW - 20
    o.append(f'<line x1="{x}" y1="{y}" x2="{x+10}" y2="{y}" stroke="{STROKE}" stroke-width="1.5"/>')
    o.append(f'<line x1="{x+10+w}" y1="{y}" x2="{RX}" y2="{y}" stroke="{STROKE}" stroke-width="1.5"/>')
    if out[0] == "OUT":
        dev = out[1]
        cx = x + 10 + w / 2
        o.append(f'<line x1="{x+10}" y1="{y}" x2="{cx-14}" y2="{y}" stroke="{STROKE}" stroke-width="1.5"/>')
        o.append(f'<line x1="{cx+14}" y1="{y}" x2="{x+10+w}" y2="{y}" stroke="{STROKE}" stroke-width="1.5"/>')
        o.append(f'<path d="M{cx-8},{y-12} Q{cx-16},{y} {cx-8},{y+12}" fill="none" stroke="{STROKE}" stroke-width="2"/>')
        o.append(f'<path d="M{cx+8},{y-12} Q{cx+16},{y} {cx+8},{y+12}" fill="none" stroke="{STROKE}" stroke-width="2"/>')
        label = dev + (f"  {out[2]}" if len(out) > 2 else "")
        o.append(f'<text x="{cx}" y="{y-16}" font-size="12" text-anchor="middle" fill="{DEV}" font-weight="600">{_esc(label)}</text>')
        c = cm.get(base_dev(dev), "")
        if c:
            o.append(f'<text x="{cx}" y="{y+24}" font-size="9.5" text-anchor="middle" fill="{CMT}">{_esc(c)}</text>')
    else:
        o.append(f'<rect x="{x+10}" y="{y-12}" width="{w}" height="24" fill="#f8fafc" stroke="{STROKE}" stroke-width="1.5"/>')
        parts = out[1].split()
        o.append(f'<text x="{x+10+w/2}" y="{y+4.5}" font-size="12.5" text-anchor="middle" '
                 f'font-family="\'Nanum Gothic Coding\',monospace" fill="{STROKE}">'
                 f'<tspan font-weight="700">{_esc(parts[0])}</tspan>'
                 + "".join(f'<tspan dx="8" fill="{DEV}">{_esc(a)}</tspan>' for a in parts[1:]) + "</text>")
        # 오퍼랜드 중 첫 디바이스 코멘트
        devs = [a for a in parts[1:] if not a.startswith("K")]
        cs = [cm.get(d, "") for d in devs]
        txt = " / ".join(c for c in cs if c)
        if txt:
            o.append(f'<text x="{x+10+w/2}" y="{y+24}" font-size="9.5" text-anchor="middle" fill="{CMT}">{_esc(txt)}</text>')


def rung_svg(idx, r, cm, step):
    nbr = len(r["branches"])
    nout = len(r["outputs"])
    rows = max(nbr, nout)
    top = 26
    h = top + rows * RH + 6
    o = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {RX+10} {h}" width="{RX+10}" height="{h}" '
         f'font-family="{FONT}" role="img" aria-label="렁 {idx}: {_esc(r["title"])}">']
    o.append(f'<text x="4" y="15" font-size="12" font-weight="700" fill="{STROKE}">({idx}) {_esc(r["title"])}</text>')
    o.append(f'<text x="{RX+6}" y="15" font-size="10" text-anchor="end" fill="#6b7280">스텝 {step}</text>')
    o.append(f'<line x1="{LX}" y1="{top}" x2="{LX}" y2="{h-4}" stroke="{STROKE}" stroke-width="3"/>')
    o.append(f'<line x1="{RX}" y1="{top}" x2="{RX}" y2="{h-4}" stroke="{STROKE}" stroke-width="3"/>')
    y0 = top + RH / 2 + 4
    # 첫 구간 (OR 병렬)
    x = LX
    for bi, br in enumerate(r["branches"]):
        yy = y0 + bi * RH
        draw_contact(o, x, yy, br[0][0], br[0][1], cm)
    if nbr > 1:
        o.append(f'<line x1="{x+CW}" y1="{y0}" x2="{x+CW}" y2="{y0+(nbr-1)*RH}" stroke="{STROKE}" stroke-width="1.5"/>')
    x += CW
    for k, d in r["series"]:
        draw_contact(o, x, y0, k, d, cm)
        x += CW
    ox = LX + NCOLS * CW
    if x < ox:
        o.append(f'<line x1="{x}" y1="{y0}" x2="{ox}" y2="{y0}" stroke="{STROKE}" stroke-width="1.5"/>')
    for oi, out in enumerate(r["outputs"]):
        draw_output(o, ox, y0 + oi * RH, out, cm)
    if nout > 1:
        o.append(f'<line x1="{ox}" y1="{y0}" x2="{ox}" y2="{y0+(nout-1)*RH}" stroke="{STROKE}" stroke-width="1.5"/>')
    o.append("</svg>")
    return "\n".join(o)


def step_count(r):
    """대략적인 스텝 위치 표시용 (실제 스텝 수는 GX Works3 변환 결과를 따른다)."""
    n = len(r["branches"]) + len(r["series"])
    for out in r["outputs"]:
        n += 2 if out[0] == "OUT" else 3
    return n


def program_svgs(key):
    _, rungs = PROGRAMS[key]
    cm = COMMENTS[key]
    out, step = [], 0
    for i, r in enumerate(rungs):
        out.append(rung_svg(i, r, cm, step))
        step += step_count(r)
    return out


def end_svg():
    h = 40
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {RX+10} {h}" width="{RX+10}" height="{h}" font-family="{FONT}">'
            f'<line x1="{LX}" y1="0" x2="{LX}" y2="{h-6}" stroke="{STROKE}" stroke-width="3"/>'
            f'<line x1="{RX}" y1="0" x2="{RX}" y2="{h-6}" stroke="{STROKE}" stroke-width="3"/>'
            f'<line x1="{LX}" y1="{h/2}" x2="{RX-80}" y2="{h/2}" stroke="{STROKE}" stroke-width="1.5"/>'
            f'<rect x="{RX-80}" y="{h/2-11}" width="70" height="22" fill="#f8fafc" stroke="{STROKE}" stroke-width="1.5"/>'
            f'<text x="{RX-45}" y="{h/2+4.5}" font-size="12.5" text-anchor="middle" font-weight="700">END</text></svg>')


# ───────────────────────── CSV (디바이스 코멘트) ─────────────────────────
def sort_key(d):
    import re
    m = re.match(r"([A-Z]+)(\w+)", d)
    pre, num = m.group(1), m.group(2)
    order = ["X", "Y", "M", "T", "D", "B", "W", "SM", "SB", "SW"]
    base = 16 if pre in ("X", "Y", "B", "W", "SB", "SW") else 10
    return (order.index(pre) if pre in order else 99, int(num, base))


def write_csv(key, path):
    with open(path, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f)
        w.writerow(["Device Name", "Comment"])
        for d in sorted(COMMENTS[key], key=sort_key):
            w.writerow([d, COMMENTS[key][d]])


def main():
    for key in PROGRAMS:
        with open(os.path.join(HERE, f"device{key}_mnemonic.txt"), "w", encoding="utf-8") as f:
            f.write(f"; {PROGRAMS[key][0]} — MELSEC iQ-R / GX Works3 MAIN 프로그램\n\n")
            f.write(mnemonic(PROGRAMS[key][1]))
        write_csv(key, os.path.join(HERE, f"device{key}_comment.csv"))
        svgdir = os.path.join(HERE, "ladder_svg")
        os.makedirs(svgdir, exist_ok=True)
        for i, s in enumerate(program_svgs(key)):
            with open(os.path.join(svgdir, f"{key}_{i:02d}.svg"), "w", encoding="utf-8") as f:
                f.write(s)
    print("done")


if __name__ == "__main__":
    main()
