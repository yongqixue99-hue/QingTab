# QingTab Privacy Policy

Last updated: 2026-08-15

QingTab is a local Windows 11 tray utility. It does not include telemetry, analytics, advertising, automatic updates, or automatic network communication.

## Data collection and transmission

- QingTab does not collect or transmit personal data.
- Folder paths opened through QingTab are processed locally and are not sent to any network service.
- Recent diagnostic history is kept locally in memory and does not store full folder paths by default.
- Persistent error records contain bounded technical identifiers such as error codes, exception types, and HRESULT values; they do not intentionally store folder paths, exception messages, or stack traces.
- Local error logs are size-limited and rotated.

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

## Local system changes

When enabled by the user, QingTab changes only the current Windows user's folder-open command so ordinary folder requests can be handled by QingTab. The optional startup setting adds a current-user startup entry. These changes are documented in `README.md` and can be disabled from the tray menu.

Exiting or uninstalling QingTab restores the Windows folder-open behavior when the registered value is still owned by QingTab. If another program has changed the same value, QingTab preserves that newer value instead of overwriting it.

## Network links

QingTab itself does not contact GitHub, SignPath, or update servers. Documentation may contain links that are opened only when the user chooses to visit them.

## Contact

Questions and public issue reports can be submitted through the [QingTab GitHub repository](https://github.com/yongqixue99-hue/QingTab/issues).
