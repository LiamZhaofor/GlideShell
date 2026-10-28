"""帧节奏实测：退出已装实例 → 用指定构建依次跑几种模拟手势各若干次 → 汇总每种覆盖层的晚到帧 → 重新启动已装实例。

用法：python -I -X utf8 tools/bench/pacing.py <GlideShell.exe> <标签> [次数]
- 要用普通权限的 NativeAOT 构建（build/publish.ps1 -Aot，复制到别处再测）：JIT 版每次会话第一帧都在编译，
  UIAccess 版不能从 Program Files 以外的地方启动。
- 模拟手势不改前台窗口、不切桌面，但覆盖层会在屏幕上出现；测试期间别碰触摸板。
- 「晚到」= 帧间隔超过 1.5 个刷新周期（少取样了一帧）。2.0 调优的基线与结果见 docs/ROADMAP.md 9.2。"""
import os, re, subprocess, sys, time
from collections import defaultdict

INSTALLED = r"C:\Program Files\GlideShell\GlideShell.exe"
LOG = os.path.expandvars(r"%LOCALAPPDATA%\GlideShell\logs\glideshell.log")
exe, label = sys.argv[1], sys.argv[2]
rounds = int(sys.argv[3]) if len(sys.argv) > 3 else 4
scenarios = ["overview-cancel", "overview-flick", "switch-back", "desktop-peek"]
pat = re.compile(r"(\S+?)渲染：(\d+) 帧.*?max ([\d.]+)ms.*?回调最长 ([\d.]+)ms.*?晚到 (\d+) 帧（醒晚 (\d+) / 超时 (\d+)）")

os.startfile(INSTALLED, arguments="--quit")
time.sleep(2.5)
size = os.path.getsize(LOG)
for r in range(rounds):
    for sc in scenarios:
        p = subprocess.Popen([exe, "--simulate", sc])
        try: p.wait(timeout=30)
        except subprocess.TimeoutExpired: p.kill()
        time.sleep(0.5)
os.startfile(INSTALLED)

with open(LOG, encoding="utf-8", errors="replace") as f:
    f.seek(size)
    new = f.read().splitlines()
agg = defaultdict(lambda: [0, 0, 0, 0, 0, 0.0])
for line in new:
    m = pat.search(line)
    if not m or int(m.group(2)) < 2: continue
    a = agg[m.group(1)]
    a[0] += 1; a[1] += int(m.group(2)); a[2] += int(m.group(5)); a[3] += int(m.group(6)); a[4] += int(m.group(7)); a[5] = max(a[5], float(m.group(3)))
print(f"[{label}] 每种模拟 {rounds} 次")
for k, (n, frames, late, woke, over, mx) in agg.items():
    print(f"  {k}：{n} 个会话，{frames} 帧，晚到 {late} 帧（{late / max(1, frames) * 1000:.1f}‰），醒晚 {woke}，超时 {over}，最长间隔 {mx}ms")
