# T-036A design review, development stage

Read T-036A.proposal.md and T-036A.proposed-contracts.cs.txt in full. No production contract approval yet.

The recommended direction is coherent with independently confirmed runtime behavior: versioned endpoint-bound proxy credential entries; explicit empty/tombstone Clear to prevent legacy reimport; one Core coordinator/gate for capture and commit; network observer takes immutable settings/generation without calling capture, avoiding DI recursion and gate self-wait; per-logical-HTTP snapshot outside existing retry handlers; immutable route/credential identity in handler-pool key; header return does not relinquish response transport ownership; fail-closed validation happens before BCL proxy callbacks; future-schema readonly latch; migration writes sanitized logical backup before main replacement and preserves credentials referenced by a published backup.

Required before A acceptance: implement and validate the already-issued stream supplement. Current copied LeaseContent only overrides SerializeToStreamAsync and buffers ReadAsStreamAsync until EOF, independently reproduced in iris-t036-stream-review/stream-2.log. Minimal forwarding comparator in stream-3.log proves a feasible nonbuffering path; not a full production resource wrapper. The design must explicitly preserve streaming, cancellation and actual response/content/stream retirement ownership. Do not accept zero-length response checks as streaming evidence.

Implementation clarifications to freeze with the later CCR/work order:
- Pure shared URI/local-address codec access for Infrastructure needs an explicit assembly-internal sharing decision; Core currently grants friend access to Providers/Platform, not Infrastructure. Avoid accidental Core->Providers dependency or extra public APIs outside the frozen declaration.
- System scheme capture must include socks4/socks4a/socks5 as well as Negotiate/NTLM/Digest/Basic; current prototype now includes all of these. Real TLS/SOCKS/Windows integrated-auth/PAC remain explicitly unverified.
- ISettingsService.IsReadOnly addition requires real store gating before mutate and UI behavior; old fakes must implement it without weakening current tests. This is not established by ProposalStore's prototype-only latch.
- Runtime capacity/LRU/failure retirement and normal response streaming wrappers remain production acceptance work even when the prototype is accepted.
- Preserve current retry/authentication semantics, no additional model Test/Translate requests for network reachability.

Codex queue help exposes no immediate-insertion option. The stream supplement is already queued once and in shared current_task; do not resend or infer a stopped turn from interrupted/completedAt=null. Computer Use connection could not be repaired safely; do not use alternate foreground input.
