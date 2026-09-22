# Verification — 1.1.2 preparation

## Verified automatically / locally

- [x] Full regression suite: 32 tests, including UDP/TCP presentation, Cloudflare IPv4/IPv6, conservative copy output, unknown-field deduplication and release version consistency, plus all 22 existing core tests.
- [x] Real filesystem update swap: new file and old backup; failed startup restores old file; checksum mismatch leaves old file unchanged.
- [x] Windows build and self-contained x64 publish.
- [x] Headless Debug WPF preview: home, Settings, diagnostics and About rendered at 100%, 125% and 150% without opening desktop windows. Discord now fits inside the original 360 × 398 window with bottom padding.
- [x] Public GitHub repository authorized by the owner.

Scaled bitmap previews check layout; actual Windows DPI transitions, pointer hover/drag and live ETW behavior still require manual verification. No tag or release is created for this patch preparation.

## Manual release acceptance checks

- [ ] Real UAC: confirm and cancel; missing game path picker; moved/deleted executable.
- [ ] Inspect both effective firewall rules: ApplicationName equals selected Overwatch.exe, outbound Block, correct ranges/profiles/protocol/mode.
- [ ] Migrate from 1.0 Block mode: old global rules disabled before path selection; other applications remain unaffected.
- [ ] Enable BLOCK/UNBLOCK during a real match and confirm intended matchmaking effect.
- [ ] Tray close, menu, repeated launch, startup after Windows login and DPI 100/125/150/200% on Windows 10/11.
- [ ] Observe a real Overwatch match through ETW; verify remote candidate and Google Cloud region. Confirm voice/chat cannot be mistaken for certainty.
- [ ] In a real match, TCP-only or Cloudflare results remain auxiliary; 66.40.191.90 must not imply a confirmed match, provider or Amsterdam location.
- [ ] Release-to-release update through UI with administrator privileges: download, protected staging, exit, executable replacement, launch acknowledgement and cleanup.
- [ ] Cancel/intercept a download and simulate checksum mismatch; current installation remains usable.
- [ ] Full crash/locked-executable/antivirus failure and rollback on the real installed binary.
- [ ] Future-dated valid remote servers.json, corrupt remote JSON and disconnected GitHub: retain saved mode and last good config.

Tests use an in-memory firewall store and temporary files. They do not replace elevated integration tests. No system reboot or live game test was performed during development.
