#!/usr/bin/env node
// FleetView server — event ingestion, state, WebSocket broadcast, static dashboard.
// Single dependency: ws. Binds 127.0.0.1 only.
"use strict";

const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const { WebSocketServer } = require("ws");

const ROOT = path.join(__dirname, "..");
const PUBLIC_DIR = path.join(ROOT, "public");
const LOGS_DIR = path.join(ROOT, "logs");

const STATES = new Set(["working", "ready", "needs_input", "error", "offline"]);
const SOURCES = new Set(["claude-code", "sdk", "manual"]);
const HISTORY_MAX = 25;
const PRUNE_OFFLINE_MS = 60 * 60 * 1000; // undeclared agents offline > 1h get removed

// ── Config ────────────────────────────────────────────────────────────
let fleet = { port: 4700, staleMinutes: 10, projects: [] };
const fleetPath = path.join(ROOT, "fleet.json");
try {
  if (fs.existsSync(fleetPath)) {
    fleet = { ...fleet, ...JSON.parse(fs.readFileSync(fleetPath, "utf8")) };
  }
} catch (e) {
  console.error(`fleet.json is invalid JSON (${e.message}) — starting with defaults.`);
}
const PORT = Number(process.env.FLEETVIEW_PORT) || fleet.port || 4700;
const STALE_MS = (Number(fleet.staleMinutes) > 0 ? Number(fleet.staleMinutes) : 10) * 60 * 1000;

// ── State ─────────────────────────────────────────────────────────────
/** key "project/agent" (lowercased) -> agent record */
const agents = new Map();
/** keys declared in fleet.json — exempt from pruning */
const declared = new Set();

function keyOf(project, agent) {
  return `${project}/${agent}`.toLowerCase();
}

function normalize(raw, now) {
  if (!raw || typeof raw !== "object") return null;
  const project = String(raw.project || "").trim();
  const agent = String(raw.agent || "").trim();
  const state = String(raw.state || "").trim();
  if (!project || !agent || !STATES.has(state)) return null;
  return {
    project,
    agent,
    state,
    title: String(raw.title || "").slice(0, 120),
    detail: String(raw.detail || "").slice(0, 2000),
    source: SOURCES.has(raw.source) ? raw.source : "manual",
    sessionId: raw.sessionId ? String(raw.sessionId).slice(0, 64) : "",
    ts: now, // server clock is authoritative
  };
}

function applyEvent(ev, { log = true, broadcast = true } = {}) {
  const key = keyOf(ev.project, ev.agent);
  let rec = agents.get(key);
  if (!rec) {
    rec = {
      key, project: ev.project, agent: ev.agent,
      state: "offline", title: "", detail: "", source: ev.source, sessionId: "",
      since: ev.ts, lastSeen: ev.ts, stale: false, history: [],
    };
    agents.set(key, rec);
  }
  if (ev.state !== rec.state) rec.since = ev.ts; // elapsed timer resets on state CHANGE only
  rec.state = ev.state;
  rec.title = ev.title;
  rec.detail = ev.detail;
  rec.source = ev.source;
  if (ev.sessionId) rec.sessionId = ev.sessionId;
  rec.lastSeen = ev.ts;
  rec.stale = false; // any sign of life clears staleness
  rec.history.push({ ts: ev.ts, state: ev.state, title: ev.title });
  if (rec.history.length > HISTORY_MAX) rec.history.splice(0, rec.history.length - HISTORY_MAX);

  if (log) appendLog(ev);
  if (broadcast) broadcastAgent(rec);
  return rec;
}

// ── JSONL persistence ─────────────────────────────────────────────────
function logFile(d) {
  return path.join(LOGS_DIR, `events-${d.toISOString().slice(0, 10)}.jsonl`);
}

function appendLog(ev) {
  fs.mkdir(LOGS_DIR, { recursive: true }, (err) => {
    if (err) return;
    fs.appendFile(logFile(new Date()), JSON.stringify(ev) + "\n", () => {});
  });
}

function replayTodayLog() {
  const file = logFile(new Date());
  let lines;
  try {
    lines = fs.readFileSync(file, "utf8").split("\n");
  } catch {
    return;
  }
  let count = 0;
  for (const line of lines) {
    if (!line.trim()) continue;
    try {
      const raw = JSON.parse(line);
      const ev = normalize(raw, Number(new Date(raw.ts)) || Date.now());
      if (ev) { applyEvent(ev, { log: false, broadcast: false }); count++; }
    } catch { /* skip bad lines */ }
  }
  if (count) console.log(`Replayed ${count} events from ${path.basename(file)}`);
  // After a restart, anything still "working" but long-silent is really gone.
  const now = Date.now();
  for (const rec of agents.values()) {
    if (rec.state !== "offline" && now - rec.lastSeen > STALE_MS) {
      rec.state = "offline";
      rec.title = "no signal since server restart";
      rec.since = rec.lastSeen;
    }
  }
}

// ── Fleet seeding ─────────────────────────────────────────────────────
function seedFleet() {
  const now = Date.now();
  for (const proj of fleet.projects || []) {
    for (const ag of proj.agents || []) {
      if (!proj.name || !ag.name) continue;
      const key = keyOf(proj.name, ag.name);
      declared.add(key);
      if (!agents.has(key)) {
        agents.set(key, {
          key, project: proj.name, agent: ag.name,
          state: "offline", title: "not started", detail: "",
          source: ag.type === "claude-code" ? "claude-code" : ag.type === "sdk" ? "sdk" : "manual",
          sessionId: "", since: now, lastSeen: now, stale: false, history: [],
        });
      }
    }
  }
}

// projects meta (color/path) for the UI, from fleet.json
function projectsMeta() {
  const meta = {};
  for (const proj of fleet.projects || []) {
    if (proj.name) meta[proj.name.toLowerCase()] = { name: proj.name, path: proj.path || "", color: proj.color || "" };
  }
  return meta;
}

// ── WebSocket ─────────────────────────────────────────────────────────
let wss;

function snapshotMsg() {
  return JSON.stringify({
    type: "snapshot",
    staleMinutes: STALE_MS / 60000,
    projects: projectsMeta(),
    agents: [...agents.values()],
  });
}

function broadcast(msg) {
  if (!wss) return;
  for (const client of wss.clients) {
    if (client.readyState === 1) client.send(msg);
  }
}

function broadcastAgent(rec) {
  broadcast(JSON.stringify({ type: "agent", data: rec }));
}

// ── Staleness + pruning ───────────────────────────────────────────────
setInterval(() => {
  const now = Date.now();
  for (const [key, rec] of agents) {
    if (rec.state === "working" && !rec.stale && now - rec.lastSeen > STALE_MS) {
      rec.stale = true;
      broadcastAgent(rec);
    }
    if (rec.state === "offline" && !declared.has(key) && now - rec.lastSeen > PRUNE_OFFLINE_MS) {
      agents.delete(key);
      broadcast(JSON.stringify({ type: "remove", key }));
    }
  }
}, 30 * 1000);

// ── HTTP ──────────────────────────────────────────────────────────────
const MIME = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".json": "application/json",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
};

function serveStatic(req, res) {
  let urlPath = decodeURIComponent(new URL(req.url, "http://x").pathname);
  if (urlPath === "/") urlPath = "/index.html";
  const file = path.normalize(path.join(PUBLIC_DIR, urlPath));
  if (!file.startsWith(PUBLIC_DIR)) {
    res.writeHead(403); return res.end("forbidden");
  }
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404); return res.end("not found"); }
    res.writeHead(200, { "content-type": MIME[path.extname(file)] || "application/octet-stream" });
    res.end(data);
  });
}

const server = http.createServer((req, res) => {
  if (req.method === "POST" && req.url === "/api/event") {
    let body = "";
    req.on("data", (c) => { body += c; if (body.length > 64 * 1024) req.destroy(); });
    req.on("end", () => {
      let raw;
      try { raw = JSON.parse(body); } catch {
        res.writeHead(400); return res.end("invalid json");
      }
      const ev = normalize(raw, Date.now());
      if (!ev) { res.writeHead(400); return res.end("need project, agent, valid state"); }
      applyEvent(ev);
      res.writeHead(204); res.end();
    });
    return;
  }
  if (req.method === "GET" && req.url === "/api/state") {
    res.writeHead(200, { "content-type": "application/json" });
    return res.end(snapshotMsg());
  }
  if (req.method === "GET") return serveStatic(req, res);
  res.writeHead(405); res.end();
});

wss = new WebSocketServer({ server });
wss.on("connection", (ws) => {
  ws.isAlive = true;
  ws.on("pong", () => { ws.isAlive = true; });
  ws.send(snapshotMsg());
});
setInterval(() => {
  for (const ws of wss.clients) {
    if (!ws.isAlive) { ws.terminate(); continue; }
    ws.isAlive = false;
    ws.ping();
  }
}, 30 * 1000);

server.on("error", (err) => {
  if (err.code === "EADDRINUSE") {
    console.error(`FleetView already running or port ${PORT} busy — set FLEETVIEW_PORT or edit fleet.json.`);
    process.exit(1);
  }
  throw err;
});

replayTodayLog();
seedFleet();
server.listen(PORT, "127.0.0.1", () => {
  console.log(`FleetView on http://127.0.0.1:${PORT}  (${agents.size} agents, stale after ${STALE_MS / 60000}m)`);
});
