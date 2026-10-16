# Final binding precheck

The first finalize_binding.py attempt included an extra hypothesis that the final-2 and final-3 self-contained Maintenance executables would have identical bytes. The actual comparison returned false and the assertion failed; this is not evidence of a broken package. No delivery-binding.json was emitted by that failed attempt, and no commit was made.

The byte-equality assertion was removed. Both binary sets and their package hashes remain preserved. A new actual clean Windows run uses release-0.2.0-final-3 directly, so earlier cold-start results are not silently substituted for the latest installer. The final binding records the real comparison rather than claiming equality.

The subsequent unrestricted git diff --cached --check output also flagged CRLF in raw evidence protected by its existing `* -text` attributes. Those original logs/snapshots were not normalized. Source/document whitespace checks are run separately from raw evidence.
