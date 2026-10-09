# T-037 independent review probe — validated

This directory is outside the repository. Build/run only when the developer has finished its build/test work and the relevant product assemblies are stable. There are no ProjectReferences; the project references the App test output copies of the actual product assemblies.

Six scenarios: real local random pipe with native same-session checks and compact Unicode framing; a real ACK write followed by the server's ending transport boundary while native client reading is held must still yield Accepted after release; noncooperative write released after cancellation must start no read; noncooperative accept released after stop must start no read or dispatch; a canceled partial read must not start another read; a real Generic Host disposing an unstarted actual IpcCommandCoordinator must complete without depending on background cleanup being scheduled first.

The probe's runtime configuration limits only this isolated process to one portable ThreadPool worker. The unused-host check invokes synchronous Host.Dispose on that worker to expose an unnecessary ForceYield deterministically. It does not alter the product's runtime configuration or machine settings and is not a performance measurement.

Fake pipes replace only the I/O boundary. Unconnected fixture handles are independently closed. The real-wire case uses only a new Iris.Review.Pipe random name, never the production pipe or current user configuration. Gates are released before all owned work is joined. A failing assertion is retained even when finally can complete normal fixture cleanup.

Executed against commit 19e15f6748b71aad0886c926e02a9542a347aa9b after independent Release/full: all six scenarios passed. Raw final output is ../final-probe.log in the archived review. The first candidate compilation missed System.IO; that fixture-only failure is retained in candidate-probe-1.log. Product source was not changed by the reviewer.
