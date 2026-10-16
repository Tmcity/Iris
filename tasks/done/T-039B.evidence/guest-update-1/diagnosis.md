# Local update and redirected-output fixture failure

The actual engine result records native exit 0, verified 0.2.1 and no cleanup errors. The old real Iris process exited 0 before commit; the dedicated control parent remained alive after commit until explicitly released. The guest process snapshot then showed exactly one restarted Iris.App process (PID 3088) with no command arguments.

The GUI process launcher redirected stdout/stderr and drained them after the validation owner exited. The newly restarted App inherited those output handles, so the outer script remained waiting for output EOF rather than progressing to its new-process assertions. The captured process snapshot identifies the surviving restarted App; the validation host was already gone. The developer sent the normal second-instance Exit in this owned guest to finish that stream lifetime. The original script then continued and correctly failed its now-late exactly-one-restart assertion. The failure is retained and is not represented as an overall pass.

The launcher has been changed to capture pipes only for the explicit UiaHost stdin/EOF scenario. GUI owner completion is based on the acquired Process handle and actual exit code; structured product/guest files record the results. A new labeled local-update run must verify restart and final registration without that diagnostic intervention.

The control-parent executable path is substituted with this experiment's real PowerShell worker. UpdateTransaction and native apply/validation/registration/restart are real product code. This is not the public GitHub user UI chain reserved for C.
