# EQ selection and bypass spectrum

Remove only the microphone target dropdown from the shared editor; retain the output bus selector. Input strip EQ buttons select the target and share selected styling, defaulting to input 1. Keep selection and edit data separate from effect enabling.

The missing spectrum is caused by tying callback startup to the EQ enable flag. Introduce a monitoring lease on the existing insert: visible microphone editor requests actual, flat input monitoring even when all EQs are bypassed. Hide/unload/minimize drops the lease; active EQ keeps its processing, while unused inserts release after a short fade/timeout. Retrying must not steal an occupied insert. On target change the callback clears its ring and readers reject stale target data. Verify with default button/target tests and native bypass-only monitoring, spectrum peak, no allocations, no callback fault and clean release.
