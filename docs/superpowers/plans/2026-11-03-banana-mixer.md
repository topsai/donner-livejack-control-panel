# Optional Banana mixer implementation plan

Goal: add an optional Chinese mixer below the existing device controls, with explicit opt-in installation guidance. Keep original-driver controls independent.

Design: WPF/C# frontend and a separate official Remote API adapter. No native-window embedding. Mixer disabled by default; no Banana payload in our installer. The checkbox persists opt-in, starts installed Banana, or opens the installation guide. Cancel leaves the feature disabled. Unchecking disconnects our client without force-closing independently running Banana or resetting its configuration.

Mixer: five input strips and five buses, gain/mute/levels/routes, hardware device selection, input compression/gate and virtual-input three-band tone controls, output six-cell EQ. Stable controls and in-place polling; disconnected writes prohibited. No automatic routing or system default-device changes.

Guide: official origin and donationware notice; explain administrator installation and reboot. Download and launch the verified official interactive installer only after the user selects installation; no automatic reboot.

Files: Core/MixerSettings.cs for persistence and parameter rules; Panel/VoicemeeterClient.cs for ABI/lifecycle; MixerSetupWindow.cs for installation; MixerPanel.cs for UI; MixerIntegration.cs for settings/window lifecycle; MainWindow.xaml and .cs for placement/header.

Execution: inline with executing-plans. Add failing core tests, implement policy, implement adapter and opt-in guide, implement UI, add isolated regression checks, build and visually verify default/compact windows, review diff, package and update local/public release through established workflow.

Validation: missing Banana must not trigger installation at startup. Native ABI and mixer UI can be tested against a disposable SDK-compatible fixture. Do not install Banana solely to test; report real-engine validation separately.

Review: fresh review completed; exception handling and mono/reverse display findings fixed and covered. Core 66/66 and isolated GUI regressions pass. Official installer signature verified without execution. Real Banana engine not installed/tested.
