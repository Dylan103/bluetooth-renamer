using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.Win32;

namespace BluetoothRenamer
{
    public interface IPersistentNameStore
    {
        // null means that FriendlyName is absent; [] and [0] remain distinct.
        byte[] Read(ulong address);
        // null restores absence by deleting FriendlyName only.
        void Write(ulong address, byte[] value);
    }

    public sealed class RegistryPersistentNameStore : IPersistentNameStore
    {
        private const string ValueName = "FriendlyName";

        public byte[] Read(ulong address)
        {
            string path = DevicePath(address);
            using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey device = machine.OpenSubKey(path, false))
            {
                RequireDevice(device);
                if (!HasExpectedKind(device)) return null;
                object value = device.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var bytes = value as byte[];
                if (bytes == null)
                    throw new IOException("The persistent Bluetooth alias changed while it was being read. Refresh and try again.");
                return (byte[])bytes.Clone();
            }
        }

        public void Write(ulong address, byte[] value)
        {
            string path = DevicePath(address);
            try
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey device = machine.OpenSubKey(path, true))
                {
                    RequireDevice(device);
                    HasExpectedKind(device);
                    if (value == null) device.DeleteValue(ValueName, false);
                    else device.SetValue(ValueName, (byte[])value.Clone(), RegistryValueKind.Binary);
                    device.Flush();
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException("Windows requires administrator access to save this persistent Bluetooth name. Enable saving and approve the Windows administrator prompt.", ex);
            }
            catch (SecurityException ex)
            {
                throw new UnauthorizedAccessException("Windows denied access to this device's persistent Bluetooth name. Enable saving and approve the Windows administrator prompt.", ex);
            }
        }

        private static string DevicePath(ulong address)
        {
            NameRules.ValidateAddress(address);
            return @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\" +
                address.ToString("x12", CultureInfo.InvariantCulture);
        }

        private static void RequireDevice(RegistryKey device)
        {
            if (device == null)
                throw new InvalidOperationException("Windows has no saved classic Bluetooth registry record for this device. Pair it in Windows Settings and refresh. No registry key was created.");
        }

        private static bool HasExpectedKind(RegistryKey device)
        {
            bool exists = device.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase);
            if (exists && device.GetValueKind(ValueName) != RegistryValueKind.Binary)
                throw new InvalidOperationException("This device's FriendlyName uses an unexpected registry type. It was left unchanged because this app requires REG_BINARY.");
            return exists;
        }
    }
}
