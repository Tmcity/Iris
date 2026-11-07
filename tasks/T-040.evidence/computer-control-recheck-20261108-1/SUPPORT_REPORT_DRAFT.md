# Windows Computer Use: Any App enabled, trusted native bridge unavailable

This is a local draft for review. It has not been submitted.

## Environment

- Windows-native desktop app, Microsoft Store package **OpenAI.Codex 26.1103.3517.0**, x64.
- Observed host OS: Windows 11, version **10.0.26200**, build **26200**, x64 (read from the running host; this is not the Sandbox OS version).
- Official Computer Use plugin version **26.609.41114**; bundled `@oai/sky` **0.7.17**.
- User confirms the **Any App** switch is enabled. Installation/eligibility/administrator policy have not been independently verified.
- The package import initially failed because `@oai/sky` did not export the exact base-client subpath imported by the same official plugin. A local, reversible package metadata repair added only that exact export; official client and approval/interruption code remain unchanged.

## Observed failure

Using the documented official `scripts/computer-use-client.mjs` entry and `setupComputerUseRuntime`, the root and child observed:

```text
Computer Use native pipe is unavailable
```

The root observed this after an earlier kernel reset. After the user's enabled-state confirmation, the child performed only a read-only capability check, with these results:

```json
{
  "hasTrustedConfig": false,
  "hasNativePipe": false,
  "canCreateNativePipeConnection": false,
  "hasNativePipeDirectory": false,
  "canRequestAppApproval": false,
  "skyInstalled": false
}
```

The failure occurs at the official preconnection guard, so `list_apps` is not reached. Please check host provisioning of the trusted Computer Use runtime and approval bridge for this desktop app/runtime combination. No helper crash or transport timeout has been established.

## Minimal supporting artifacts

- `native-capabilities-after-any-app-on.json`
- `host-context-after-any-app-on.json`
- The package import repair hashes and rollback record in the preceding repair evidence.

Complete session transcripts, private credentials and unrelated application logs are unnecessary for this minimal report. The report should be submitted only if the user chooses to do so through the official feedback or issue flow.
