"""Delay only the fake auto-start query to reproduce the original CI assertion."""
from pathlib import Path
import hashlib
import json
import subprocess

ROOT = Path("F:/Iris")
WORK = ROOT / "artifacts/t039c/worktree-v026-ci-fix-20261103-1"
SOURCE = "ee2d7118597d07b891066256349098877e65f794"
path = "tests/Iris.App.Tests/Settings/SettingsViewModelTests.cs"
target = WORK / path
original = subprocess.check_output(["git", "show", SOURCE + ":" + path], cwd=WORK).decode("utf-8").replace("\r\n", "\n")
assert target.read_text(encoding="utf-8").replace("\r\n", "\n") == original
start = original.index("    [Fact]\n    public async Task SaveAsync_WhenAutoStartApplyFails_")
end = original.index("    [Fact]\n", start + 12)
method = original[start:end]
method = method.replace("var (vm, settingsService, _, _, autoStart) = CreateSut();",
    "var (vm, settingsService, _, coordinator, autoStart) = CreateSut();")
method = method.replace("        using (vm)\n        {", "        using (vm)\n        using (coordinator)\n        using (var releaseQuery = new ManualResetEventSlim())\n        {")
method = method.replace("            var failAutoStart = true;", "            var failAutoStart = true;\n            var queryBlocked = false;")
method = method.replace('                    throw new InvalidOperationException("Registry error");',
    '                    Volatile.Write(ref queryBlocked, true);\n                    throw new InvalidOperationException("Registry error");')
method = method.replace("            autoStart.IsEnabled().Returns(_ => !failAutoStart);", """            autoStart.IsEnabled().Returns(_ =>
            {
                if (Volatile.Read(ref queryBlocked)) releaseQuery.Wait();
                return !failAutoStart;
            });""")
behavior_start = method.index("            vm.LaunchAtStartup = true;")
behavior_end = method.rfind("        }\n    }")
behavior = method[behavior_start:behavior_end]
method = method[:behavior_start] + "            try\n            {\n" + "\n".join("    " + line if line else "" for line in behavior.rstrip().split("\n")) + "\n            }\n            finally\n            {\n                releaseQuery.Set();\n                await coordinator.WaitForActiveWorkAsync();\n                await vm.WaitForActiveWorkAsync();\n            }\n" + method[behavior_end:]
updated = original[:start] + method + original[end:]
target.write_text(updated, encoding="utf-8", newline="\n")
report = ROOT / "tasks/done/T-039C.evidence/ci-v025-query-counterexample-20261103-1"
report.mkdir(exist_ok=False)
(report / "SettingsViewModelTests.cs").write_bytes(target.read_bytes())
diff = subprocess.check_output(["git", "diff", "--", path], cwd=WORK)
(report / "counterexample.diff").write_bytes(diff)
(report / "binding.json").write_text(json.dumps({"SourceCommit": SOURCE, "ProductChanged": False,
    "FakeQueryBlockedUntilFinally": True, "SourcePath": path, "Bytes": target.stat().st_size,
    "Sha256": hashlib.sha256(target.read_bytes()).hexdigest(), "DiffSha256": hashlib.sha256(diff).hexdigest()}, indent=2)
    + "\n", encoding="utf-8", newline="\n")
print("Prepared controlled query delay; original retry-state assertion is retained.")
