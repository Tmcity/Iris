"""Classify observations from every preserved R4 guest attempt without inferring success."""
from pathlib import Path
import json
from inspect_delivery import file_record, json_read

evidence = Path(__file__).resolve().parent
target = evidence / "guest-attempt-ledger-1.json"
if target.exists():
    raise FileExistsError(target)
attempts = []
for directory in sorted(evidence.iterdir()):
    if not directory.is_dir() or not directory.name.startswith(("guest-install-r4-", "guest-update-r4-", "guest-network-r4-")):
        continue
    intent_path = directory / "intent.json"
    if not intent_path.exists():
        continue
    intent = json_read(intent_path)
    scenario = intent["Scenario"]
    result_path = directory / "output" / ("update-r4" if scenario == "update" else "") / "result.json"
    result = json_read(result_path) if result_path.exists() else None
    records = result["Checks"] if result is not None else []
    booleans = [item for item in records if type(item["Value"]) is bool]
    commands = {}
    for name in ("start", "execute", "stop", "list-after"):
        path = directory / (name + ".command.json")
        commands[name] = json_read(path)["ExitCode"] if path.exists() else None
    outer = evidence / (directory.name + "-run.command.json")
    execution_log = directory / "execute.log"
    execution = json_read(execution_log) if execution_log.exists() and execution_log.stat().st_size else None
    attempts.append({"Label": directory.name, "Scenario": scenario, "Intent": file_record(intent_path),
        "SandboxId": intent["SandboxId"], "PrefetchRuntimeCache": intent.get("PrefetchRuntimeCache", False),
        "PublicRuntimeDns": intent.get("PublicRuntimeDns", False),
        "RunnerExitCode": json_read(outer)["ExitCode"] if outer.exists() else None,
        "GuestCommandExitCode": execution["ExitCode"] if execution is not None else None,
        "CommandExitCodes": commands, "ObservationRecords": len(records), "BooleanAssertions": len(booleans),
        "FalseAssertions": [item["Name"] for item in booleans if not item["Value"]],
        "Failure": result["Failure"] if result is not None else "No guest result; see original runner/preflight log",
        "Result": file_record(result_path) if result_path.exists() else None,
        "Status": "CompletedScenario" if result is not None and result["Failure"] is None and not any(not item["Value"] for item in booleans)
            and commands["stop"] == 0 and json_read(outer)["ExitCode"] == 0 else "RetainedFailure"})
target.write_text(json.dumps({"Attempts": attempts,
    "Scope": "Network diagnostics are not installation/update proofs; each failed installer had a real nonzero exit and its own Sandbox was stopped. Runtime prefetch does not install a runtime."},
    ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps(attempts, ensure_ascii=False, indent=2))
