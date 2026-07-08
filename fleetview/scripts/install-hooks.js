#!/usr/bin/env node
// Safely merge FleetView's reporter hooks into a project's .claude/settings.local.json.
//
//   node scripts/install-hooks.js <projectPath>     install into one project
//   node scripts/install-hooks.js --all             every project in fleet.json
//   node scripts/install-hooks.js --uninstall <p>   remove FleetView hooks
//   node scripts/install-hooks.js --uninstall --all
//
// Safety: append-only (never clobbers existing hooks), writes a .bak first,
// idempotent via the "fleetview" substring marker, and HARD-REFUSES to touch a
// settings file that is present but not valid JSON.
"use strict";

const fs = require("node:fs");
const path = require("node:path");

const ROOT = path.join(__dirname, "..");
const FLEETVIEW = ROOT.replace(/\\/g, "/"); // forward slashes dodge Windows JSON-escape bugs
const MARKER = "fleetview";
const EVENTS = ["SessionStart", "UserPromptSubmit", "PreToolUse", "Notification", "Stop", "SessionEnd"];

function reporterCmd() {
  const p = `${FLEETVIEW}/reporter/report.js`;
  // quote only if the path has spaces
  return /\s/.test(p) ? `node "${p}"` : `node ${p}`;
}

function entryFor(event) {
  const hook = { type: "command", command: reporterCmd(), timeout: 5 };
  return event === "PreToolUse"
    ? { matcher: "*", hooks: [hook] }
    : { hooks: [hook] };
}

function hasFleetview(group) {
  return Array.isArray(group) && group.some((g) =>
    Array.isArray(g.hooks) && g.hooks.some((h) => typeof h.command === "string" && h.command.includes(MARKER)));
}

function load(settingsPath) {
  if (!fs.existsSync(settingsPath)) return {};
  const text = fs.readFileSync(settingsPath, "utf8");
  if (!text.trim()) return {};
  try {
    return JSON.parse(text);
  } catch (e) {
    throw new Error(`${settingsPath} is not valid JSON (${e.message}). Refusing to modify it. Fix or remove it, then retry.`);
  }
}

function install(projectPath, { uninstall = false } = {}) {
  const dir = path.join(projectPath, ".claude");
  const file = path.join(dir, "settings.local.json");
  let settings;
  try { settings = load(file); }
  catch (e) { console.error("✗ " + e.message); return false; }

  settings.hooks = settings.hooks || {};
  let changed = false;

  for (const event of EVENTS) {
    const group = settings.hooks[event] || [];
    if (uninstall) {
      const filtered = group.filter((g) =>
        !(Array.isArray(g.hooks) && g.hooks.some((h) => typeof h.command === "string" && h.command.includes(MARKER))));
      if (filtered.length !== group.length) {
        if (filtered.length) settings.hooks[event] = filtered; else delete settings.hooks[event];
        changed = true;
      }
    } else {
      if (hasFleetview(group)) continue; // already installed
      settings.hooks[event] = group.concat(entryFor(event));
      changed = true;
    }
  }
  if (settings.hooks && Object.keys(settings.hooks).length === 0) delete settings.hooks;

  if (!changed) {
    console.log(`• ${projectPath} — ${uninstall ? "no FleetView hooks to remove" : "already installed"}`);
    return true;
  }

  fs.mkdirSync(dir, { recursive: true });
  if (fs.existsSync(file)) fs.copyFileSync(file, file + ".bak");
  fs.writeFileSync(file, JSON.stringify(settings, null, 2) + "\n");
  console.log(`✓ ${projectPath} — ${uninstall ? "removed" : "installed"} FleetView hooks (${path.relative(projectPath, file)})`);
  return true;
}

function fleetProjects() {
  const fp = path.join(ROOT, "fleet.json");
  if (!fs.existsSync(fp)) { console.error("✗ fleet.json not found — needed for --all."); process.exit(1); }
  let fleet;
  try { fleet = JSON.parse(fs.readFileSync(fp, "utf8")); }
  catch (e) { console.error(`✗ fleet.json invalid JSON: ${e.message}`); process.exit(1); }
  return (fleet.projects || []).map((p) => p.path).filter(Boolean);
}

// ── main ──────────────────────────────────────────────────────────────
const argv = process.argv.slice(2);
const uninstall = argv.includes("--uninstall");
const all = argv.includes("--all");
const targets = all ? fleetProjects() : argv.filter((a) => !a.startsWith("--"));

if (!targets.length) {
  console.error("Usage: node scripts/install-hooks.js <projectPath> | --all  [--uninstall]");
  process.exit(1);
}
let ok = true;
for (const t of targets) ok = install(path.resolve(t), { uninstall }) && ok;
process.exit(ok ? 0 : 1);
