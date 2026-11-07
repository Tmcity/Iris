# Windows Computer Use compatibility repair

The official plugin imports WindowsComputerUseClientBase from the bundled @oai/sky 0.7.17 package. The matching implementation is present, but package.json did not export the subpath used by that same official plugin. The public client entry does not export this base class.

Only the exact missing exports mapping was added. The official plugin client and base implementation were kept byte-for-byte unchanged. This retains the original privileged native pipe, app approvals, response metadata, turn cancellation and interruption handling. No helper process was spawned, no alternate protocol client was created, no application was activated or operated, and no security mechanism was changed.

Before: the official bootstrap failed with ERR_PACKAGE_PATH_NOT_EXPORTED.
After package repair and kernel reset: module import succeeds; bootstrap reaches the official native-pipe guard and reports `Computer Use native pipe is unavailable`.

The child environment does not expose nodeRepl.config, nativePipe, createElicitation or the native-pipe directory. Therefore list_apps was not reached in that environment. The parent must verify its own trusted runtime. These missing capabilities cannot be safely fabricated by a package patch.

## Recovery

The original package metadata is `package.json.original`; all raw hashes are in `repair.json`. To restore it, run:

```powershell
python F:\Iris\tasks\T-040.evidence\computer-control-repair-20261107-1\rollback.py
```

The rollback checks that the target still matches the repaired SHA before replacing it. If the runtime package has changed since this repair, it refuses and requires inspection. The package adjustment is local to this runtime build and may be replaced by a future official runtime update.

This report verifies the import fix. It does not claim that Windows control is available; trusted bridge verification remains separate.
