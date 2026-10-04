using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BluetoothRenamer
{
    public sealed class DeviceInfo
    {
        public ulong Address { get; set; }
        public string AddressText { get; set; }
        public string Name { get; set; }
        public bool Connected { get; set; }
        public uint ClassOfDevice { get; set; }
        public string Kind { get; set; }
    }

    public interface IBluetoothService
    {
        List<DeviceInfo> ListDevices();
        DeviceInfo GetDevice(ulong address);
        DeviceInfo Rename(ulong address, string expectedName, string newName);
    }

    public static class NameRules
    {
        // The native API has a UTF-16 buffer, but the persistent Bluetooth name
        // uses a 248-byte UTF-8 buffer including its null terminator.
        public static void Validate(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Enter a name that is not blank.");
            for (int i = 0; i < name.Length; i++)
            {
                if (Char.IsControl(name[i]))
                    throw new ArgumentException("The name cannot contain control characters, tabs, or line breaks.");
                if (Char.IsHighSurrogate(name[i]))
                {
                    if (i + 1 >= name.Length || !Char.IsLowSurrogate(name[i + 1]))
                        throw new ArgumentException("The name contains an incomplete Unicode character.");
                    i++;
                }
                else if (Char.IsLowSurrogate(name[i]))
                    throw new ArgumentException("The name contains an incomplete Unicode character.");
            }
            if (Encoding.UTF8.GetByteCount(name) > 247)
                throw new ArgumentException("Use a name of at most 247 UTF-8 bytes. Accented characters and emoji may use several bytes each.");
        }

        public static byte[] EncodePersistentName(string name)
        {
            Validate(name);
            return Encoding.UTF8.GetBytes(name + "\0");
        }

        public static void ValidateAddress(ulong address)
        {
            if (address == 0 || address > 0xFFFFFFFFFFFFUL)
                throw new ArgumentException("The Bluetooth address must contain six bytes and cannot be zero.");
        }

        public static string FormatAddress(ulong address)
        {
            string hex = address.ToString("X12", CultureInfo.InvariantCulture);
            return String.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)).ToArray());
        }
    }

    public sealed class BluetoothService : IBluetoothService
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeSystemTime
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NativeDevice
        {
            public uint Size;
            public ulong Address;
            public uint ClassOfDevice;
            [MarshalAs(UnmanagedType.Bool)] public bool Connected;
            [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
            [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
            public NativeSystemTime LastSeen, LastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeSearch
        {
            public uint Size;
            [MarshalAs(UnmanagedType.Bool)] public bool ReturnAuthenticated;
            [MarshalAs(UnmanagedType.Bool)] public bool ReturnRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool ReturnUnknown;
            [MarshalAs(UnmanagedType.Bool)] public bool ReturnConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool IssueInquiry;
            public byte TimeoutMultiplier;
            public IntPtr Radio;
        }

        private sealed class FindHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public FindHandle() : base(true) { }
            protected override bool ReleaseHandle() { return BluetoothFindDeviceClose(handle); }
        }

        private sealed class RadioFindHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public RadioFindHandle() : base(true) { }
            protected override bool ReleaseHandle() { return BluetoothFindRadioClose(handle); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRadioSearch { public uint Size; }

        [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
        private static extern RadioFindHandle BluetoothFindFirstRadio(ref NativeRadioSearch search, out SafeFileHandle radio);
        [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BluetoothFindNextRadio(RadioFindHandle search, out SafeFileHandle radio);
        [DllImport("bthprops.cpl", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BluetoothFindRadioClose(IntPtr search);

        [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
        private static extern FindHandle BluetoothFindFirstDevice(ref NativeSearch search, ref NativeDevice device);
        [DllImport("bthprops.cpl", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BluetoothFindNextDevice(FindHandle handle, ref NativeDevice device);
        [DllImport("bthprops.cpl", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BluetoothFindDeviceClose(IntPtr handle);
        [DllImport("bthprops.cpl", ExactSpelling = true)]
        private static extern uint BluetoothGetDeviceInfo(IntPtr radio, ref NativeDevice device);
        [DllImport("bthprops.cpl", ExactSpelling = true)]
        private static extern uint BluetoothUpdateDeviceRecord([In] ref NativeDevice device);

        public BluetoothService()
        {
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Use the 64-bit version of Bluetooth Renamer.");
            if (Marshal.SizeOf(typeof(NativeDevice)) != 560 ||
                Marshal.OffsetOf(typeof(NativeDevice), "Name").ToInt32() != 64 ||
                Marshal.SizeOf(typeof(NativeSearch)) != 40)
                throw new PlatformNotSupportedException("Windows Bluetooth structure layout did not match the expected 64-bit layout.");
        }

        public List<DeviceInfo> ListDevices()
        {
            var result = new Dictionary<ulong, DeviceInfo>();
            try
            {
                var radioSearch = new NativeRadioSearch { Size = (uint)Marshal.SizeOf(typeof(NativeRadioSearch)) };
                SafeFileHandle radio;
                using (RadioFindHandle radios = BluetoothFindFirstRadio(ref radioSearch, out radio))
                {
                    int firstError = Marshal.GetLastWin32Error();
                    if (radios == null || radios.IsInvalid)
                    {
                        if (radio != null) radio.Dispose();
                        if (firstError != 259 && firstError != 1168)
                            throw WindowsError(firstError, "Windows could not find a Bluetooth radio");
                    }
                    else
                    {
                        while (true)
                        {
                            using (radio)
                            {
                                if (radio == null || radio.IsInvalid)
                                    throw WindowsError(6, "Windows returned an invalid Bluetooth radio");
                                EnumerateDeviceRecords(radio.DangerousGetHandle(), result);
                            }
                            if (!BluetoothFindNextRadio(radios, out radio))
                            {
                                int error = Marshal.GetLastWin32Error();
                                if (radio != null) radio.Dispose();
                                if (error != 259)
                                    throw WindowsError(error, "Windows could not finish listing Bluetooth radios");
                                break;
                            }
                        }
                    }
                }
            }
            catch (Win32Exception ex)
            {
                // Some Windows builds reject the classic enumeration handle even though
                // direct saved-record reads work. Discover addresses only, then ask the API.
                if (ex.NativeErrorCode != 6) throw;
                ReadSavedParentAddresses(result);
            }
            if (result.Count == 0) ReadSavedParentAddresses(result);
            return result.Values.OrderByDescending(d => d.Connected)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Address).ToList();
        }

        private static void ReadSavedParentAddresses(Dictionary<ulong, DeviceInfo> result)
        {
            using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey parents = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\BTHENUM", false))
            {
                if (parents == null) return;
                foreach (string key in parents.GetSubKeyNames())
                {
                    ulong address;
                    if (key.Length != 16 || !key.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase) ||
                        !UInt64.TryParse(key.Substring(4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address) || address == 0)
                        continue;
                    try { result[address] = ToDevice(ReadNative(address)); }
                    catch (Win32Exception ex) { if (ex.NativeErrorCode != 1168) throw; }
                    catch (InvalidOperationException) { /* A stale parent that is no longer remembered. */ }
                }
            }
        }

        private static void EnumerateDeviceRecords(IntPtr radio, Dictionary<ulong, DeviceInfo> result)
        {
            var search = new NativeSearch
            {
                Size = (uint)Marshal.SizeOf(typeof(NativeSearch)),
                ReturnAuthenticated = true,
                ReturnRemembered = true,
                ReturnConnected = true,
                ReturnUnknown = false,
                IssueInquiry = false,
                Radio = radio
            };
            NativeDevice device = CreateNative(0);
            using (FindHandle handle = BluetoothFindFirstDevice(ref search, ref device))
            {
                if (handle == null || handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) return;
                    throw WindowsError(error, "Windows could not list saved Bluetooth devices");
                }
                while (true)
                {
                    if (device.Remembered && device.Address != 0 && device.Address <= 0xFFFFFFFFFFFFUL)
                        result[device.Address] = ToDevice(device);
                    device = CreateNative(0);
                    if (!BluetoothFindNextDevice(handle, ref device))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 259)
                            throw WindowsError(error, "Windows could not finish listing Bluetooth devices");
                        break;
                    }
                }
            }
        }

        public DeviceInfo GetDevice(ulong address) { return ToDevice(ReadNative(address)); }

        public DeviceInfo Rename(ulong address, string expectedName, string newName)
        {
            NameRules.Validate(newName);
            NativeDevice record = ReadNative(address);
            if (!String.Equals(record.Name, expectedName, StringComparison.Ordinal))
                throw new InvalidOperationException("This device's name changed since it was selected. Refresh the list and try again.");
            record.Name = newName;
            uint error = BluetoothUpdateDeviceRecord(ref record);
            if (error != 0) throw WindowsError((int)error, "Windows could not update this Bluetooth name");
            DeviceInfo after = GetDevice(address);
            if (!String.Equals(after.Name, newName, StringComparison.Ordinal))
                throw new InvalidOperationException("Windows accepted the rename, but the new name could not be verified. Refresh the list before retrying.");
            return after;
        }

        private static NativeDevice ReadNative(ulong address)
        {
            NameRules.ValidateAddress(address);
            NativeDevice record = CreateNative(address);
            uint error = BluetoothGetDeviceInfo(IntPtr.Zero, ref record);
            if (error != 0) throw WindowsError((int)error, "Windows could not read this saved Bluetooth device");
            if (!record.Remembered)
                throw new InvalidOperationException("Windows no longer remembers this device. Pair it in Windows Settings and refresh the list.");
            return record;
        }

        private static NativeDevice CreateNative(ulong address)
        {
            return new NativeDevice { Size = (uint)Marshal.SizeOf(typeof(NativeDevice)), Address = address };
        }

        private static DeviceInfo ToDevice(NativeDevice record)
        {
            int major = (int)((record.ClassOfDevice >> 8) & 31);
            string kind = major == 4 ? "Audio" : major == 5 ? "Input device" : major == 2 ? "Phone" :
                major == 1 ? "Computer" : major == 6 ? "Imaging" : major == 7 ? "Wearable" : "Bluetooth device";
            return new DeviceInfo
            {
                Address = record.Address, AddressText = NameRules.FormatAddress(record.Address),
                Name = record.Name ?? String.Empty, Connected = record.Connected,
                ClassOfDevice = record.ClassOfDevice, Kind = kind
            };
        }

        private static Exception WindowsError(int error, string message)
        {
            string hint = error == 5 ? " Close the app and use Run as administrator if Windows requires elevated access." :
                (error == 1168 || error == 1062 || error == 4319) ? " Check that Bluetooth is enabled and the device is still paired." : String.Empty;
            return new Win32Exception(error, message + ". " + new Win32Exception(error).Message + " (Windows error " + error + ")." + hint);
        }
    }

    [DataContract]
    public sealed class HistoryEntry
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public DateTime TimeUtc { get; set; }
        [DataMember] public ulong Address { get; set; }
        [DataMember] public string AddressText { get; set; }
        [DataMember] public string OldName { get; set; }
        [DataMember] public string NewName { get; set; }
        [DataMember] public string Status { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public string BackupDirectory { get; set; }
        [DataMember] public bool HasPersistentSnapshot { get; set; }
        // null records an absent value; zero-length and a single 00 are distinct.
        [DataMember] public byte[] OldAliasBytes { get; set; }
        [DataMember] public byte[] NewAliasBytes { get; set; }
    }

    public sealed class RenameResult
    {
        public DeviceInfo Device { get; set; }
        public HistoryEntry Entry { get; set; }
        public string Warning { get; set; }
    }

    public sealed class RenameOperationException : InvalidOperationException
    {
        public HistoryEntry Entry { get; private set; }
        public RenameOperationException(string message, HistoryEntry entry, Exception inner) : base(message, inner) { Entry = entry; }
    }

    public sealed class HistoryStore
    {
        private readonly Action<ulong, string> backupAction;
        public string RootDirectory { get; private set; }
        public string LastLoadWarning { get; private set; }

        public HistoryStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BluetoothRenamer"), CaptureRegistryBackups) { }
        public HistoryStore(string rootDirectory) : this(rootDirectory, CaptureRegistryBackups) { }
        public HistoryStore(string rootDirectory, Action<ulong, string> backupAction)
        {
            if (String.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("A history folder is required.");
            if (backupAction == null) throw new ArgumentNullException("backupAction");
            RootDirectory = Path.GetFullPath(rootDirectory);
            this.backupAction = backupAction;
        }

        public List<HistoryEntry> GetEntries()
        {
            LastLoadWarning = null;
            var entries = new List<HistoryEntry>();
            string history = Path.Combine(RootDirectory, "History");
            if (!Directory.Exists(history)) return entries;
            int unreadable = 0;
            foreach (string directory in Directory.GetDirectories(history))
            {
                string path = Path.Combine(directory, "entry.json");
                if (!File.Exists(path)) continue;
                try
                {
                    HistoryEntry entry;
                    using (var input = File.OpenRead(path))
                        entry = (HistoryEntry)new DataContractJsonSerializer(typeof(HistoryEntry)).ReadObject(input);
                    if (entry == null || entry.Id != Path.GetFileName(directory)) throw new InvalidDataException();
                    NameRules.ValidateAddress(entry.Address);
                    entry.BackupDirectory = directory;
                    entry.AddressText = NameRules.FormatAddress(entry.Address);
                    if (entry.Status == "Prepared" || entry.Status == "Updating")
                    {
                        entry.Status = "Uncertain";
                        entry.Message = "This operation did not record a final outcome. Refresh the device before deciding whether to retry or undo it.";
                    }
                    entries.Add(entry);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException || ex is ArgumentException || ex is System.Xml.XmlException)) throw;
                    unreadable++;
                }
            }
            if (unreadable != 0) LastLoadWarning = unreadable + " history record(s) could not be read; the files were preserved.";
            return entries.OrderByDescending(e => e.TimeUtc).ToList();
        }

        internal HistoryEntry Prepare(DeviceInfo before, string newName)
        {
            return PrepareCore(before, newName, false, null, null);
        }

        internal HistoryEntry Prepare(DeviceInfo before, string newName, byte[] oldAliasBytes, byte[] newAliasBytes)
        {
            return PrepareCore(before, newName, true, oldAliasBytes, newAliasBytes);
        }

        private HistoryEntry PrepareCore(DeviceInfo before, string newName, bool hasSnapshot, byte[] oldAliasBytes, byte[] newAliasBytes)
        {
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var entry = new HistoryEntry
            {
                Id = id, TimeUtc = DateTime.UtcNow, Address = before.Address,
                AddressText = NameRules.FormatAddress(before.Address), OldName = before.Name, NewName = newName,
                Status = "Prepared", Message = "Original cached name and persistent alias saved; rename has not started.",
                HasPersistentSnapshot = hasSnapshot,
                OldAliasBytes = oldAliasBytes == null ? null : (byte[])oldAliasBytes.Clone(),
                NewAliasBytes = newAliasBytes == null ? null : (byte[])newAliasBytes.Clone(),
                BackupDirectory = Path.Combine(RootDirectory, "History", id)
            };
            Directory.CreateDirectory(entry.BackupDirectory);
            Save(entry);
            return entry;
        }

        internal void Backup(HistoryEntry entry) { backupAction(entry.Address, entry.BackupDirectory); }

        internal void Save(HistoryEntry entry)
        {
            string expected = Path.Combine(RootDirectory, "History", entry.Id);
            if (!String.Equals(Path.GetFullPath(expected), Path.GetFullPath(entry.BackupDirectory), StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(entry.Id) != entry.Id)
                throw new InvalidOperationException("Invalid history record location.");
            AtomicJson(Path.Combine(expected, "entry.json"), entry);
        }

        internal static void AtomicJson<T>(string destination, T value)
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    new DataContractJsonSerializer(typeof(T)).WriteObject(file, value);
                    file.Flush(true);
                }
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        [DataContract]
        private sealed class BackupRecord
        {
            [DataMember] public string Key { get; set; }
            [DataMember] public string Status { get; set; }
            [DataMember] public string File { get; set; }
            [DataMember] public string Sha256 { get; set; }
        }

        private static void CaptureRegistryBackups(ulong address, string directory)
        {
            NameRules.ValidateAddress(address);
            string hex = address.ToString("X12", CultureInfo.InvariantCulture);
            string[] keys =
            {
                @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\" + hex.ToLowerInvariant(),
                @"SYSTEM\CurrentControlSet\Enum\BTHENUM\DEV_" + hex
            };
            string[] names = { "BTHPORT-before.reg", "BTHENUM-before.reg" };
            var records = new List<BackupRecord>();
            for (int i = 0; i < keys.Length; i++)
            {
                string fullKey = @"HKEY_LOCAL_MACHINE\" + keys[i];
                bool exists;
                try
                {
                    using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (RegistryKey key = machine.OpenSubKey(keys[i], false)) exists = key != null;
                }
                catch (Exception ex)
                {
                    throw new IOException("Could not read the selected device's registry key for backup. No rename was attempted. If access was denied, reopen the app with Run as administrator.", ex);
                }
                if (!exists)
                {
                    records.Add(new BackupRecord { Key = fullKey, Status = "Key not present" });
                    continue;
                }
                string destination = Path.Combine(directory, names[i]);
                string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
                // The key is generated solely from a validated numeric address; the file is under an app-created folder.
                var start = new ProcessStartInfo(executable, "export " + Quote(fullKey) + " " + Quote(destination))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                var output = new StringBuilder();
                using (var process = new Process { StartInfo = start })
                {
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                    process.Start();
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    if (!process.WaitForExit(30000))
                    {
                        process.Kill(); process.WaitForExit();
                        throw new IOException("The device-specific registry backup timed out. No rename was attempted.");
                    }
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        throw new IOException("The device-specific registry backup failed. No rename was attempted. " + output.ToString().Trim() +
                            " If Windows denied access, reopen the app with Run as administrator.");
                }
                string contents = File.ReadAllText(destination);
                if (!contents.StartsWith("Windows Registry Editor Version 5.00", StringComparison.Ordinal) ||
                    contents.IndexOf("[" + fullKey + "]", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new IOException("The device-specific backup could not be verified. No rename was attempted.");
                string hash;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(destination))
                    hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty);
                records.Add(new BackupRecord { Key = fullKey, Status = "Backed up", File = names[i], Sha256 = hash });
            }
            AtomicJson(Path.Combine(directory, "registry-backups.json"), records);
        }

        private static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
                throw new ArgumentException("A backup path contained unsupported characters.");
            return "\"" + value + "\"";
        }
    }

    public sealed class RenameCoordinator
    {
        private readonly IBluetoothService service;
        private readonly HistoryStore history;
        private readonly IPersistentNameStore aliases;

        public RenameCoordinator(IBluetoothService service, HistoryStore history)
            : this(service, history, new RegistryPersistentNameStore()) { }

        public RenameCoordinator(IBluetoothService service, HistoryStore history, IPersistentNameStore aliasStore)
        {
            if (service == null) throw new ArgumentNullException("service");
            if (history == null) throw new ArgumentNullException("history");
            if (aliasStore == null) throw new ArgumentNullException("aliasStore");
            this.service = service; this.history = history; aliases = aliasStore;
        }

        public RenameResult Undo(HistoryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (!entry.HasPersistentSnapshot)
                throw new InvalidOperationException("This older history entry has no persistent-alias backup, so it cannot be undone safely. Select the device and save the name you want instead.");
            NameRules.ValidateAddress(entry.Address); NameRules.Validate(entry.OldName);
            return RunLocked(entry.Address, delegate
            {
                DeviceInfo current = service.GetDevice(entry.Address);
                if (!String.Equals(current.Name, entry.NewName, StringComparison.Ordinal))
                    throw new InvalidOperationException("Undo stopped because the device no longer has the name from this history entry. Refresh and choose the name you want manually.");
                return RenameLocked(current, entry.OldName, Copy(entry.OldAliasBytes), true, Copy(entry.NewAliasBytes));
            });
        }

        public RenameResult Rename(DeviceInfo selected, string newName)
        {
            if (selected == null) throw new ArgumentNullException("selected");
            NameRules.ValidateAddress(selected.Address); NameRules.Validate(newName);
            byte[] newAlias = NameRules.EncodePersistentName(newName);
            return RunLocked(selected.Address, delegate { return RenameLocked(selected, newName, newAlias, false, null); });
        }

        private static RenameResult RunLocked(ulong address, Func<RenameResult> operation)
        {
            using (var gate = new Mutex(false, @"Local\BluetoothRenamer-" + address.ToString("X12", CultureInfo.InvariantCulture)))
            {
                bool entered = false;
                try
                {
                    try { entered = gate.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { entered = true; }
                    if (!entered) throw new InvalidOperationException("Another rename for this device is running. Wait for it to finish and refresh.");
                    return operation();
                }
                finally { if (entered) gate.ReleaseMutex(); }
            }
        }

        private RenameResult RenameLocked(DeviceInfo selected, string newName, byte[] newAlias, bool checkExpectedAlias, byte[] expectedAlias)
        {
            DeviceInfo before = service.GetDevice(selected.Address);
            if (before.Address != selected.Address || !String.Equals(before.Name, selected.Name, StringComparison.Ordinal))
                throw new InvalidOperationException("This device's name changed since it was selected. Refresh the list before renaming it.");
            byte[] oldAlias = Copy(aliases.Read(before.Address));
            if (checkExpectedAlias && !EqualBytes(oldAlias, expectedAlias))
                throw new InvalidOperationException("Undo stopped because this device's persistent alias changed since the history entry. Refresh and choose the name you want manually.");
            if (String.Equals(before.Name, newName, StringComparison.Ordinal) && EqualBytes(oldAlias, newAlias))
                return new RenameResult { Device = before, Warning = "This name is already saved in both the Windows Bluetooth cache and the persistent alias." };
            HistoryEntry entry = history.Prepare(before, newName, oldAlias, newAlias);
            try
            {
                history.Backup(entry);
                // Recheck after backup so stale selections or external changes never
                // get silently overwritten by this two-part operation.
                DeviceInfo latest = service.GetDevice(before.Address);
                if (latest.Address != before.Address || !String.Equals(latest.Name, before.Name, StringComparison.Ordinal) ||
                    !EqualBytes(aliases.Read(before.Address), oldAlias))
                    throw new InvalidOperationException("The device's cached name or persistent alias changed while its backup was being made. Refresh and try again.");
                entry.Status = "Updating"; entry.Message = "Device-specific backups completed; waiting for both the persistent alias and API name to be verified.";
                history.Save(entry);
            }
            catch (Exception ex)
            {
                entry.Status = "Failed"; entry.Message = "No rename was attempted. " + ex.Message;
                string warning = TrySave(entry);
                throw new RenameOperationException(entry.Message + warning, entry, ex);
            }

            DeviceInfo after;
            string operationWarning = null;
            try
            {
                if (!EqualBytes(oldAlias, newAlias)) aliases.Write(before.Address, Copy(newAlias));
                if (!EqualBytes(aliases.Read(before.Address), newAlias))
                    throw new IOException("The persistent alias write could not be verified.");
                if (!String.Equals(before.Name, newName, StringComparison.Ordinal))
                    service.Rename(before.Address, before.Name, newName);
                // Do not rely on an update method's success return; independently
                // read both stores before declaring the operation verified.
                after = service.GetDevice(before.Address);
                if (after.Address != before.Address || !String.Equals(after.Name, newName, StringComparison.Ordinal) ||
                    !EqualBytes(aliases.Read(before.Address), newAlias))
                    throw new IOException("The requested name was not verified in both the Bluetooth cache and the persistent alias.");
            }
            catch (Exception ex)
            {
                after = null;
                byte[] observedAlias = null;
                bool aliasRead = false;
                try { after = service.GetDevice(before.Address); } catch { }
                try { observedAlias = aliases.Read(before.Address); aliasRead = true; } catch { }
                bool validDevice = after != null && after.Address == before.Address;
                if (validDevice && aliasRead && String.Equals(after.Name, newName, StringComparison.Ordinal) && EqualBytes(observedAlias, newAlias))
                    operationWarning = "Windows reported a problem during the update, but independent reads verified both the cached name and persistent alias. " + ex.Message;
                else
                {
                    bool originalState = validDevice && aliasRead && String.Equals(after.Name, before.Name, StringComparison.Ordinal) && EqualBytes(observedAlias, oldAlias);
                    entry.Status = originalState ? "Failed" : "Uncertain";
                    entry.Message = ex.Message + (originalState ? " Both the original cached name and original persistent alias are still present." :
                        " Only part of the rename may have completed, or the current values could not be read. The exact original values are backed up; refresh before retrying. No automatic rollback overwrote the current state.");
                    string warning = TrySave(entry);
                    throw new RenameOperationException(entry.Message + warning, entry, ex);
                }
            }
            entry.Status = "Verified"; entry.Message = "Verified by independently reading the Windows Bluetooth cache and the exact persistent alias after saving.";
            string saveWarning = TrySave(entry);
            return new RenameResult { Device = after, Entry = entry, Warning = JoinWarnings(operationWarning, saveWarning) };
        }

        private static byte[] Copy(byte[] value) { return value == null ? null : (byte[])value.Clone(); }

        private static bool EqualBytes(byte[] first, byte[] second)
        {
            if (first == null || second == null) return first == null && second == null;
            return first.SequenceEqual(second);
        }

        private string TrySave(HistoryEntry entry)
        {
            try { history.Save(entry); return null; }
            catch (Exception ex)
            {
                entry.Status = "Uncertain";
                return " The final history update could not be saved: " + ex.Message +
                    " The original-name backup is preserved at " + entry.BackupDirectory + ". Refresh the device before retrying or undoing.";
            }
        }

        private static string JoinWarnings(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first)) return String.IsNullOrWhiteSpace(second) ? null : second.Trim();
            return String.IsNullOrWhiteSpace(second) ? first : first + " " + second.Trim();
        }
    }
}
