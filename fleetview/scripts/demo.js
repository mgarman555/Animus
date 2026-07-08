#!/usr/bin/env node
// Drives a fake fleet of events at the running server so you can build/eyeball
// the UI without any real agents. Usage: node scripts/demo.js [--once]
"use strict";

const BASE = process.env.FLEETVIEW_URL || "http://127.0.0.1:4700";
const ONCE = process.argv.includes("--once");

const FLEET = [
  { project: "animus", agents: ["refactorer", "tester", "doc-writer"] },
  { project: "scrapers", agents: ["crawler", "parser"] },
  { project: "webapp", agents: ["frontend", "backend"] },
];

const TOOLS = [
  "Bash: npm test", "Edit: src/server.js", "Read: package.json",
  "Grep: TODO", "Bash: git status", "Write: public/app.js", "Task: refactor module",
];
const QUESTIONS = [
  "Allow Bash command `rm -rf build/ && npm run build`? This will delete the existing build directory before rebuilding from scratch.",
  "The refactor touches the public API in src/core/index.ts — should I keep backward-compatible aliases or remove the old names?",
  "Two migration strategies are possible. Do you want the safe incremental path or the faster big-bang rewrite?",
];

function post(ev) {
  const u = new URL(BASE + "/api/event");
  const data = JSON.stringify({ v: 1, source: "sdk", ...ev });
  const lib = u.protocol === "https:" ? require("node:https") : require("node:http");
  const req = lib.request(
    { hostname: u.hostname, port: u.port, path: u.pathname, method: "POST",
      headers: { "content-type": "application/json", "content-length": Buffer.byteLength(data) } },
    (res) => res.resume());
  req.on("error", () => {});
  req.write(data); req.end();
}

const pick = (a) => a[Math.floor(Math.random() * a.length)];

function seedAll() {
  for (const p of FLEET)
    for (const a of p.agents)
      post({ project: p.project, agent: a, state: "ready", title: "session started (startup)" });
}

function tick() {
  const p = pick(FLEET);
  const agent = pick(p.agents);
  // "scrapers/parser" is our deliberately-silent agent to exercise staleness:
  if (p.project === "scrapers" && agent === "parser") {
    post({ project: p.project, agent, state: "working", title: "parsing 12,455 records (long job)" });
    return;
  }
  const roll = Math.random();
  let ev;
  if (roll < 0.55) ev = { state: "working", title: pick(TOOLS) };
  else if (roll < 0.72) ev = { state: "ready", title: "turn finished — ready for input" };
  else if (roll < 0.88) ev = { state: "needs_input", title: "waiting for you", detail: pick(QUESTIONS) };
  else ev = { state: "error", title: "tool failed", detail: "Bash exited 1: TypeError: cannot read property 'id' of undefined\n  at build.js:42" };
  post({ project: p.project, agent, ...ev });
}

seedAll();
console.log(`Seeded fake fleet at ${BASE}`);
if (!ONCE) {
  console.log("Emitting random transitions every ~1.8s. Ctrl-C to stop.");
  setInterval(tick, 1800);
}
