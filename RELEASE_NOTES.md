143 OW Switch 1.1.0 — final pass

- Firewall rules target the selected Overwatch.exe only; legacy global blocks are disabled during migration.
- Automatic game path discovery, file picker, persistence and repair.
- Validated CIDR servers.json: async GitHub refresh, cache and bundled offline fallback.
- GitHub Releases updater: semantic versions, SHA-256, protected staging, atomic replacement, startup acknowledgement and rollback.
- Current-server diagnostics: five-second Windows ETW network metadata sample, TCP table fallback, local Google Cloud region lookup and copyable report. Results are candidates, not guaranteed match servers.
- Shorter branding and refined Settings. Preview mode exists only in Debug builds.

Download 143OWSwitch.exe. .NET is included. Existing settings and startup configuration are retained. Keep the executable in a permanent folder. To migrate from 1.0, exit the old app using its tray menu first; 1.0 has no built-in updater.

The executable is unsigned. UAC is required at launch and after Windows login when startup is enabled. SHA-256 is provided in 143OWSwitch.exe.sha256.

Verified: core regression tests, file replacement/rollback, local Windows build and UI preview. Full elevated end-to-end update, UAC/reboot and a live Overwatch match remain manual checks; see CHECKLIST.md.
