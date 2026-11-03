# Microphone EQ implementation plan

Design: ../specs/2026-11-03-microphone-eq-design.md

1. Donner.Core/MicrophoneEq.cs: validated six-band settings, RBJ coefficients and frequency response; block processor with independent stereo input states, bypass and crossfades. Tests in Donner.Tests cover literal center gain, high/low cut rejection, pass-through of other strips, nonfinite/rate/parameter handling and allocation-free steady processing. Watch DSP tests fail against identity stub before implementing filters.
2. Donner.Panel/MicrophoneEqInsert.cs: native input callback ABI, copy-through for all unmodified channels, real sample-rate status, no blocking/allocation in callback, conflict handling, restart/disposal before client Logout. Ring buffer for input spectrum. Validate native pointer layout with synthetic buffers before an all-bypass live callback run.
3. Donner.Panel/MicrophoneEqWindow.cs and EqGraph.cs: independent dark editor opened per hardware strip; interactive log frequency graph, biquad response, actual spectrum, numeric fields/type/Q, enable/bypass/reset/preamp; atomic config persistence with Enabled=false on load. Control lifetime owned by MixerPanel; hide editor without losing DSP, dispose before client. UI checks for invalid numeric entries, node mapping and edit isolation.
4. Verification: core suite; WPF smoke; native bypass probe and callback cleanup; screenshots visually checked at minimum and normal window sizes; review real-time lifecycle and routing isolation. Build installer/update docs/release only after passing; do not change current real routing or apply non-flat EQ during verification.

Review focus: occupied insert must not be unregistered by failed client; SR changes use correct coefficients; no input/virtual crossfeed; duplicate editor opening must not duplicate callbacks; disabled/closing/disconnected states must preserve normal sound.
