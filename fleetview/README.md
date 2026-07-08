# FleetView

A local dashboard for watching a fleet of AI agents work across multiple projects.
Each agent shows a traffic-light status so you can glance at one window and know
where every long-running agent stands:

| Light | State | Meaning |
|---|---|---|
| 🟢 green | `ready` | Turn finished — good to hand it the next task |
| 🟡 yellow (pulsing) | `needs_input` | Waiting on you — the question/permission is shown on the card |
| 🔴 red | `error` | Something failed — the error text is shown on the card |
| 🔵 blue (pulsing ring) | `working` | Actively running (with the current tool/activity) |
| ⚪ gray (hollow) | `offline` | Not started / session ended |

Agents are grouped by project. Anything needing attention floats to the top and
raises the attention banner (plus an optional browser notification + beep).

It's a single Node process (one dependency: `ws`) serving a vanilla HTML/JS page —
no build step. Works with **Claude Code sessions** (via hooks) and **Agent SDK
agents** (via a tiny reporter helper), reporting through one shared HTTP endpoint.

---

## Quick start

```bash
npm install
cp fleet.example.json fleet.json     # then edit it (see below)
npm start                            # dashboard at http://127.0.0.1:4700
```

Kick the tyres with a fake fleet before wiring real agents:

```bash
npm run demo                         # emits fake agents/transitions every ~2s
```

Open `http://127.0.0.1:4700`, click **Enable alerts** once (browser notifications
need a click to grant permission), and watch it move.

---

## How it fits together

```
  Claude Code session ──hooks──▶ reporter/report.js ──┐
  Agent SDK agent ──reporter.mjs/.py──────────────────┤  POST /api/event
  launcher breadcrumbs ──report.js --state ...────────┘        │
                                                               ▼
                                                    server/server.js
                                              (state + JSONL log + WebSocket)
                                                               │
                                                               ▼
                                                    public/ dashboard (browser)
```

Everything speaks one event schema:

```json
POST /api/event
{ "project":"animus", "agent":"refactorer",
  "state":"working|ready|needs_input|error|offline",
  "title":"Bash: npm test", "detail":"question or error text",
  "source":"claude-code|sdk|manual" }
```

`project`, `agent`, `state` are required. The server timestamps everything itself.
An agent is identified by `project/agent`.

---

## Defining your fleet (`fleet.json`)

```json
{
  "port": 4700,
  "staleMinutes": 10,
  "projects": [
    { "name": "animus", "path": "C:/dev/Animus", "color": "#0E639C",
      "agents": [
        { "name": "refactorer", "type": "claude-code",
          "args": ["--permission-mode", "acceptEdits"],
          "prompt": "Continue the refactor plan in PLAN.md" },
        { "name": "tester", "type": "claude-code" },
        { "name": "crawler", "type": "sdk", "cmd": "python agents/crawler.py", "cwd": "C:/dev/scrapers" }
      ] }
  ]
}
```

- `type: "claude-code"` — launched as `claude <args> <prompt>` in the project dir.
- `type: "sdk"` / `"custom"` — the `cmd` line is run (in `cwd` if given, else the project `path`).
- Declared agents show up on the dashboard as gray "not started" cards before launch,
  so you always see the whole fleet skeleton.
- `staleMinutes`: a `working` agent that goes silent this long is flagged **stale?**
  (hooks can't see crashes/sleep — this is the safety net).

---

## Running a group (Windows)

```powershell
.\scripts\Start-Fleet.ps1                  # install hooks + start server + open browser + launch all agents
.\scripts\Start-Fleet.ps1 -Project animus  # just one group
```

`Start-Fleet.ps1`:
1. runs `install-hooks.js --all` (idempotent — safe every time),
2. starts the dashboard server if it isn't already up, opens the browser,
3. opens **one Windows Terminal tab per project**, split into **one pane per agent**
   (falls back to separate PowerShell windows if `wt.exe` isn't installed).

Each agent comes up with `AGENT_PROJECT` / `AGENT_NAME` / `FLEETVIEW_URL` set, so it
reports under the right card. You still type responses in the terminal panes — the
dashboard is for *seeing* status, not driving it.

To launch a single agent yourself: `.\scripts\Start-Agent.ps1 -Project animus -Agent tester`.

---

## Claude Code integration (hooks)

Report status by installing FleetView's hooks into a project:

```bash
node scripts/install-hooks.js C:/dev/Animus     # one project
node scripts/install-hooks.js --all             # every project in fleet.json
node scripts/install-hooks.js --uninstall C:/dev/Animus
```

This merges six hooks into the project's `.claude/settings.local.json` (gitignored by
convention, so teammates never see it). It is **append-only** — your existing hooks and
permissions are preserved, a `.bak` is written first, it's idempotent, and it refuses to
touch a settings file that isn't valid JSON. Because it installs into the project itself,
Claude Code sessions you start **manually** in that project also report.

Hook → state mapping (all handled by `reporter/report.js`):

| Hook | State | Shows |
|---|---|---|
| SessionStart | ready | "session started" |
| UserPromptSubmit | working | your prompt (first 100 chars) |
| PreToolUse | working | the tool + target (`Bash: npm test`, `Edit: src/x.ts`, …) |
| Notification | **needs_input** | the permission / idle prompt text → the yellow "Waiting on" box |
| Stop | **ready** | "turn finished — ready for input" |
| SessionEnd | offline | "session ended" |

> **Why green means "ready," not "healthy."** Green is the *last known good* state.
> A machine sleep or a killed terminal can't fire a hook, so the server's staleness
> timer is the backstop that catches silent deaths.

### Manual sessions without the launcher

If you just open `claude` in a project (no `AGENT_*` env), the agent is identified by
`<folder-name>/cc-<session-id-prefix>` — it still shows up, just with an auto name.

---

## Agent SDK integration

Import the reporter and call it at lifecycle points. Fire-and-forget, 1.5s timeout,
never throws.

**JavaScript / TypeScript** (`reporter/sdk/reporter.mjs`):
```js
import { report } from "fleetview/reporter/sdk/reporter.mjs";
report("working", "starting task: scrape catalogue");
// on each tool_use block seen in the message stream (throttle to ~1/sec):
report("working", "Tool: fetch");
// when you pause for a human decision / permission:
report("needs_input", "permission: write to disk", "Overwrite output/data.json?");
report("ready", "done: 1,204 records");   // on the result message
// wrap the run in try/catch:
report("error", "run failed", String(err));
```

**Python** (`reporter/sdk/reporter.py`) — same API, stdlib only:
```python
from reporter import report
report("working", "starting task")
report("needs_input", "permission: shell", "Allow rm -rf build/ ?")
report("ready", "done")
report("error", "crashed", str(exc))
```

Both read `AGENT_PROJECT` / `AGENT_NAME` / `FLEETVIEW_URL` from the env the launcher
sets. When launched via `Start-Agent.ps1`, the launcher also emits an `offline` event
on process exit — so a crash your `try/except` misses still clears the card.

---

## Files

```
server/server.js            HTTP + WebSocket + state + staleness + JSONL log
reporter/report.js          Claude Code hook bridge + CLI mode (--state ...)
reporter/sdk/reporter.mjs   JS Agent SDK helper
reporter/sdk/reporter.py    Python Agent SDK helper
public/{index.html,style.css,app.js}   the dashboard
scripts/install-hooks.js    safe hook merge into a project's settings.local.json
scripts/Start-Fleet.ps1     launch dashboard + all agent groups
scripts/Start-Agent.ps1     launch one agent with identity env set
scripts/demo.js             fake fleet for UI development
hooks/hooks.template.json   the hook block install-hooks.js merges in
fleet.example.json          copy to fleet.json and edit
```

## Notes / limits

- Binds `127.0.0.1` only — local-only, no auth, no firewall prompt.
- History is the last 25 events per agent (dashboard shows 20). Full history is
  appended to `logs/events-YYYY-MM-DD.jsonl`; the server replays today's log on
  restart so a mid-day restart doesn't lose state.
- Designed for a handful of projects × a few agents each. Everything is in-memory.
- Cross-platform server/reporter; the launcher scripts are Windows PowerShell.
