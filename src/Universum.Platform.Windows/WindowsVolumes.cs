using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Universum.Core.IO;

namespace Universum.Platform.Windows;

/// <summary>
/// The real volume of a path: a folder can be a mount point of another drive (a USB stick mounted in C:\Media), so the
/// drive letter alone does not tell whether a move is a rename or whether there is a Recycle Bin.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsVolumes
{
    /// <summary>Uses the real volumes for <see cref="Paths.SameVolume"/> (call once at start-up).</summary>
    public static void Register() => Paths.VolumeResolver = VolumeOf;

    /// <summary>Volume identifier ("\\?\Volume{…}\") of the path, or its mount point when that is unknown.</summary>
    public static string VolumeOf(string path)
    {
        var mountPoint = MountPoint(path);
        if (mountPoint is null)
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
        }

        var buffer = new char[64];
        return GetVolumeNameForVolumeMountPoint(mountPoint, buffer, buffer.Length) ? new string(buffer).TrimEnd('\0') : mountPoint;
    }

    /// <summary>The folder where the volume of the path is mounted ("C:\", "C:\Media\Usb\"); null when unknown.</summary>
    public static string? MountPoint(string path)
    {
        // the path may not exist yet (a new library folder): its nearest existing parent decides
        var existing = Path.GetFullPath(path);
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return null;
            }

            existing = parent;
        }

        var buffer = new char[existing.Length + 64];
        return GetVolumePathName(existing, buffer, buffer.Length) ? new string(buffer).TrimEnd('\0') : null;
    }

    /// <summary>Windows drive type of the volume (3 = fixed disk).</summary>
    public static uint DriveType(string mountPoint) => GetDriveType(mountPoint);

    /// <summary>Total size of the volume in bytes; null when unknown.</summary>
    public static long? TotalSize(string mountPoint) =>
        GetDiskFreeSpaceEx(mountPoint, out _, out var total, out _) ? (long)total : null;

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathName(string fileName, [Out] char[] volumePathName, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] volumeName, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string rootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total, out ulong free);
}
