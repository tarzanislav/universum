using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Universum.Core.Execution;

namespace Universum.Platform.Windows;

/// <summary>
/// Moves files to the Windows Recycle Bin. A file is moved only where Windows really keeps it: on a fixed drive whose
/// Recycle Bin is switched on and large enough for the file. Should Windows still want to delete a file permanently,
/// it has to ask first (FOF_WANTNUKEWARNING); a refusal leaves the file in place.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    private const string BitBucketKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";

    private const uint DriveFixed = 3;

    public string? CannotRecycle(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "network location without a Recycle Bin";
        }

        try
        {
            // the real volume: a folder may be a mount point of a USB drive
            if (WindowsVolumes.MountPoint(full) is not { } mountPoint)
            {
                return "the drive of the file cannot be determined";
            }

            if (WindowsVolumes.DriveType(mountPoint) != DriveFixed)
            {
                return $"{mountPoint} is not a fixed disk and has no Recycle Bin";
            }

            var guid = VolumeGuid(mountPoint);
            if (guid is null)
            {
                return $"the Recycle Bin of {mountPoint} cannot be determined";
            }

            var (nukeOnDelete, maxCapacityMb) = BinSettings(guid);
            if (nukeOnDelete)
            {
                return $"the Recycle Bin of {mountPoint} deletes files immediately";
            }

            // without a stored size, assume the smallest default (5 % of the drive): an underestimate is safe
            var capacity = maxCapacityMb is { } mb ? mb << 20 : (WindowsVolumes.TotalSize(mountPoint) ?? 0) / 20;
            var size = File.Exists(full) ? new FileInfo(full).Length : 0;
            return size > capacity ? $"the file ({size >> 20} MB) is larger than the Recycle Bin of {mountPoint} ({capacity >> 20} MB)" : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "the Recycle Bin cannot be checked: " + ex.Message;
        }
    }

    public void MoveToRecycleBin(string path)
    {
        if (CannotRecycle(path) is { } reason)
        {
            throw new IOException($"'{path}' cannot go to the Recycle Bin: {reason}.");
        }

        var from = Marshal.StringToHGlobalUni(Path.GetFullPath(path) + "\0"); // the list ends with a second null
        try
        {
            var operation = new ShFileOpStruct
            {
                Func = FoDelete,
                From = from,
                Flags = FofAllowUndo | FofNoConfirmation | FofWantNukeWarning | FofSilent | FofNoErrorUi,
            };
            var result = SHFileOperation(ref operation);
            if (result != 0 || operation.AnyOperationsAborted != 0 || File.Exists(path))
            {
                throw new IOException($"'{path}' could not be moved to the Recycle Bin (code {result}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    /// <summary>Recycle Bin settings of a volume from the registry (per volume, or the old global values).</summary>
    private static (bool NukeOnDelete, long? MaxCapacityMb) BinSettings(string volumeGuid)
    {
        try
        {
            using var bitBucket = Registry.CurrentUser.OpenSubKey(BitBucketKey);
            using var volume = bitBucket?.OpenSubKey(@"Volume\" + volumeGuid);
            var nuke = volume?.GetValue("NukeOnDelete") ?? bitBucket?.GetValue("NukeOnDelete");
            var capacity = volume?.GetValue("MaxCapacity");
            return (nuke is int n && n != 0, capacity is int c && c > 0 ? c : null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (false, null);
        }
    }

    /// <summary>"{xxxxxxxx-…}" of the volume mounted at a mount point such as "C:\".</summary>
    private static string? VolumeGuid(string mountPoint)
    {
        var buffer = new char[64];
        if (!WindowsVolumes.GetVolumeNameForVolumeMountPoint(mountPoint, buffer, buffer.Length))
        {
            return null;
        }

        var name = new string(buffer).TrimEnd('\0');
        var start = name.IndexOf('{', StringComparison.Ordinal);
        var end = name.IndexOf('}', StringComparison.Ordinal);
        return start >= 0 && end > start ? name[start..(end + 1)] : null;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref ShFileOpStruct operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public IntPtr Window;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }
}
