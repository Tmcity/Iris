# T-036A evidence index

- **Final runnable source/project:** `prototype-final/README.md` and `prototype-final/Prototype.csproj`.
- **Final execution:** `prototype-final-build-3.log`, `prototype-final-run-4.log`, and the corresponding `.exit.txt` files.
- `prototype-final-source-sha256.json`, `prototype-final-binary-sha256.json`, `final-run-summary.json` pin the final prototype.
- `protected-source-sha256.json` pins the unchanged production/tests/CONTRACTS baseline. `received-evidence-sha256.json` records architect handoff inputs.
- `prototype/` is an intermediate archived experiment. **Use `prototype-final/` for final acceptance and reproduction.** Its earlier hash manifests remain historical evidence.
- `stream-baseline-source/` and `streaming-baseline-1.log` preserve the buffering counterexample and cleanup. `independent-stream-review/` is an unchanged copy of the architect's independent original sources/logs.
- `prototype-final/runtime-exact/` contains official source corresponding to the installed runtime VMR commit. Earlier lookup failures and v10.0.0 comparison downloads are separately retained under `runtime-sources/`.
- All earlier numbered `.log` files remain original outputs, including compilation failures and the corrected concurrency-label history. They are not replaced by the final pass.

This delivery is design/prototype evidence only. It does not implement T-036's UI or publish proposed interfaces into Iris.
