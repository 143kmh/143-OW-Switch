# Release verification

## Verified during development

- [x] Release build and self-contained win-x64 publish.
- [x] Core regression tests: create, toggle, repair, duplicates, rollback, ownership, cleanup, config, drift.
- [x] Published executable launches in preview mode; main window is visually inspected.

## Requires interactive elevated Windows testing

Run these with no existing application rules, or note their state before testing. Use Settings → Remove firewall rules afterwards if the app is not intended to remain installed.

- [ ] First launch → UAC → PARTY, exactly two disabled outbound Block rules.
- [ ] SOLO enables both; PARTY disables both; no unrelated rules changed.
- [ ] Close/reopen and full exit/restart preserve selected mode.
- [ ] Delete one owned rule or change its IP; restarting repairs it.
- [ ] Cancel UAC; error panel and administrator retry work.
- [ ] Disable or manually change one rule; status changes within 10 seconds.
- [ ] Start a second instance; only one elevated instance remains and window opens.
- [ ] Tray left/right clicks, toggles, close to tray and Exit work.
- [ ] Enable startup; sign out/in; UAC appears and the saved mode is restored.
- [ ] Start minimized hides main window after UAC; startup disabled removes only the app entry.
- [ ] Windows 10 and 11, DPI 100%, 125%, 150%, 200%; move between monitors.
- [ ] Test startup after replacing or moving executable.
- [ ] Settings cleanup removes managed rules and startup entry, then exits.
- [ ] Confirm user-supplied ranges produce the intended matchmaking behavior.

No system reboot or elevated firewall mutation was performed during automated development verification.
