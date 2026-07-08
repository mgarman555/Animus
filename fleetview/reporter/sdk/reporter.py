"""FleetView status reporter for Python Agent SDK agents. Fire-and-forget, stdlib only.

Identity comes from AGENT_PROJECT / AGENT_NAME env (set by the launcher).

    from reporter import report
    report("working", "starting task")
    report("needs_input", "permission: Bash", "Allow rm -rf build/ ?")
    report("ready", "done")
    report("error", "crashed", str(err))
"""
import json
import os
import threading
import urllib.request

BASE = os.environ.get("FLEETVIEW_URL", "http://127.0.0.1:4700")


def report(state, title="", detail=""):
    payload = json.dumps({
        "v": 1,
        "project": os.environ.get("AGENT_PROJECT", "default"),
        "agent": os.environ.get("AGENT_NAME", "sdk-agent"),
        "state": state, "title": title, "detail": detail, "source": "sdk",
    }).encode()

    def _send():
        try:
            req = urllib.request.Request(
                BASE + "/api/event", data=payload,
                headers={"content-type": "application/json"}, method="POST")
            urllib.request.urlopen(req, timeout=1.5).read()
        except Exception:
            pass  # inert on failure

    threading.Thread(target=_send, daemon=True).start()
