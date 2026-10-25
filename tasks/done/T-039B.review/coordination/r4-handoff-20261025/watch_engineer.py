"""Read incremental visible engineer activity; never submit or restart work."""
from pathlib import Path
import datetime
import importlib.util
import json
import sys
import time

sys.dont_write_bytecode = True
sys.stdout.reconfigure(encoding="utf-8")
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
spec = importlib.util.spec_from_file_location("iris_user_bridge", ROOT / ".codex/codex_threads.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)
connection = bridge.Connection(bridge.desktop_cli())
cursor_path = HERE / "watch-cursor.json"
cursor = json.loads(cursor_path.read_text("utf-8")) if cursor_path.exists() else {}
try:
    # Optional bounded observation. A quiet snapshot never authorizes restarting
    # the engineer; only explicit terminal state can end that execution.
    seconds = min(25, max(0, int(sys.argv[1]))) if len(sys.argv) > 1 else 0
    deadline = time.monotonic() + seconds
    while True:
        thread = connection.read("01a16ef2-a0bb-71a1-8230-57165c05cc5b")
        turn = thread["turns"][-1]
        start = cursor.get("itemCount", 0) if cursor.get("turnId") == turn["id"] else 0
        items = turn.get("items", [])
        visible = []
        for item in items[start:]:
            if item.get("type") == "agentMessage":
                visible.append({"type": "agentMessage", "text": item.get("text", "")})
            elif item.get("type") == "commandExecution":
                visible.append({k: v for k, v in item.items() if k in {"id", "type", "command", "status", "exitCode"}})
            elif item.get("type") == "fileChange":
                visible.append(dict(type="fileChange", id=item.get("id"), status=item.get("status"), paths=[p.get("path") for p in item.get("changes", [])]))
        if visible or turn.get("completedAt") is not None or time.monotonic() >= deadline:
            break
        time.sleep(min(5, max(0, deadline - time.monotonic())))
    now = datetime.datetime.now(datetime.timezone.utc)
    result = dict(observedAt=now.isoformat(), threadId=thread["id"], turnId=turn["id"], status=turn.get("status"), completedAt=turn.get("completedAt"), error=turn.get("error"), previousItemCount=start, itemCount=len(items), newVisibleItems=visible)
    with (HERE / ("watch-" + now.strftime("%Y%m%dT%H%M%S%fZ") + ".json")).open("x", encoding="utf-8") as output:
        json.dump(result, output, indent=2, ensure_ascii=False)
    cursor_path.write_text(json.dumps(dict(turnId=turn["id"], itemCount=len(items))), encoding="utf-8")
    if len(visible) > 8:
        result["newVisibleItems"] = [x for x in visible if x["type"] == "agentMessage"] + [x for x in visible if x["type"] != "agentMessage"][-5:]
    print(json.dumps(result, indent=2, ensure_ascii=False))
finally:
    connection.close()
