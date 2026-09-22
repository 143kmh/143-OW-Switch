# Verification — 1.1.0

## Verified automatically / locally

- [x] Core regression suite: 22 tests, including scoped rules, migration, changed game path, repair counts, malformed server JSON, CIDR, offline cache, semantic versions and checksum failures.
- [x] Real filesystem update swap: new file and old backup; failed startup restores old file; checksum mismatch leaves old file unchanged.
- [x] Windows build and self-contained x64 publish.
- [x] Debug preview launches; shortened title and main-screen composition inspected.
- [x] Public GitHub repository authorized by the owner.

Visual automation of Settings was stopped by the user with Escape. No further UI automation was performed afterwards.

## Manual release acceptance checks

- [ ] Real UAC: confirm and cancel; missing game path picker; moved/deleted executable.
- [ ] Inspect both effective firewall rules: ApplicationName equals selected Overwatch.exe, outbound Block, correct ranges/profiles/protocol/mode.
- [ ] Migrate from 1.0 SOLO: old global rules disabled before path selection; other applications remain unaffected.
- [ ] Enable SOLO/PARTY during a real match and confirm intended matchmaking effect.
- [ ] Tray close, menu, repeated launch, startup after Windows login and DPI 100/125/150/200% on Windows 10/11.
- [ ] Observe a real Overwatch match through ETW; verify remote candidate and Google Cloud region. Confirm voice/chat cannot be mistaken for certainty.
- [ ] Release-to-release update through UI with administrator privileges: download, protected staging, exit, executable replacement, launch acknowledgement and cleanup.
- [ ] Cancel/intercept a download and simulate checksum mismatch; current installation remains usable.
- [ ] Full crash/locked-executable/antivirus failure and rollback on the real installed binary.
- [ ] Future-dated valid remote servers.json, corrupt remote JSON and disconnected GitHub: retain saved mode and last good config.

Tests use an in-memory firewall store and temporary files. They do not replace elevated integration tests. No system reboot or live game test was performed during development.
