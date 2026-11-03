# Background mixer and readable endpoints

User intent: operate only the Donner UI; simplify endpoint names in both our UI and Windows. Existing enable consent and configuration warnings persist.

Design: Banana remains a required running audio engine. Default Start is background, using hidden process startup and official Command.Show=0 after connection; opening the original GUI is an explicit separate action. No audio routes or defaults change. Reconnect may start a stopped engine in background. Windows names change only through an explicit operation with a durable original-name backup and a restore action. Identify only VB-Audio Voicemeeter endpoints by immutable adapter/description properties, retain endpoint IDs and technical VAIO/AUX/B1/B2 mapping. Non-Banana/extension endpoints are clearly marked, never disabled. Default/uncheck does not rename endpoints.

1. Add failing core tests for alias mapping and fake UI tests for background/show distinction.
2. Implement background engine lifecycle and explicit GUI show. Verify real window hidden with API connection preserved.
3. Add endpoint inventory, reversible renaming adapter and UI. Verify only known endpoints are changed, backup precedes writes, restore protects later custom names. Read back Windows names.
4. Build, UI smoke and review; document, publish and upgrade current release.
