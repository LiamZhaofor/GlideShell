// GlideShell 官网脚本：主题切换、手势示意图、进场动画、首页的「跟手」演示、教程目录高亮。没有依赖。
(() => {
  "use strict";
  const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;

  // ---------- 主题：跟随系统，可手动切换（记住选择） ----------
  const root = document.documentElement;
  const saved = (() => { try { return localStorage.getItem("theme"); } catch { return null; } })();
  if (saved === "light" || saved === "dark") root.dataset.theme = saved;
  const isDark = () => root.dataset.theme ? root.dataset.theme === "dark" : matchMedia("(prefers-color-scheme: dark)").matches;
  const sun = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="4.2"/><path d="M12 2.5v2.2M12 19.3v2.2M4.6 4.6l1.6 1.6M17.8 17.8l1.6 1.6M2.5 12h2.2M19.3 12h2.2M4.6 19.4l1.6-1.6M17.8 6.2l1.6-1.6"/></svg>';
  const moon = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"><path d="M20.5 14.2A8.5 8.5 0 0 1 9.8 3.5a8.5 8.5 0 1 0 10.7 10.7Z"/></svg>';
  document.querySelectorAll(".theme-toggle").forEach(btn => {
    const paint = () => { btn.innerHTML = isDark() ? sun : moon; btn.setAttribute("aria-label", isDark() ? "切换到浅色" : "切换到深色"); };
    paint();
    btn.addEventListener("click", () => {
      root.dataset.theme = isDark() ? "light" : "dark";
      try { localStorage.setItem("theme", root.dataset.theme); } catch { /* 隐私模式等：只在本页生效 */ }
      paint();
    });
  });

  // ---------- 手势示意图：<span class="pad" data-fingers="3" data-dir="up|down|vertical|horizontal|tap"> ----------
  const arrow = (x1, y1, x2, y2) => {
    const a = Math.atan2(y2 - y1, x2 - x1), h = 7;
    const p = (ang) => `${x2 - h * Math.cos(a - ang)},${y2 - h * Math.sin(a - ang)}`;
    return `<path d="M${x1},${y1} L${x2},${y2} M${p(0.55)} L${x2},${y2} L${p(-0.55)}" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/>`;
  };
  document.querySelectorAll(".pad[data-fingers]").forEach(el => {
    const n = +el.dataset.fingers || 3, dir = el.dataset.dir || "up";
    const gap = 17, x0 = 80 - gap * (n - 1) / 2;
    let dots = "";
    for (let i = 0; i < n; i++) {
      const y = 52 + (i === 0 || i === n - 1 ? 4 : 0);
      dots += `<circle cx="${x0 + gap * i}" cy="${y}" r="6.5" fill="currentColor"/>`;
      if (dir !== "tap") dots += `<circle cx="${x0 + gap * i}" cy="${y}" r="11" fill="currentColor" opacity=".14"/>`;
    }
    let arrows = "";
    if (dir === "up") arrows = arrow(132, 72, 132, 30);
    else if (dir === "down") arrows = arrow(132, 30, 132, 72);
    else if (dir === "vertical") arrows = arrow(134, 50, 134, 22) + arrow(134, 54, 134, 82);
    else if (dir === "horizontal") arrows = arrow(78, 82, 44, 82) + arrow(82, 82, 116, 82);
    else if (dir === "left") arrows = arrow(110, 82, 50, 82);
    else if (dir === "right") arrows = arrow(50, 82, 110, 82);
    el.innerHTML = `<svg viewBox="0 0 160 103" role="img" aria-label="${n} 指手势示意">${dots}${arrows}</svg>`;
  });

  // ---------- 进场动画 ----------
  const io = "IntersectionObserver" in window && !reduceMotion
    ? new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); } }), { rootMargin: "0px 0px -8% 0px" })
    : null;
  document.querySelectorAll(".reveal").forEach(el => io ? io.observe(el) : el.classList.add("in"));

  // ---------- 原生 vs 跟手 对比条 ----------
  const lanes = document.querySelectorAll(".lane");
  if (lanes.length) {
    let t0 = performance.now();
    const loop = (now) => {
      const t = ((now - t0) / 1000) % 4; // 4 秒一轮：手指走走停停、再往回
      const finger = t < 1.2 ? t / 1.2 * 0.55 : t < 2 ? 0.55 : t < 2.8 ? 0.55 - (t - 2) / 0.8 * 0.25 : 0.3 - (t - 2.8) / 1.2 * 0.3;
      lanes.forEach(lane => {
        const f = lane.querySelector(".finger"), p = lane.querySelector(".pane");
        f.style.left = `${8 + finger * 84}%`;
        let pos;
        if (lane.dataset.mode === "native") {
          // 原生：识别到手势后播一段固定动画，手指停住、往回都不管
          const k = t < 0.35 ? 0 : t < 0.95 ? (t - 0.35) / 0.6 : t < 3.3 ? 1 : 1 - Math.min(1, (t - 3.3) / 0.45);
          pos = (1 - Math.cos(Math.PI * k)) / 2;
        } else pos = finger / 0.55; // 跟手：画面位置就是手指位置
        p.style.left = `${8 + Math.max(0, Math.min(1, pos)) * 84}%`;
      });
      requestAnimationFrame(loop);
    };
    if (!reduceMotion) requestAnimationFrame(loop);
  }

  // ---------- 首页：可拖动的跟手演示 ----------
  const screen = document.querySelector(".screen");
  if (screen) initDemo(screen);

  function initDemo(screen) {
    // 窗口在桌面上的原位（百分比：左、上、宽、高）与平铺位；p = 0 原位，1 平铺
    const wins = [...screen.querySelectorAll(".win")];
    const labels = [...screen.querySelectorAll(".label")];
    const dim = screen.querySelector(".dim"), spaces = screen.querySelector(".spaces"), hint = screen.querySelector(".hint");
    const fill = document.querySelector(".meter .fill"), readout = document.querySelector(".meter code");
    const src = wins.map(w => w.dataset.src.split(",").map(Number));
    const dst = wins.map(w => w.dataset.dst.split(",").map(Number));
    let p = 0, v = 0, target = 0, dragging = false, animating = false, last = 0, lastY = 0, touched = false;
    let samples = [];

    const lerp = (a, b, t) => a + (b - a) * t;
    const smooth = (e0, e1, x) => { const t = Math.max(0, Math.min(1, (x - e0) / (e1 - e0))); return t * t * (3 - 2 * t); };
    const render = () => {
      const W = screen.clientWidth, H = screen.clientHeight;
      wins.forEach((w, i) => {
        const [sx, sy, sw, sh] = src[i], [dx, dy, dw, dh] = dst[i];
        const x = lerp(sx, dx, p), y = lerp(sy, dy, p), ww = lerp(sw, dw, p), hh = lerp(sh, dh, p);
        w.style.width = `${sw}%`; w.style.height = `${sh}%`;
        w.style.transform = `translate(${x / 100 * W}px, ${y / 100 * H}px) scale(${ww / sw}, ${hh / sh})`;
        const l = labels[i];
        if (l) {
          l.style.left = `${(x + ww / 2)}%`;
          l.style.top = `${y + hh + 1.6}%`;
          l.style.opacity = smooth(0.75, 1, p);
        }
      });
      dim.style.opacity = Math.max(0, Math.min(1, p)) * 0.45;
      spaces.style.opacity = smooth(0.75, 1, p);
      const shown = Math.max(0, Math.min(1, p));
      if (fill) fill.style.width = `${shown * 100}%`;
      if (readout) readout.textContent = `${Math.round(shown * 100)}%`;
    };

    // 弹簧（与 GlideShell 默认的总览弹簧同参数：response 0.35s、阻尼比 0.86），带松手初速度
    const step = (now) => {
      if (!animating) return;
      const dt = Math.min(0.032, (now - last) / 1000); last = now;
      const k = Math.pow(2 * Math.PI / 0.35, 2), c = 2 * 0.86 * Math.sqrt(k);
      v += (-k * (p - target) - c * v) * dt;
      p += v * dt;
      if (Math.abs(p - target) < 0.0005 && Math.abs(v) < 0.01) { p = target; v = 0; animating = false; }
      render();
      if (animating) requestAnimationFrame(step);
    };
    const animateTo = (t, velocity = 0) => {
      target = t; v = velocity;
      if (reduceMotion) { p = t; render(); return; }
      if (!animating) { animating = true; last = performance.now(); requestAnimationFrame(step); }
    };

    // 拖动：往上拖 = 展开（屏幕高度的 55% 拖满），越界有橡皮筋；松手按「位置 + 速度投影」决定
    const travel = () => screen.clientHeight * 0.55;
    // 橡皮筋（iOS 式）：越界 o 时画面只走 0.35 × (1 − 1 / (o × 0.55 / 0.35 + 1))，越拉越拉不动
    const band = (o) => 0.35 * (1 - 1 / (o * 0.55 / 0.35 + 1));
    const rubber = (x) => x < 0 ? -band(-x) : x > 1 ? 1 + band(x - 1) : x;
    let startP = 0, startY = 0;
    screen.addEventListener("pointerdown", e => {
      dragging = true; animating = false; touched = true;
      hint.style.opacity = 0;
      screen.setPointerCapture(e.pointerId); screen.classList.add("dragging");
      startP = p; startY = e.clientY; lastY = e.clientY; samples = [{ t: e.timeStamp, y: e.clientY }];
    });
    screen.addEventListener("pointermove", e => {
      if (!dragging) return;
      const raw = startP + (startY - e.clientY) / travel();
      p = rubber(raw);
      samples.push({ t: e.timeStamp, y: e.clientY });
      if (samples.length > 6) samples.shift();
      render();
    });
    const release = (e) => {
      if (!dragging) return;
      dragging = false; screen.classList.remove("dragging");
      const a = samples[0], b = samples[samples.length - 1];
      const dt = Math.max(1, b.t - a.t) / 1000;
      const vel = b.t - a.t > 120 ? 0 : (a.y - b.y) / travel() / dt; // 停住再松手 = 不甩
      const projected = p + vel * 0.18;
      animateTo(projected > 0.5 ? 1 : 0, vel);
    };
    screen.addEventListener("pointerup", release);
    screen.addEventListener("pointercancel", release);
    screen.addEventListener("keydown", e => {
      if (e.key === "ArrowUp" || e.key === "Enter" || e.key === " ") { animateTo(1); e.preventDefault(); }
      if (e.key === "ArrowDown" || e.key === "Escape") { animateTo(0); e.preventDefault(); }
    });
    addEventListener("resize", render);
    render();

    // 第一次进入视野：自动演示一次（展开、停一下、收起），之后等用户自己拖
    if (!reduceMotion && "IntersectionObserver" in window) {
      const once = new IntersectionObserver(es => {
        if (!es[0].isIntersecting) return;
        once.disconnect();
        setTimeout(() => { if (!touched) animateTo(1); }, 700);
        setTimeout(() => { if (!touched) animateTo(0); }, 2600);
      }, { threshold: 0.6 });
      once.observe(screen);
    }
  }

  // ---------- 教程页：目录高亮当前章节 ----------
  const tocLinks = [...document.querySelectorAll(".toc a[href^='#']")];
  if (tocLinks.length && "IntersectionObserver" in window) {
    const map = new Map(tocLinks.map(a => [a.getAttribute("href").slice(1), a]));
    const spy = new IntersectionObserver(es => {
      es.forEach(e => {
        if (!e.isIntersecting) return;
        tocLinks.forEach(a => a.classList.remove("active"));
        map.get(e.target.id)?.classList.add("active");
      });
    }, { rootMargin: "-20% 0px -70% 0px" });
    map.forEach((_, id) => { const el = document.getElementById(id); if (el) spy.observe(el); });
  }
})();
