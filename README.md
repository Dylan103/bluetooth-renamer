# Bluetooth Renamer 1.1

A portable 64-bit Windows app for the local Bluetooth device names shown in Quick Settings.

## Download

Download [Bluetooth Renamer 1.1.0](https://github.com/Dylan103/bluetooth-renamer/releases/tag/v1.1.0), then extract the ZIP before opening the app.

## Use

1. Open **Bluetooth Renamer.exe**. Keep its `.exe.config` file beside it.
2. Select a device and enter its new name. Choose **Enable saving…** if shown, and approve Windows' administrator prompt. The editing window keeps your device selection and draft name.
3. Choose **Save name** in the editing window. Saving updates both the current Bluetooth name and its persistent `FriendlyName` value.
4. If Quick Settings still shows the old name, disconnect and reconnect that device when convenient.

You can save the current name without changing the text to make an earlier temporary rename persistent. No startup script, scheduled task, or background process is needed.

**History** records changes made with this app. Select a verified change and choose **Restore previous name** to undo it. Restoration recovers both the previous API name and the exact previous persistent value, including an absent or empty value. It stops if either has changed since the recorded operation. Version 1.0 history has no persistent-value snapshot; those entries remain readable, but automatic undo is disabled. Enter their previous name on the Devices tab to restore that name instead.

The app works with remembered classic Bluetooth devices, including compatible headphones and speakers. Bluetooth LE-only devices are not listed by the Windows API used here. Names apply locally on this PC; they do not change the device's name on another computer or phone. Reboot persistence of the binary `FriendlyName` method was manually verified on Windows 11 with a Galaxy Buds FE device. This registry behavior is not a documented Microsoft persistence contract; unpairing, driver changes, or Windows updates may remove or replace a saved name.

## Keyboard and touch

- **F5** or **Ctrl+R**: refresh paired devices.
- **Ctrl+F**: filter by name or address.
- **Ctrl+Enter**: save the selected device's edited name.
- **Alt+N**: focus the new-name field.
- **Tab / Shift+Tab**: move between controls.
- **F1**: help.

Buttons have touch-sized targets. Narrow windows switch to a stacked, scrollable layout. The app declares **Per-Monitor V2 DPI awareness**, uses vector/WPF layout in device-independent units, and clamps windows to the available work area when display configuration changes. Windows high-contrast colors are respected when the app starts.

## Requirements and permissions

- Windows 11, x64; not restricted to a Lenovo model.
- .NET Framework 4.8 or newer. 
- No installer, PowerShell execution-policy changes, downloads, CUDA, NPU, or network connection required.
- Browsing devices and history uses standard user privileges. Saving/restoring names requires administrator access to the device's machine-wide registry value. **Enable saving…** or **Enable restore…** requests Windows elevation and opens the editing window; it does not save anything until you choose Save or Restore there. Cancelling approval leaves the current window open. The app never changes registry permissions.

WPF handles drawing with Windows graphics and can fall back to software rendering. Device discovery reads saved records without a radio inquiry or connecting devices. Work runs off the UI thread, and there is no background polling or scheduled task. Closing the window exits the app.

If Windows rejects its Bluetooth enumeration handle, the app reads only the saved classic-device address key names under BTHENUM and verifies each remembered device through the Bluetooth API.

## Local data and undo

History and per-device backups are stored in:

```text
%LOCALAPPDATA%\BluetoothRenamer\History
```

Before changing either name, the app durably records the old API name and exact `FriendlyName` bytes/existence, then exports only that device's corresponding BTHPORT and BTHENUM registry branches if they exist. It writes only `FriendlyName` under:

```text
HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\<12-digit-address>
```

The new value is `REG_BINARY`, encoded as UTF-8 with one terminating zero byte. A name can contain at most 247 UTF-8 bytes; accented characters and emoji may consume several bytes. Missing device keys and unexpected value types stop the operation. `Name`, `DeviceDesc`, and unrelated device values are not directly edited.

The app also updates the current name with Microsoft's `BluetoothUpdateDeviceRecord` API. Both the persistent bytes and current API name must match before a change is marked **Verified**. A partially applied or unverifiable operation is labelled **Uncertain** and retains its original snapshots; refresh and retry the intended name. Saving the API name alone is never reported as a successful persistent rename.

Registry imports alone are not proven to restore the API-managed alias. Use the app's restore function or enter the previous name manually. There is no telemetry or automatic update service.

To remove the app, close it and delete its portable folder. History remains in the user-data folder unless you separately remove it. The app contains no uninstaller or background service.

## Source and build

Source is included in `Source`. From Windows PowerShell:

```powershell
.\Build.ps1
```

Run that command from `Source`; the result is written to `Source\bin`. The build uses the Windows .NET Framework C# compiler and built-in libraries. It requires no NuGet restore or third-party packages. Alternatively use the included project in Visual Studio with .NET Framework 4.8 development support.

Run `Source\tests\Run-Tests.ps1` to check rename/restore safeguards with simulated devices and display geometry. These tests do not change real Bluetooth records. Test artifacts are saved in a temporary folder printed by the runner.

`--demo --data-dir "C:\path\to\temporary-folder"` starts an isolated preview with example devices. It never touches Bluetooth records. This is intended for development testing. `--diagnostics "C:\path\to\report.txt"` writes startup architecture, DPI, and device-list diagnostics when explicitly requested; these may include device names and addresses and are not sent anywhere.

The executable is locally built and unsigned; no publisher certificate is claimed.

## API references

- [BluetoothUpdateDeviceRecord](https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothupdatedevicerecord)
- [BluetoothFindFirstDevice](https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothfindfirstdevice)
- [Windows DPI awareness](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process)
