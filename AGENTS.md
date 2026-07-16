# Agent Instructions

## Security boundary (mandatory)

This project may improve file and folder search with a high-speed local indexer similar in user experience to Everything. Performance work on indexing is allowed, but it must stay within normal, documented, least-privileged operating-system and application APIs.

Do not modify, disable, weaken, bypass, or attempt to evade any security-related feature, policy, or protection. This prohibition includes, but is not limited to:

- antivirus, EDR, firewall, SmartScreen, code-signing, sandbox, access-control, audit, or security-monitoring behavior;
- security policies, permission checks, authentication or authorization logic, file ownership, ACLs, UAC, execution policy, or privilege boundaries;
- security exclusions, allowlists, registry security settings, system services, drivers, scheduled tasks, startup or persistence mechanisms;
- repository safeguards, secret scanning, dependency/security checks, CI security gates, or existing security-related code and configuration;
- undocumented security bypasses, exploit-like techniques, stealth, evasion, obfuscation, or attempts to conceal indexing activity.

Never request, acquire, or retain elevated privileges merely to make indexing faster. Do not install or modify kernel drivers, read raw disks or filesystem structures, access protected files, or use undocumented APIs. Do not change operating-system or third-party security settings. Existing security-related files and behavior are out of scope unless the user gives explicit, specific approval for a clearly described defensive change.

Index only files and folders that the current user can normally access and that are within user-selected or application-configured roots. Respect access failures, exclusions, symbolic-link/reparse-point boundaries, privacy settings, and cancellation. Skipping an inaccessible item is the required behavior; bypassing the restriction is not.

Prefer safe performance techniques such as incremental metadata updates, bounded parallelism, batching, caching, debouncing, efficient data structures, and documented filesystem notifications. Tests must use repository fixtures or temporary directories and must not scan unrelated user, system, credential, browser-profile, or security-product data.

If a proposed implementation could affect a security control, permissions, protected system state, or data outside the configured indexing scope, stop before making changes. Explain the exact need, files/settings affected, risks, and a safer alternative, then wait for the user's explicit approval. A request for "fast indexing" is never permission to alter security controls or cross privilege boundaries.
