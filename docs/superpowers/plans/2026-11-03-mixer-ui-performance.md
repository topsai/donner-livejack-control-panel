# Mixer responsiveness and header rates

Measured regression: Windows rate read2.1–2.5s each, render endpoint identity1.4–2.2s, native device enumeration~100ms. All repeated on dispatcher (rates every2s, home route every500ms), blocking the application.

Fix: background Windows read with immutable cache and one in-flight read, avoid hidden page polling, cancel/ignore results after dispose, maintain UI and Voicemeeter SDK level calls on dispatcher. Home routing uses background cached endpoint inventory; no render COM or device enumeration in40ms meter path. Compact rate summary + settings toggle beside title, editor below only when requested, preserving current audio settings. Regression: inject slow reader and verify dispatcher progress, no overlapping background queries, cache route accuracy, correct header placement and no ctor reads. Build/native read-only smoke + independent review; publish v1.28.
