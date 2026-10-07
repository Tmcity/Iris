# T-036A executable evidence prototype

This is an experimental project, not Iris production implementation. It references the existing Release Iris.Core / Iris.Infrastructure / Iris.Providers DLLs from F:/Iris. It does not build or edit production projects.

PowerShell-compatible reproduction:

```powershell
python .\prepare.py
dotnet build .\Prototype.csproj -c Release
dotnet run --project .\Prototype.csproj -c Release --no-build -- current runtime actual-coordinator persistence migration pipelines connectivity streaming
```

Modes use temporary owned JSON files, MemorySecrets and scripted in-memory HTTP streams. `ConnectCallback` never opens a socket. The runtime HTTP parser/authentication/pool and both current named retry pipelines are real. Temporary JSON directories are removed only after owned work completes. Synthetic credential strings are intentional and are never real user values.

`prepare.py` resolves existing DLL paths and writes binding hashes. Its commit field records the source checkout at preparation time; it does not prove newly compiled DLL provenance by itself. The delivered evidence pins DLL/source hashes separately. No API keys, proxy/server credentials, user configuration or user clipboard are required.

`runtime-exact/` contains official .NET 10.0.12 source at the matching VMR build commit and its MIT license. `fetch_runtime.py` is an optional explicit read-only official-source fetch; it is not invoked by build/run. Earlier source lookup failures and near-version source copies are kept in `runtime-sources/` for provenance.

Current/unsafe behaviors print `COUNTEREXAMPLE` and are expected observations, not production fixes. `ProposedPersistence`, `ProposalStore`, `GenerationPool` and `DraftProbeSession` are bounded experimental mechanisms, not full UI/LRU/production migration implementations. The proposed public declarations are in the separate T-036A contract recommendation text, not compiled into Iris.

Streaming checks exercise the actual Iris SSE parser and the original named retry pipelines with non-empty, progressively delivered chunked bodies. The response content owns the handler-generation lease; consumers join reads before response disposal. A borrowed stream alone does not claim to complete response ownership.

Only this directory's root `*.cs` files are compiled. Downloaded runtime sources are reading evidence, not replacement framework implementations. `Directory.Build.props` isolates this standalone experimental project from a parent repository's build settings when archived.
