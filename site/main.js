const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

/* Hạt lấp lánh hình ngôi sao bốn cánh, lấy từ logo */
(() => {
  const canvas = document.getElementById("sparkles");
  if (!canvas || reduceMotion) return;
  const ctx = canvas.getContext("2d");
  let w, h, stars;
  const resize = () => {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    w = canvas.width = innerWidth * dpr;
    h = canvas.height = innerHeight * dpr;
    canvas.style.width = innerWidth + "px";
    canvas.style.height = innerHeight + "px";
    const count = Math.round((innerWidth * innerHeight) / 26000);
    stars = Array.from({ length: count }, () => ({
      x: Math.random() * w, y: Math.random() * h,
      r: (Math.random() * 3 + 1.5) * dpr,
      phase: Math.random() * Math.PI * 2,
      speed: 0.6 + Math.random() * 1.2,
      drift: (Math.random() * 0.15 + 0.05) * dpr,
    }));
  };
  const star = (x, y, r) => {
    ctx.beginPath();
    ctx.moveTo(x, y - r * 2);
    ctx.quadraticCurveTo(x, y, x + r * 2, y);
    ctx.quadraticCurveTo(x, y, x, y + r * 2);
    ctx.quadraticCurveTo(x, y, x - r * 2, y);
    ctx.quadraticCurveTo(x, y, x, y - r * 2);
    ctx.fill();
  };
  const tick = (t) => {
    ctx.clearRect(0, 0, w, h);
    for (const s of stars) {
      const a = (Math.sin(t / 1000 * s.speed + s.phase) + 1) / 2;
      s.y -= s.drift;
      if (s.y < -20) { s.y = h + 20; s.x = Math.random() * w; }
      ctx.fillStyle = `rgba(226, 205, 255, ${0.15 + a * 0.6})`;
      star(s.x, s.y, s.r * (0.6 + a * 0.5));
    }
    requestAnimationFrame(tick);
  };
  resize();
  addEventListener("resize", resize);
  requestAnimationFrame(tick);
})();

/* Màn quét mô phỏng: hiện từng dòng, đếm dung lượng, tự chọn mục an toàn, quét sạch rồi lặp lại */
(() => {
  const list = document.getElementById("rows");
  if (!list) return;
  const items = [
    { name: "Xcode DerivedData", path: "~/Library/Developer/Xcode/DerivedData", size: 14.2, level: "safe", why: "Xcode builds this again the next time you build." },
    { name: "npm cache", path: "~/.npm/_cacache", size: 3.1, level: "safe", why: "npm downloads these packages again when needed." },
    { name: "Slack cache", path: "~/Library/Caches/com.tinyspeck.slackmacgap", size: 1.4, level: "safe", why: "Slack recreates its cache on next launch." },
    { name: "Xcode_16.dmg", path: "~/Downloads", size: 3.6, level: "review", why: "An installer you downloaded 8 months ago. Not preselected." },
    { name: "iPhone backup", path: "~/Library/Application Support/MobileSync/Backup", size: 22.0, level: "risky", why: "This may be your only copy. Never preselected." },
  ];
  const labels = { safe: "Safe", review: "Review", risky: "Risky" };
  const status = document.getElementById("scan-status");
  const bar = document.getElementById("scan-bar");
  const total = document.getElementById("total");
  const cleanBtn = document.getElementById("clean-btn");
  const broom = document.getElementById("broom");
  const check = '<svg viewBox="0 0 16 16" fill="none" stroke="#fff" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M3 8.5l3 3 7-7"/></svg>';
  let timers = [];
  let userTouched = false;
  const later = (fn, ms) => timers.push(setTimeout(fn, reduceMotion ? 0 : ms));
  const clearTimers = () => { timers.forEach(clearTimeout); timers = []; };

  const animateNumber = (el, to) => {
    const from = parseFloat(el.dataset.value || "0");
    el.dataset.value = to;
    if (reduceMotion) { el.textContent = to.toFixed(1) + " GB"; return; }
    const start = performance.now();
    const step = (now) => {
      const p = Math.min(Math.max((now - start) / 600, 0), 1);
      const v = from + (to - from) * (1 - Math.pow(1 - p, 3));
      el.textContent = v.toFixed(1) + " GB";
      if (p < 1) requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  };
  const updateTotal = () => {
    const sum = [...list.querySelectorAll('.row[aria-pressed="true"]')].reduce((s, r) => s + parseFloat(r.dataset.size), 0);
    animateNumber(total, sum);
    cleanBtn.disabled = sum === 0;
  };

  const build = () => {
    list.innerHTML = "";
    for (const it of items) {
      const li = document.createElement("li");
      li.innerHTML = `<button class="row" type="button" aria-pressed="false" data-size="${it.size}" data-level="${it.level}">
        <span class="check">${check}</span>
        <span class="row-main"><span class="row-name">${it.name}<span class="tag ${it.level}">${labels[it.level]}</span></span>
        <span class="row-path">${it.path}</span><span class="row-why">${it.why}</span></span>
        <span class="row-size">${it.size.toFixed(1)} GB</span></button>`;
      const row = li.firstElementChild;
      row.addEventListener("click", () => {
        userTouched = true;
        clearTimers();
        row.setAttribute("aria-pressed", row.getAttribute("aria-pressed") === "true" ? "false" : "true");
        updateTotal();
      });
      list.appendChild(li);
    }
  };

  const run = () => {
    clearTimers();
    build();
    total.dataset.value = "0";
    total.textContent = "0.0 GB";
    cleanBtn.disabled = true;
    status.textContent = "Scanning…";
    bar.style.width = "0";
    const rows = [...list.querySelectorAll(".row")];
    rows.forEach((row, i) => later(() => {
      row.classList.add("in");
      bar.style.width = ((i + 1) / rows.length) * 100 + "%";
    }, 500 + i * 450));
    const done = 500 + rows.length * 450 + 300;
    later(() => { status.textContent = "Scan complete · only safe items selected"; }, done);
    rows.forEach((row, i) => {
      if (row.dataset.level === "safe") later(() => { row.setAttribute("aria-pressed", "true"); updateTotal(); }, done + 400 + i * 250);
    });
    later(sweep, done + 3800);
  };

  const sweep = () => {
    const picked = [...list.querySelectorAll('.row[aria-pressed="true"]')];
    if (!picked.length) return;
    clearTimers();
    status.textContent = "Moving to Trash…";
    broom.classList.remove("go");
    void broom.offsetWidth;
    broom.classList.add("go");
    picked.forEach((row, i) => later(() => row.classList.add("swept"), 150 + i * 120));
    // Thu gọn chỗ trống sau khi dòng bay đi, các dòng còn lại trượt lên.
    picked.forEach((row, i) => later(() => row.parentElement.classList.add("gone"), 700 + i * 120));
    const freed = picked.reduce((s, r) => s + parseFloat(r.dataset.size), 0);
    later(() => {
      status.textContent = `Freed ${freed.toFixed(1)} GB · restorable from History`;
      animateNumber(total, 0);
      cleanBtn.disabled = true;
    }, 900);
    if (!userTouched) later(run, 4200);
  };

  cleanBtn.addEventListener("click", () => { userTouched = true; sweep(); });
  document.getElementById("rescan-btn")?.addEventListener("click", () => { userTouched = false; run(); });

  // Chỉ chạy khi màn quét nằm trong khung nhìn.
  let started = false;
  new IntersectionObserver((entries) => {
    if (entries[0].isIntersecting && !started) { started = true; run(); }
  }, { threshold: 0.3 }).observe(list);
})();

/* Trình chiếu ảnh chụp, tự chuyển mỗi 6 giây */
(() => {
  const tabs = [...document.querySelectorAll(".tab")];
  const imgs = [...document.querySelectorAll(".frame img")];
  if (!tabs.length) return;
  let index = 0, timer;
  const show = (i, auto) => {
    index = i;
    tabs.forEach((t, k) => {
      t.setAttribute("aria-selected", k === i ? "true" : "false");
      t.tabIndex = k === i ? 0 : -1;
      const p = t.querySelector(".progress");
      if (p) { p.style.animation = "none"; void p.offsetWidth; p.style.animation = ""; }
    });
    imgs.forEach((im, k) => im.classList.toggle("active", k === i));
    clearTimeout(timer);
    if (auto !== false && !reduceMotion) timer = setTimeout(() => show((index + 1) % tabs.length), 6000);
  };
  tabs.forEach((t, i) => {
    t.addEventListener("click", () => show(i));
    t.addEventListener("keydown", (e) => {
      if (e.key === "ArrowDown" || e.key === "ArrowRight") { e.preventDefault(); show((i + 1) % tabs.length); tabs[index].focus(); }
      if (e.key === "ArrowUp" || e.key === "ArrowLeft") { e.preventDefault(); show((i - 1 + tabs.length) % tabs.length); tabs[index].focus(); }
    });
  });
  show(0);
})();

/* Hiện dần khi cuộn tới */
(() => {
  const els = document.querySelectorAll(".reveal");
  if (reduceMotion) { els.forEach((el) => el.classList.add("visible")); return; }
  const io = new IntersectionObserver((entries) => {
    for (const e of entries) if (e.isIntersecting) { e.target.classList.add("visible"); io.unobserve(e.target); }
  }, { threshold: 0.15 });
  els.forEach((el, i) => { el.style.transitionDelay = (el.dataset.delay || 0) + "ms"; io.observe(el); });
})();

document.querySelectorAll("[data-year]").forEach((el) => { el.textContent = new Date().getFullYear(); });
