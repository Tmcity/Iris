# User-confirmed Any App enabled

The user confirmed **“Any App 已开启”**, relayed by the root agent. This supersedes the earlier unknown switch state in `REVIEW.md` and `checks.json`; those earlier records are retained as historical observations. It proves the user's visible switch state, not component installation, app eligibility, regional availability, or administrator policy.

A fresh read-only capability check at 2026-11-07T19:08:49.744Z still returned false for the trusted config, native pipe, native-pipe connection function, application-supplied pipe directory and app-approval callback. No kernel reset, bootstrap retry, toggle or component mutation followed this observation.

The current official client throws the exact unqualified error `Computer Use native pipe is unavailable` before attempting a pipe connection when the trusted runtime interface is missing. A connection failure would instead include an appended transport error. The available evidence therefore concerns missing host-provided runtime capability; it does not prove a crashed helper or an Iris defect.

The installed Microsoft Store package identity is independently confirmed from AppxManifest.xml as **OpenAI.Codex 26.1103.3517.0, x64**. The executable file version is recorded separately and is not substituted for the package version.

The fetched official [Computer Use documentation](https://learn.chatgpt.com/docs/computer-use) describes the Settings → Computer use → Any App setup and app approvals. The fetched official [Troubleshooting page](https://learn.chatgpt.com/docs/reference/troubleshooting) provides a **Feedback and logs** section: type `/` in the composer to provide feedback, or report an issue through the linked Codex GitHub issue flow. Neither fetched page documents a local fix specifically for an enabled switch with a missing native bridge. Its restart recommendation belongs to terminal-stuck recovery and is not treated as a proven fix for this failure.

The remaining control-component problem needs host application/runtime support to restore the trusted bridge. A minimal local support-report draft is provided in `SUPPORT_REPORT_DRAFT.md`. No report, issue, logs or session transcript was sent anywhere. The user is not asked to repeat toggles or restarts; Iris diagnostics can continue independently.
