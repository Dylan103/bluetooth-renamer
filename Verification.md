# Verification — version 1.1, 4 October 2026

Reboot persistence was manually verified on Windows 11 with a Galaxy Buds FE device. Read-only inspection confirmed the expected null-terminated UTF-8 REG_BINARY value.

Version 1.1 adds that persistent write to the existing Bluetooth API update. Backups precede either mutation. Both values must be independently verified. Undo restores the exact previous persistent bytes or absence, as well as the previous API name. No startup task is installed.

Validation:

- The x64 .NET Framework/WPF application builds successfully with the installed compiler.
- 54 backend checks passed, including exact undo of absent/empty/single-null aliases, same-name persistence, backup ordering, Unicode byte limits, stale values, and partial failures.
- 9 display geometry checks passed.
- The updated GUI was visually inspected at 300% scaling. A simulated device's unchanged displayed name was successfully saved with a persistent alias, and its history recorded both snapshots as Verified.
- Actual preview window diagnostics reported 288 DPI and PerMonitorV2 awareness.
- The Windows elevation launch keeps device selection and non-empty draft names. Cancelling elevation is handled without closing the original window. The UAC approval interaction was not exercised during this update.

No real Bluetooth name or registry value was changed while testing this app update. Reboot evidence comes from a manual test of this registry method. Physical mixed-monitor movement and touch/pen input have not been retested.
