#!/usr/bin/env python3
"""GX Works3 프로젝트(.gx3) 생성 (실험용).

GX Works3 에서 만든 빈 프로젝트(.gx3, zip)를 틀로 받아, MAIN 프로그램의 래더 DB
(<hash>_LDDB.db 의 LadderBlocks 테이블)에 ladder_gen.py 의 PROGRAM_A / PROGRAM_B 렁을
써 넣고 새 .gx3 로 다시 묶는다. 나머지(유닛 구성, 파라미터, 코멘트)는 틀 그대로다.

래더 블록 문자열 형식 (GX Works3 내부, 비공개 — 실제 파일에서 관찰한 규칙):
    V1:<토큰수>:<각 토큰 길이...>:<토큰...>:cb{fg=fg{dim=<폭>x<행>:es=[<요소>...]:vs=[<세로선>...]}}
    - 토큰: 접점은 역할(a/b)+디바이스형(X,M,...), 코일은 c+디바이스형, 명령은 명령명+피연산자형
    - 요소의 '#' 는 토큰을 앞에서부터 차례로 가리킨다
    - 접점  e{s=ce{op=ct{op=#:ct=a|p:as=[as{vt=Abl}]}:args=[d{s=#:a=<번호>:vt=nn}]}:pos=x,y}
    - 출력  e{s=ce{op=cl{op=#:ct=a:as=[...]}:args=[...]}:pos=x,y}
    - 세로선 v{pos=x,y}  : x 열 왼쪽, y-1 행과 y 행을 잇는다
    - 비트 지정 SW74.1 = 토큰 SW,Dots / M{b=d{..}:m=c{s=#:v=1}}
    - 자릿수 지정 K4Y40 = 토큰 Y,Ks / M{b=d{..}:m=c{s=#:v=4}}

사용법:
    python3 docs/plc/gx3_build.py <틀.gx3> <출력폴더>
    → <출력폴더>/장치A_국번1.gx3, 장치B_국번2.gx3
"""
import os
import re
import shutil
import sqlite3
import sys
import tempfile
import uuid
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ladder_gen import PROGRAM_A, PROGRAM_B  # noqa: E402

HEX_TYPES = {"X", "Y", "B", "W", "SB", "SW"}
BIT_OPERAND_OPS = {"SET", "RST"}      # 비트 피연산자 명령 (as vt=Abl)
# 칸 폭: 코일(c)만 1칸, 명령 박스는 1+피연산자 수 (SET M10 = 2칸, MOV K0 D0 = 3칸).
# GX Works3 가 저장한 렁에서 확인: RST M10 이 pos=10 에 놓이고 dim 폭 12.


class Rung:
    def __init__(self):
        self.tokens = []
        self.elems = []
        self.verts = []
        self.width = 0
        self.rows = 1

    def tok(self, *ts):
        self.tokens.extend(ts)

    def data(self):
        lens = ":".join(str(len(t)) for t in self.tokens)
        s = (f"V1:{len(self.tokens)}:{lens}:{':'.join(self.tokens)}:"
             f"cb{{fg=fg{{dim={self.width}x{self.rows}:es=[{':'.join(self.elems)}]")
        if self.verts:
            s += f":vs=[{':'.join(self.verts)}]"
        return s + "}}"


def split_dev(text):
    """'SW74.1' -> ('SW', 0x74, '1', None) / 'K4Y40' -> ('Y', 0x40, None, 4) / 'K10' -> const"""
    m = re.fullmatch(r"K(\d)([XYMB]\w+)", text)
    if m:
        t, n, _, _ = split_dev(m.group(2))
        return t, n, None, int(m.group(1))
    m = re.fullmatch(r"(SM|SB|SW|SD|X|Y|M|B|W|D|T)([0-9A-F]+)(?:\.([0-9A-F]))?", text)
    if not m:
        raise ValueError(text)
    t, num, bit = m.groups()
    return t, int(num, 16 if t in HEX_TYPES else 10), bit, None


def operand(r, text):
    """피연산자 하나: 토큰을 추가하고 인자 문자열을 돌려준다."""
    if re.fullmatch(r"K-?\d+", text):
        r.tok("K_1")
        return f"c{{s=#:v={int(text[1:])}:si=s}}"
    t, n, bit, digit = split_dev(text)
    d = f"d{{s=#:a={n}:vt=nn}}"
    if bit is not None:
        r.tok(t, "Dots")
        return f"M{{b={d}:m=c{{s=#:v={int(bit, 16)}}}}}"
    if digit is not None:
        r.tok(t, "Ks")
        return f"M{{b={d}:m=c{{s=#:v={digit}}}}}"
    r.tok(t)
    return d


def contact(r, kind, dev, x, y):
    if kind == "CMP":
        op, a1, a2 = dev.split()
        r.tok(op)
        args = [operand(r, a1), operand(r, a2)]
        r.elems.append(f"e{{s=ce{{op=ct{{op=#:ct=a:as=[as{{vt=A16}}:as{{vt=A16}}]}}"
                       f":args=[{':'.join(args)}]}}:pos={x},{y}}}")
        return 3
    r.tok("b" if kind == "NC" else "a")
    arg = operand(r, dev)
    ct = "p" if kind == "P" else "a"
    r.elems.append(f"e{{s=ce{{op=ct{{op=#:ct={ct}:as=[as{{vt=Abl}}]}}:args=[{arg}]}}:pos={x},{y}}}")
    return 1


def output(r, out, x, y):
    if out[0] == "OUT":
        dev = out[1]
        if len(out) > 2:                      # 타이머 OUT T0 K10
            r.tok("OUT__16")
            args = [operand(r, dev), operand(r, out[2])]
            r.elems.append(f"e{{s=ce{{op=cl{{op=#:ct=a:as=[as{{vt=Abl}}:as{{vt=A16}}]}}"
                           f":args=[{':'.join(args)}]}}:pos={x},{y}}}")
            return 1 + len(args)
        r.tok("c")
        arg = operand(r, dev)
        r.elems.append(f"e{{s=ce{{op=cl{{op=#:ct=a:as=[as{{vt=Abl}}]}}:args=[{arg}]}}:pos={x},{y}}}")
        return 1
    op, *ops = out[1].split()
    r.tok(op)
    args = [operand(r, a) for a in ops]
    vt = "Abl" if op in BIT_OPERAND_OPS else "A16"
    types = ":".join(f"as{{vt={vt}}}" for _ in ops)
    r.elems.append(f"e{{s=ce{{op=cl{{op=#:ct=a:as=[{types}]}}:args=[{':'.join(args)}]}}:pos={x},{y}}}")
    return 1 + len(ops)


def build_rung(spec):
    r = Rung()
    # 첫 구간 (OR 병렬, 각 1접점)
    w0 = 0
    for y, br in enumerate(spec["branches"]):
        (kind, dev), = br
        w0 = max(w0, contact(r, kind, dev, 0, y))
    nb = len(spec["branches"])
    for y in range(1, nb):
        r.verts.append(f"v{{pos={w0},{y}}}")
    x = w0
    for kind, dev in spec["series"]:
        x += contact(r, kind, dev, x, 0)
    no = len(spec["outputs"])
    ow = 0
    for y, out in enumerate(spec["outputs"]):
        ow = max(ow, output(r, out, x, y))
    for y in range(1, no):
        r.verts.append(f"v{{pos={x},{y}}}")
    r.width = x + ow
    r.rows = max(nb, no)
    return r


def find_lddb(root):
    c = [f for f in os.listdir(root) if f.endswith("_LDDB.db")]
    if len(c) != 1:
        raise SystemExit(f"LDDB 가 1개가 아님: {c}")
    return os.path.join(root, c[0])


def write_program(lddb_path, rungs):
    con = sqlite3.connect(lddb_path)
    rows = con.execute("select id,pos,blocktype from LadderBlocks order by pos").fetchall()
    end = [r for r in rows if r[2] == 5]
    if len(end) != 1:
        raise SystemExit("END 블록을 찾지 못함")
    end_pos = end[0][1]
    con.execute("delete from LadderBlocks where blocktype<>5")
    step = end_pos / (len(rungs) + 1)
    for i, spec in enumerate(rungs):
        r = build_rung(spec)
        con.execute("insert into LadderBlocks(id,pos,blocktype,data,rowsize,translated,ConvTarget) "
                    "values(?,?,0,?,?,0,0)",
                    (f"_guid/{uuid.uuid4()}", step * (i + 1),
                     r.data(), r.rows))
    con.commit()
    con.close()


def repack(template, workdir, out_path, lddb_name):
    with zipfile.ZipFile(template) as zin, zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as zout:
        for info in zin.infolist():
            data = zin.read(info.filename)
            if info.filename == lddb_name:
                with open(os.path.join(workdir, lddb_name), "rb") as f:
                    data = f.read()
            zout.writestr(info, data)


def build(template, out_path, rungs):
    with tempfile.TemporaryDirectory() as tmp:
        with zipfile.ZipFile(template) as z:
            names = [n for n in z.namelist() if n.endswith("_LDDB.db") and "\\" not in n and "/" not in n]
            if len(names) != 1:
                raise SystemExit(f"LDDB 가 1개가 아님: {names}")
            lddb = names[0]
            with open(os.path.join(tmp, lddb), "wb") as f:
                f.write(z.read(lddb))
        write_program(os.path.join(tmp, lddb), rungs)
        repack(template, tmp, out_path, lddb)


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        raise SystemExit(1)
    template, outdir = sys.argv[1], sys.argv[2]
    os.makedirs(outdir, exist_ok=True)
    for name, prog in (("장치A_국번1.gx3", PROGRAM_A), ("장치B_국번2.gx3", PROGRAM_B)):
        p = os.path.join(outdir, name)
        build(template, p, prog)
        print(p)


if __name__ == "__main__":
    main()
