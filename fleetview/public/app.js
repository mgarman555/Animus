"use strict";
// FleetView dashboard client. Receives WS messages, renders project-grouped
// agent cards, fires alerts on transitions into needs_input / error.

const STATE_ORDER = { error: 0, needs_input: 1, working: 2, ready: 3, offline: 4 };
const STATE_LABEL = {
  working: "working", ready: "ready", needs_input: "needs input",
  error: "error", offline: "offline",
};

const agents = new Map();      // key -> record
let projectsMeta = {};         // lowercased project name -> {name, path, color}
let staleMinutes = 10;
let alertsOn = localStorage.getItem("fleetview.alerts") === "1";

const $board = document.getElementById("board");
const $empty = document.getElementById("empty");
const $banner = document.getElementById("banner");
const $conn = document.getElementById("conn");
const $alertsBtn = document.getElementById("alertsBtn");

// ── Alerts ────────────────────────────────────────────────────────────
function refreshAlertsBtn() {
  $alertsBtn.classList.toggle("on", alertsOn);
  $alertsBtn.textContent = alertsOn ? "Alerts on" : "Enable alerts";
}
$alertsBtn.onclick = async () => {
  if (!alertsOn) {
    if ("Notification" in window && Notification.permission !== "granted") {
      try { await Notification.requestPermission(); } catch {}
    }
    alertsOn = true;
  } else {
    alertsOn = false;
  }
  localStorage.setItem("fleetview.alerts", alertsOn ? "1" : "0");
  refreshAlertsBtn();
};
refreshAlertsBtn();

function beep() {
  try {
    const ac = new (window.AudioContext || window.webkitAudioContext)();
    const o = ac.createOscillator(), g = ac.createGain();
    o.connect(g); g.connect(ac.destination);
    o.frequency.value = 660; g.gain.value = 0.05;
    o.start();
    o.frequency.setValueAtTime(880, ac.currentTime + 0.12);
    o.stop(ac.currentTime + 0.24);
  } catch {}
}

function notify(rec) {
  if (!alertsOn) return;
  beep();
  if ("Notification" in window && Notification.permission === "granted") {
    const verb = rec.state === "error" ? "error" : "needs input";
    try {
      new Notification(`${rec.project}/${rec.agent} — ${verb}`, {
        body: rec.detail || rec.title || "", tag: rec.key,
      });
    } catch {}
  }
}

// ── WebSocket ─────────────────────────────────────────────────────────
let ws, reconnectTimer;
function connect() {
  $conn.className = "conn wait"; $conn.title = "connecting";
  ws = new WebSocket(`ws://${location.host}`);
  ws.onopen = () => { $conn.className = "conn on"; $conn.title = "connected"; };
  ws.onclose = () => {
    $conn.className = "conn off"; $conn.title = "disconnected";
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(connect, 2000);
  };
  ws.onerror = () => ws.close();
  ws.onmessage = (e) => handle(JSON.parse(e.data));
}

function handle(msg) {
  if (msg.type === "snapshot") {
    staleMinutes = msg.staleMinutes || 10;
    projectsMeta = msg.projects || {};
    agents.clear();
    for (const a of msg.agents) agents.set(a.key, a);
    renderAll();
  } else if (msg.type === "agent") {
    const prev = agents.get(msg.data.key);
    agents.set(msg.data.key, msg.data);
    if (isAttention(msg.data.state) && (!prev || prev.state !== msg.data.state)) notify(msg.data);
    renderAll();
  } else if (msg.type === "remove") {
    agents.delete(msg.key);
    renderAll();
  }
}

const isAttention = (s) => s === "needs_input" || s === "error";

// ── Rendering ─────────────────────────────────────────────────────────
const expanded = new Set(); // keys with feed open

function renderAll() {
  const list = [...agents.values()];
  $empty.classList.toggle("hidden", list.length > 0);

  // group by project
  const groups = new Map();
  for (const a of list) {
    const pk = a.project.toLowerCase();
    if (!groups.has(pk)) groups.set(pk, []);
    groups.get(pk).push(a);
  }

  // sort projects: those with attention first, then by name
  const projOrder = [...groups.entries()].sort((a, b) => {
    const aw = a[1].some((x) => isAttention(x.state)) ? 0 : 1;
    const bw = b[1].some((x) => isAttention(x.state)) ? 0 : 1;
    return aw - bw || a[0].localeCompare(b[0]);
  });

  const frag = document.createDocumentFragment();
  for (const [pk, ags] of projOrder) {
    frag.appendChild(renderProject(pk, ags));
  }
  // replace board children except #empty
  [...$board.querySelectorAll(".project")].forEach((n) => n.remove());
  $board.appendChild(frag);

  renderBanner(list);
  renderPills(list);
  document.title = attentionCount(list) > 0 ? `(${attentionCount(list)}!) FleetView` : "FleetView";
}

function renderProject(pk, ags) {
  const meta = projectsMeta[pk] || { name: ags[0].project, path: "", color: "" };
  const tpl = document.getElementById("projectTpl").content.cloneNode(true);
  const sec = tpl.querySelector(".project");
  if (meta.color) tpl.querySelector(".pbar").style.background = meta.color;
  tpl.querySelector(".pname").textContent = meta.name;
  tpl.querySelector(".ppath").textContent = meta.path || "";

  const counts = countStates(ags);
  tpl.querySelector(".pcounts").textContent =
    ["error", "needs_input", "working", "ready", "offline"]
      .filter((k) => counts[k]).map((k) => `${counts[k]} ${STATE_LABEL[k]}`).join(" · ");

  ags.sort((a, b) =>
    (STATE_ORDER[a.state] - STATE_ORDER[b.state])
    || ((b.stale ? 1 : 0) - (a.stale ? 1 : 0))
    || a.agent.localeCompare(b.agent));

  const cards = tpl.querySelector(".cards");
  for (const a of ags) cards.appendChild(renderCard(a));
  return sec;
}

function renderCard(a) {
  const tpl = document.getElementById("cardTpl").content.cloneNode(true);
  const card = tpl.querySelector(".card");
  card.className = `card s-${a.state}${a.stale ? " stale" : ""}`;
  card.dataset.key = a.key;
  card.dataset.since = a.since;
  tpl.querySelector(".aname").textContent = a.agent;
  tpl.querySelector(".slabel").textContent = STATE_LABEL[a.state];
  tpl.querySelector(".elapsed").textContent = fmtElapsed(a.since);
  tpl.querySelector(".activity").textContent = a.title || "";

  const callout = tpl.querySelector(".callout");
  if (a.state === "needs_input" || a.state === "error") {
    const label = a.state === "needs_input" ? "Waiting on:" : "Error:";
    callout.innerHTML = `<b>${label}</b> ${escapeHtml(a.detail || a.title || "")}`;
    callout.classList.remove("hidden");
  }

  const feed = tpl.querySelector(".feed");
  if (expanded.has(a.key)) {
    feed.classList.remove("hidden");
    renderFeed(feed, a);
  }

  card.onclick = () => {
    if (expanded.has(a.key)) expanded.delete(a.key); else expanded.add(a.key);
    renderAll();
  };
  return card;
}

function renderFeed(feed, a) {
  feed.innerHTML = "";
  const rows = [...(a.history || [])].reverse().slice(0, 20);
  for (const h of rows) {
    const row = document.createElement("div");
    row.className = "frow";
    row.innerHTML =
      `<span class="ft">${fmtTime(h.ts)}</span>` +
      `<span class="fd ${h.state}"></span>` +
      `<span class="fx">${escapeHtml(h.title || h.state)}</span>`;
    feed.appendChild(row);
  }
}

function renderBanner(list) {
  const att = list.filter((a) => isAttention(a.state));
  if (!att.length) { $banner.classList.add("hidden"); return; }
  $banner.classList.remove("hidden");
  $banner.classList.toggle("err", att.some((a) => a.state === "error"));
  $banner.innerHTML = att
    .sort((a, b) => STATE_ORDER[a.state] - STATE_ORDER[b.state])
    .map((a) => `<span class="item" data-key="${a.key}">${a.state === "error" ? "✕" : "⚠"} ${escapeHtml(a.project)}/${escapeHtml(a.agent)} — ${STATE_LABEL[a.state]}</span>`)
    .join("");
  $banner.onclick = () => {
    const first = $board.querySelector(`.card[data-key="${cssEsc(att[0].key)}"]`);
    if (first) first.scrollIntoView({ behavior: "smooth", block: "center" });
  };
}

function renderPills(list) {
  const c = countStates(list);
  for (const pill of document.querySelectorAll(".pill")) {
    const k = pill.dataset.k;
    pill.querySelector(".n").textContent = c[k] || 0;
    pill.classList.toggle("zero", !c[k]);
  }
}

// ── Helpers ───────────────────────────────────────────────────────────
function countStates(list) {
  const c = {};
  for (const a of list) c[a.state] = (c[a.state] || 0) + 1;
  return c;
}
const attentionCount = (list) => list.filter((a) => isAttention(a.state)).length;

function fmtElapsed(since) {
  if (!since) return "";
  let s = Math.max(0, Math.floor((Date.now() - since) / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  return `${h}h ${m % 60}m`;
}
function fmtTime(ts) {
  const d = new Date(ts);
  return d.toTimeString().slice(0, 8);
}
function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
const cssEsc = (s) => (window.CSS && CSS.escape ? CSS.escape(s) : s.replace(/["\\]/g, "\\$&"));

// tick elapsed timers once a second without a full re-render
setInterval(() => {
  for (const el of document.querySelectorAll(".card")) {
    const since = Number(el.dataset.since);
    const t = el.querySelector(".elapsed");
    if (t && since) t.textContent = fmtElapsed(since);
  }
}, 1000);

connect();
