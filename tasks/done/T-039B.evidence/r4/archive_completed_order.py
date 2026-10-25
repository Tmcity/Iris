"""Remove only the byte-archived active order after all required R4 guest evidence exists."""
from pathlib import Path
import datetime
import json
import subprocess
from inspect_delivery import file_record, json_read

evidence = Path(__file__).resolve().parent
root = evidence.parents[3]
active = json_read(evidence / "active_round.json")
inspection = json_read(evidence / active["GuestInspection"])
assert len(inspection["SandboxVerificationR4"]) == 2
assert all(item["FalseAssertions"] == 0 and item["Failure"] is None and item["GuestCommandExitCode"] == item["StopExitCode"] == 0
    for item in inspection["SandboxVerificationR4"])
assert len(json_read(evidence / "reviewed-guests-r4-1.json")["Frames"]) == 2
assert subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root).decode().strip() == json_read(evidence / active["SourceBinding"])["BaseCommit"]
current = root / "tasks/current_task.md"
snapshot = evidence / active["OrderSnapshot"]
assert current.resolve() == root.resolve() / "tasks/current_task.md"
assert current.read_bytes() == snapshot.read_bytes()
receipt_path = evidence / "active-order-archive-receipt-1.json"
if receipt_path.exists():
    raise FileExistsError(receipt_path)
archived = file_record(snapshot)
current.unlink()
receipt = {"ArchivedOrder": archived, "RemovedOnly": "tasks/current_task.md",
    "RemovedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "GuestInspection": file_record(evidence / active["GuestInspection"]),
    "EngineerViewedImages": file_record(evidence / "reviewed-guests-r4-1.json"),
    "Purpose": "Engineering delivery for independent review. No parent acceptance, BACKLOG change or next task."}
receipt_path.write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps(receipt, ensure_ascii=False, indent=2))
