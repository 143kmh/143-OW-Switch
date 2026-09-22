143 OW Switch 1.1.2 — prepared patch (not released)

- Only observed non-CDN UDP traffic is shown as a current-match candidate. TCP connections are auxiliary endpoints with an explicit missing-match-UDP notice.
- Cloudflare proxy ranges are classified locally from the official published IPv4/IPv6 lists. No per-IP lookup services, transmitted IPs or inferred Blizzard/city labels.
- Diagnostics now separate the heading, endpoint details and confidence note; redundant unknown fields are omitted. Copied reports preserve the same classification.
- Polished Discord text-action alignment/hover and the thin purple Settings scrollbar; retained BLOCK / UNBLOCK and compact dark styling.
- Aligned application, updater and Settings/About versions at 1.1.2, with regression coverage for presentation and version consistency.

Existing config/firewall compatibility, scoped rules, ETW collection and safe updater behavior are retained. No tag or release is created by this preparation.
