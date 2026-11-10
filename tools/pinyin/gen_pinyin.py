"""从 Unicode Unihan 数据生成 GlideShell 的汉字拼音表（src/GlideShell.Core/Search/Pinyin.bin）。

用法：python tools/pinyin/gen_pinyin.py <Unihan.zip 路径>
Unihan.zip 从 https://www.unicode.org/Public/UCD/latest/ucd/Unihan.zip 下载（Unicode 许可证），不放进仓库。

读音取 kMandarin（最常用的读音，排第一）加 kXHC1983（《现代汉语词典》）与 kTGHZ2013（《通用规范汉字字典》）里的读音，
去掉声调、ü 写作 v，去重。不取 kHanyuPinyin（《汉语大字典》，古音、僻音太多，匹配会出很多意外结果）。
只收 CJK 统一汉字基本区 U+4E00–U+9FFF（程序名里的汉字都在这里）。

文件格式（小端）：
  "PY1\\0"
  uint16 音节数 N，接着 N 个以 \\0 结尾的 ASCII 音节
  uint32 首个码位（0x4E00），uint32 码位个数
  每个码位：uint8 读音数 n，接着 n 个 uint16 音节下标
"""
import os
import struct
import sys
import unicodedata
import zipfile

FIRST, LAST = 0x4E00, 0x9FFF
FIELDS = ('kMandarin', 'kXHC1983', 'kTGHZ2013')


def plain(reading: str) -> str:
    # 去声调：分解后丢掉组合附加符号；ü（分解后是 u + 分音符）写作 v
    out = []
    for ch in unicodedata.normalize('NFD', reading.lower()):
        if ch == '̈':  # 分音符：前面的 u 改成 v
            out[-1] = 'v'
        elif unicodedata.category(ch) != 'Mn':
            out.append(ch)
    return ''.join(out)


def main(path: str) -> None:
    text = zipfile.ZipFile(path).read('Unihan_Readings.txt').decode('utf-8')
    readings: dict[int, dict[str, list[str]]] = {}
    for line in text.splitlines():
        if not line.startswith('U+'):
            continue
        cp_text, field, value = line.split('\t', 2)
        if field not in FIELDS:
            continue
        cp = int(cp_text[2:], 16)
        if not FIRST <= cp <= LAST:
            continue
        values = [v.split(':')[-1] for v in value.split(' ')]
        readings.setdefault(cp, {}).setdefault(field, []).extend(values)

    syllables: list[str] = []
    index: dict[str, int] = {}
    table: list[list[int]] = []
    for cp in range(FIRST, LAST + 1):
        per = readings.get(cp, {})
        seen: list[str] = []
        for field in FIELDS:
            for r in per.get(field, []):
                p = plain(r)
                if p.isascii() and p.isalpha() and p not in seen:
                    seen.append(p)
        ids = []
        for p in seen:
            if p not in index:
                index[p] = len(syllables)
                syllables.append(p)
            ids.append(index[p])
        table.append(ids)

    out = bytearray(b'PY1\0')
    out += struct.pack('<H', len(syllables))
    for s in syllables:
        out += s.encode('ascii') + b'\0'
    out += struct.pack('<II', FIRST, LAST - FIRST + 1)
    for ids in table:
        out += struct.pack('<B', len(ids))
        for i in ids:
            out += struct.pack('<H', i)

    target = os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'GlideShell.Core', 'Search', 'Pinyin.bin')
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, 'wb') as f:
        f.write(out)
    covered = sum(1 for ids in table if ids)
    multi = sum(1 for ids in table if len(ids) > 1)
    print(f'{len(syllables)} 个音节，{covered} 个汉字有读音（其中多音字 {multi} 个），{len(out)} 字节 -> {os.path.normpath(target)}')


if __name__ == '__main__':
    main(sys.argv[1])
