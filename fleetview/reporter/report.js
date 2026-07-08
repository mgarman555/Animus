#!/usr/bin/env node
// FleetView reporter — the bridge between Claude Code hooks (and launchers) and
// the dashboard server. Two modes:
//   hook mode: invoked by a Claude Code hook with the hook JSON on stdin.
//   CLI mode:  node report.js --state ready --title "..." [--project X --agent Y --detail "..."]
//
// CORRECTNESS RULES (do not relax):
//   * Always exit 0.  * Never write to stdout/stderr.  * 1500ms POST timeout.
//   * Swallow every error.  A PreToolUse hook that fails or emits output can
//     BLOCK or DENY Claude's tool calls — this script must be inert on failure.
"use strict";

const http = require("node:http");
const https = require("node:https");
const path = require("node:path");

const BASE = process.env.FLEETVIEW_URL || "http://127.0.0.1:4700";
const TIMEOUT_MS = 1500;

function post(ev) {
  return new Promise((resolve) => {
    let u;
    try { u = new URL(BASE + "/api/event"); } catch { return resolve(); }
    const data = JSON.stringify(ev);
    const lib = u.protocol === "https:" ? https : http;
    const req = lib.request(
      { hostname: u.hostname, port: u.port || (u.protocol === "https:" ? 443 : 80),
        path: u.pathname, method: "POST",
        headers: { "content-type": "application/json", "content-length": Buffer.byteLength(data) },
        timeout: TIMEOUT_MS },
      (res) => { res.resume(); res.on("end", resolve); });
    req.on("error", () => resolve());
    req.on("timeout", () => { req.destroy(); resolve(); });
    req.write(data);
    req.end();
  });
}

// ── CLI mode ──────────────────────────────────────────────────────────
function parseArgs(argv) {
  const o = {};
  for (let i = 0; i < argv.length; i++) {
    if (argv[i].startsWith("--")) o[argv[i].slice(2)] = argv[i + 1] ?? "";
    i++;
  }
  return o;
}

// ── Identity ──────────────────────────────────────────────────────────
function identity(hook) {
  const project =
    process.env.AGENT_PROJECT ||
    (hook && hook.cwd ? path.basename(hook.cwd) : "") ||
    "default";
  const sid = hook && hook.session_id ? String(hook.session_id) : "";
  const agent =
    process.env.AGENT_NAME ||
    (sid ? "cc-" + sid.slice(0, 8) : "cc-unknown");
  return { project, agent, sessionId: sid };
}

// ── Hook mode: map a Claude Code hook payload to an event ──────────────
function clip(s, n) { return String(s == null ? "" : s).replace(/\s+/g, " ").trim().slice(0, n); }

function toolSummary(tool, input) {
  input = input || {};
  switch (tool) {
    case "Bash": return `Bash: ${clip(input.command, 80)}`;
    case "Edit": case "Write": case "Read": case "NotebookEdit":
      return `${tool}: ${clip(input.file_path, 80)}`;
    case "Task": return `Task: ${clip(input.description || input.prompt, 80)}`;
    case "Grep": return `Grep: ${clip(input.pattern, 60)}`;
    case "Glob": return `Glob: ${clip(input.pattern, 60)}`;
    default: return tool || "tool";
  }
}

function mapHook(hook) {
  const { project, agent, sessionId } = identity(hook);
  const base = { v: 1, project, agent, sessionId, source: "claude-code" };
  const name = hook.hook_event_name;
  switch (name) {
    case "SessionStart":
      return { ...base, state: "ready", title: `session started (${hook.source || "startup"})` };
    case "UserPromptSubmit":
      return { ...base, state: "working", title: clip(hook.prompt, 100) };
    case "PreToolUse":
      return { ...base, state: "working", title: toolSummary(hook.tool_name, hook.tool_input) };
    case "Notification":
      return { ...base, state: "needs_input", title: "waiting for you", detail: clip(hook.message, 2000) };
    case "Stop":
      return { ...base, state: "ready", title: "turn finished — ready for input" };
    case "SessionEnd":
      return { ...base, state: "offline", title: `session ended (${hook.reason || "exit"})` };
    default:
      // Unknown / future event: stay non-committal so schema drift degrades gracefully.
      return { ...base, state: "working", title: clip(name || "activity", 60) };
  }
}

function readStdin() {
  return new Promise((resolve) => {
    if (process.stdin.isTTY) return resolve("");
    let buf = "";
    let done = false;
    const finish = () => { if (!done) { done = true; resolve(buf); } };
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (c) => { buf += c; });
    process.stdin.on("end", finish);
    process.stdin.on("error", finish);
    setTimeout(finish, 400); // never hang if stdin stays open
  });
}

(async () => {
  try {
    const args = parseArgs(process.argv.slice(2));
    let ev;
    if (args.state) {
      // CLI mode (launchers / custom agents)
      const project = args.project || process.env.AGENT_PROJECT || "default";
      const agent = args.agent || process.env.AGENT_NAME || "agent";
      ev = {
        v: 1, project, agent, state: args.state,
        title: args.title || "", detail: args.detail || "",
        source: args.source || "manual",
      };
    } else {
      const raw = await readStdin();
      if (!raw.trim()) return;      // nothing to do
      let hook;
      try { hook = JSON.parse(raw); } catch { return; }
      ev = mapHook(hook);
    }
    await post(ev);
  } catch {
    // swallow — inert on any failure
  } finally {
    process.exit(0);
  }
})();
