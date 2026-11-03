# Update notice

User specifies README mixer introduction immediately below home screenshot; About shows release updates and both gear/About-menu red dots exist exactly while a newer stable release is known.

Design: asynchronous public GitHub latest-release check on startup and every 6 hours, manual retry on About. Compare numeric normalized versions to assembly version, ignore drafts/prereleases. No installer auto-run/download. Only open fixed repository release URLs. Initial/failure unknown states do not assert up-to-date; preserve previously confirmed available update on transient failure. Opening About does not dismiss a version-availability indicator. Cancel requests on close; smoke isolated from network.

1. Core version/metadata/client tests first, then update checker.
2. About UI and synchronized dots; fake-network UI regression newer/current/older/failed/retry and responsiveness.
3. Move complete README mixer section below screenshot, refresh docs, build and review, publish v1.26 and upgrade.
