// FleetView status reporter for JS/TS Agent SDK agents. Fire-and-forget.
// Identity comes from AGENT_PROJECT / AGENT_NAME env (set by the launcher).
//
//   import { report } from "./reporter.mjs";
//   report("working", "starting task");
//   report("needs_input", "permission: Bash", "Allow rm -rf build/ ?");
//   report("ready", "done");   report("error", "crashed", String(err));

const BASE = process.env.FLEETVIEW_URL ?? "http://127.0.0.1:4700";

export function report(state, title = "", detail = "") {
  try {
    fetch(`${BASE}/api/event`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        v: 1,
        project: process.env.AGENT_PROJECT ?? "default",
        agent: process.env.AGENT_NAME ?? "sdk-agent",
        state, title, detail, source: "sdk",
      }),
      signal: AbortSignal.timeout(1500),
    }).catch(() => {});
  } catch { /* inert on failure */ }
}
