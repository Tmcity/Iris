# T-036C independent UI review

Actual owned SettingsWindow/VM/session, Core edit/snapshot source, JsonSettingsService in private temporary files, real ProxyConnectivityTester and runtime socket byte streams. Translation/OCR registry boundaries are fake to isolate unrelated providers; no real network or user data.

ui-3.log passes credential paste -> test with zero config/secret writes -> one joint History/Network save and password clearing, and manual cleanup retry whose final UI publication blocks save until released. Consumer/dispatcher gates are released before cleanup; owned STA exits.

ui-1 is an isolated harness compile error (WPF implicit usings omit System.IO/System.Net.Http), fixed via GlobalUsings.cs. ui-2 ran successfully with two harness warnings; ui-3 adds explicit null assertions and the default scripted response, no product edits or removed business assertions. Final release binding still required.
