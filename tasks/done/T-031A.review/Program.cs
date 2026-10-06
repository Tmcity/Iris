using Iris.Platform.Windows.Tests.Selection;
var test=new UiaStartupCleanupTests();int passed=0;
foreach(bool stop in new[]{false,true}) {
 await test.ReadAsync_And_StopAsync_WhenStderrDrainPending_DoNotClaimCompletionUntilDrainObserved(stop);passed++;Console.WriteLine($"DRAIN stop={stop} PASS");
 await test.DrainScenario_WhenBodyThrows_FinallyReleasesAndJoinsAllOwnedWork(stop);passed++;Console.WriteLine($"BODY_FAILURE_CLEANUP stop={stop} PASS");
}
await test.DrainScenario_WhenBodyAndCleanupThrow_PreservesBothErrors();passed++;
await test.RetryCleanupAsync_WhenActualDrainPending_ConcurrentRetryAndStopJoinSameAttempt();passed++;
Console.WriteLine($"UIA_FINAL_SCENARIOS passed={passed} failures=0");
