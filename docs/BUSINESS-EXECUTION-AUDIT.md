# Business execution audit — 3.0.2

This review follows the missing-script-dependency/F9 failure and covers the desktop and MCP execution paths. Review focus: fresh input validation, reliable terminal states, process cleanup, and publishing only verified new output.

| Path | Defect and correction | Regression coverage |
| --- | --- | --- |
| F9 / serial video | Preview tolerated script errors; stale metadata could survive dependency removal. Evaluate scripts before F9 navigation and revalidate before execution. Preserve full UTF-8 diagnostics. | EncodingSourceValidationTests, SourceVideoInfoProbeTests |
| Video / Opus pipes | Clean EOF could precede producer failure and stop monitoring it. Continue watching process exit; preserve already-exited source errors. | ProcessPipelineMonitorTests; existing audio lifecycle tests |
| Parallel video | Execution used cached source metadata and required metadata only for some encoders. Probe fresh metadata for every supported non-raw input. | EncodingSourceValidationTests (x264 and SVT-AV1) |
| Automatic encoding | Script dependencies were not checked; failure/cancel events could be overwritten by completion or exit code 0. Validate source and latch terminal errors; verify staged video before publishing. | BusinessExecutionSafetyTests (structured and legacy modes) |
| Audio | eac3to could publish empty output; reader failures could strand running processes. Require non-empty staged output and monitor reader faults. deew retains its manifest and error checks. | BusinessExecutionSafetyTests, Deew5ExecutionTests, CliAudioProcessingRunnerTests |
| Blu-ray demux / scan | Missing playlist, stale staging, and exit code 0 without outputs could report success. Recheck source/playlist, use fresh staging, verify selected outputs and return actual artifact paths. Bound scan waits. | BusinessExecutionSafetyTests (DGDemux and eac3to) |
| Shared queue / MCP | Sources can disappear while queued; a runner returning a nonterminal outcome could leave phantom active work. Recheck at dequeue and reject nonterminal results. Validated MCP previews use fresh script evaluation. | SharedTaskCoordinatorTests, TaskPlanningServiceTests |
| Process lifecycle | Reader faults were not watched during process wait. Managed disposal now terminates the tree; output cleanup waits are bounded. | ProcessExecutionMonitorTests, ProcessProbeRunnerTests |
| Preview | Host response could wait indefinitely and a failed render could retain a stale protocol session. Bound responses to two minutes and close failed sessions. | VapourSynthPreviewServiceTests |
| UI terminal state | Delayed progress could reopen a completed video job. Ignore progress after terminal state and check job identity. | Reviewed against other module active-job guards |

Reverse review checked that completion is emitted after artifact publication, old files survive empty/invalid output, failure events stay authoritative, queue capacity is released after preflight failure, and real child processes are terminated on cancellation/reader failure. A temporary Windows executable lock in a new fixture was corrected by bounded cleanup retries for both IO and access-denied errors.

## 3.0.2 republication: error presentation regression

The initial 3.0.2 build exposed full multiline script diagnostics in the workspace header. `TextWrapping="NoWrap"` does not suppress explicit newlines, so the Auto-sized header consumed the editor's remaining height. This UI path was not exercised by the initial backend tests.

The corrected build bounds the header with `MaxLines="1"`, summarizes existing multiline status values at presentation time, and reports F9 failures on the originating tab with a short localized status and full diagnostic log. It also bounds inline status text in preview, shared task panels and dashboard cards. Successful F9 preparation clears an earlier failure status.

Manual verification with the actual Release application covered the original missing-source script, a synthetic eighty-line error, repeated F9 failure, F5 evaluation failure, scrolling to the final diagnostic line, editing and saving after failure, successful F9 retry, navigating back to the editor, successful F5 retry, preview frame stepping and returning to the editor. Closing the saved test tab restored the original script without modifying it. The 596 non-external-tool tests passed again, and final Release compilation completed with zero warnings and zero errors. Real UI checks supplement the backend suite; they are not claimed to exhaust every visual state.

Validation: all 596 non-external-tool tests passed in Release configuration; desktop Release compilation completed with zero warnings and zero errors. Tests use controllable child executables alongside the existing regression suite. These tests do not replace licensed DEE acceptance runs or testing every physical Blu-ray disc and codec combination. The demux/audio checks establish presence and nonzero size; video additionally uses ffprobe to confirm a readable stream. Detailed deew coverage and limitations remain in DEEW5-VALIDATION.md.
