# Current-chat Windows control compatibility fix

The user reports that other chats on the same host control apps normally, and confirms Any App is enabled. The current chat's freshly reset Node API exposes `rpc`, but omits the direct `config`, `nativePipe`, `createElicitation`, and `setResponseMeta` APIs assumed by the official Windows wrapper. The wrapper therefore failed before reaching a working Computer Use service.

The root independently called the SDK's documented internal setup operation, `nodeRepl.rpc('sky', {type:'setup'})`. It returned `target:'windows'` and the Windows window API. This establishes that the current chat already has a registered official Sky service. Earlier conclusions that the whole host lacked control, or that support was the only remaining route, were too broad.

## Change

Only the official `scripts/computer-use-client.mjs` wrapper is changed. When the direct native Node API exists, its original native-pipe path is retained. When the current runtime supplies `rpc`, the wrapper internally loads the SDK's existing Sky RPC adapter and requires the Windows window API. It calls `setResponseMeta` only when that optional function exists.

The agent still imports the absolute official wrapper. It does not import @oai/sky directly from its task code. No custom RPC/helper protocol, desktop client implementation, approval callback, pipe path, or approved-app metadata was added.

## Original SDK approval and interruption path

- CUA REPL's official `environment.js` registers the computer surface as `sky: '@oai/sky/service'`.
- `sky.js` obtains the available methods via the official `rpc('sky', {type:'setup'})` adapter. Method calls use the existing `sky_service_rpc.invoke` and `service.handleRpc.execute` dispatch.
- The service dispatches to the same `WindowsComputerUseClient` and `WindowsComputerUseClientBase`, which collect and forward current-turn metadata.
- The original helper transport requires a real elicitation callback for an app approval, rejects unavailable/denied approval, and only adds approved-app metadata after an `accept` result. Its native-pipe counterpart likewise rejects when a required approval callback is unavailable.
- Physical Escape handling, interruption records, request rejection and cross-turn `end_turn` transitions remain in the unchanged SDK transports. The CUA REPL launcher also retains its original signal forwarding.

All these SDK files were archived and checked byte-for-byte unchanged. The wrapper's response metadata setter is a display/annotation hook, not an approval token; its absence is not replaced by a fabricated function.

## Verification and recovery

`node --check` passed on the modified wrapper. Root independently verified official absolute-wrapper bootstrap, `list_apps`, canonical Sandbox window resolution/activation and visible screenshots. A real mouse right-click opened the Iris tray menu; clicks also closed the menu and opened guest Explorer. Focusing the address bar, Ctrl+L, literal text input and Return navigated Explorer to `C:\IrisT040R2rCandidate`, with the expected title/address and package files visibly present. This proves the current-chat control connection plus mouse, keyboard shortcut and text-input effects for that scenario.

A separate Escape API call completed but the tray menu remained visible in its next screenshot. That call is retained as an observation and is not used to claim a successful key effect. The actual screenshot images remain in root tool outputs; the child did not decode or write their data. Runtime observations and tool-completion reports are distinguished in `root-runtime-verification.json`.

The actual current service method count is 13. An earlier oral count of 14 was corrected against the real setup/source result. These checks close this control-connection repair; they do not establish broader Iris acceptance or NFR completion.

Original wrapper SHA256: `bdc61752e0f9d9b1e73e3cba25ca71d3d9fe54d335ed8196378106081474d7c1`.

Patched wrapper SHA256: `aa7cf705c7b4c93ea52d8f2ac6dd81de841a3297da0e4b53c78cebd117d35a7b`.

To revert only this wrapper repair:

```powershell
python F:\Iris\tasks\T-040.evidence\computer-control-rpc-compatibility-20261108-1\rollback.py
```

The rollback verifies the current wrapper SHA before restoring original bytes. It does not alter the earlier package export repair or other chats' runtime configuration. The child did not reset the shared kernel, manipulate application UI, change security settings, or operate Iris/Sandbox.
