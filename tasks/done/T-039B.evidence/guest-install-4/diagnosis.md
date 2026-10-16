# Actual installed application shutdown failure

This round used release-0.2.0-5 in owned Sandbox 1035d4ff-d461-4bab-b9a4-f9a006808d27. The revised script rejected the external maintenance owner's exit code 2. The .NET runtime bootstrap, installed identity, no automatic App startup, UiaHost EOF exit and actual English settings window were separately observed before this failure.

The external Maintenance process waited for the original App to exit, then returned TaskCanceledException. The App had already closed its window and stopped IPC acceptance. Its retained failure was captured from the actual guest process in iris-shutdown.dmp (kept under artifacts). Offline SOS with the exact guest 10.0.13 DAC shows Dispatcher.VerifyAccess → TaskbarIcon.set_LeftClickCommand → TrayIconController.Dispose → AppBootstrapper.StopSynchronousParts → StopCoreAsync. The first offline analysis lacked the matching DAC; that unsuccessful diagnostic output is retained.

A subsequent second-instance Exit could not repair an instance whose IPC was already sealed; its feedback dialog was closed in the guest. After collecting the dump, original logs and safe Maintenance result, this owned Sandbox was explicitly stopped. This is fixture termination of the failed environment, not successful product shutdown/uninstall. No native uninstall was observed in this failed round. Original results were not overwritten.

The source fix dispatches synchronous UI teardown to the original Application Dispatcher and only marks synchronous cleanup complete after it succeeds. The deterministic host test checks the cleanup thread via a narrow resource boundary; installed-product revalidation is a separate next round.
