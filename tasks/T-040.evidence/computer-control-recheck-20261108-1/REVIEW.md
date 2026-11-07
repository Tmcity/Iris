# Windows Computer Use recheck: 2026-11-08

The prior package import repair is still installed. The current package SHA256 is `babaa17c7a26204a5937476427506545aa662703c68a90286cfc217a8730a407`, matching the previously reviewed repair. All 13 files in the prior evidence manifest still match their recorded bytes and SHA256 values. No new component mutation was made in this recheck.

The root's current official bootstrap returned `Computer Use native pipe is unavailable`. A fresh read-only capability probe in the child environment confirms that `nodeRepl.config`, `nativePipe`, `nativePipe.createConnection`, the application-provided pipe directory and `createElicitation` are absent. The import compatibility fix and the missing trusted desktop bridge are separate conditions. No new bootstrap retry or kernel reset was performed by this agent.

## Official recovery entry

The official documentation page at https://developers.openai.com/codex/computer-use redirects to https://learn.chatgpt.com/docs/computer-use and returned HTTP200. The complete page body and response metadata are retained in `official-computer-use.html` and `official-computer-use.json`.

Its **Set up Computer Use** section says:

> Open Settings > Computer use. Under Control, find Any App. If the row shows Install, select it and follow the prompts. If the switch is off, turn it on. If your app shows a Computer Use plugin page instead, follow the setup prompts on that page.

The page also requires the target app to be visible on the active desktop on Windows. It says app permissions may be restricted by a workspace administrator. This recheck does not establish the user's current Any App setting, installation status, region entitlement or administrator policy.

The smallest next user check is to open that setting (or the older Computer Use plugin page) and report whether Any App shows **Install**, **off**, or **on**. If installation or enabling is needed, the official page supplies that setup flow. If it is already on but bootstrap still lacks the bridge, local package edits cannot safely fabricate the host's trusted connection and approval service. No app restart was tested or claimed to be an assured repair.

## Why the setup check requires the user

The applied Windows control skill explicitly says: “Do not automate the Codex desktop app UI or Codex CLI or Codex extensions within Windows apps.” The fetched official Computer Use documentation independently says that the feature cannot automate ChatGPT itself or approve security/privacy permission prompts. Therefore the agent cannot use Computer Use to configure its own host; the user must handle that one setup check.

Skill source: C:/Users/28910/.codex/plugins/cache/openai-bundled/computer-use/26.609.41114/skills/computer-use/SKILL.md.

No approval mechanism was fabricated, no custom helper protocol or alternate desktop channel was used, no helper executable was launched directly, no security configuration or allow-list was changed, and no Iris/Sandbox operation was performed.

## Retrieval note

The official search URL returned HTTP404; links in its actual documentation navigation led to the fetched pages. The Computer Use page retrieval and artifact writes succeeded with HTTP200. The first console excerpt print then encountered a GBK `UnicodeEncodeError`; the complete UTF-8 page and HTTP metadata were already saved and independently inspected. This output-encoding error did not alter the source body.
