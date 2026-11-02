using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Universum.Core.IO;

namespace Universum.Platform.Windows;

/// <summary>
/// The physical disks behind a path and how they behave (owner, 2. 11. 2026 – práca na rôznych diskoch, "nie priemerné
/// riešenie"): two drive letters can be partitions of one disk (they must not be written at the same time), one volume can
/// span several disks (Storage Spaces, RAID – one lane for all of them), and a disk without seek penalty (SSD, NVMe) takes
/// two files at once without slowing down. Read from Windows itself (volume disk extents, the storage seek-penalty
/// property, the bus type); a path whose disks cannot be read keeps its volume as its disk.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsDisks
{
    private static readonly Dictionary<string, DiskIdentity> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>
    /// Uses the physical disks for the lanes of processing, the system's copy engine (block cloning on ReFS, unbuffered
    /// copies of large files) and a low input/output priority for background tools (call once at start-up).
    /// </summary>
    public static void Register()
    {
        Paths.DiskResolver = DiskOf;
        Paths.CopyFile = CopyAsync;
        Universum.Core.Processes.ProcessRunner.LowerPriority = LowerIoPriority;
        Paths.LinkCounter = LinksOf;
    }

    /// <summary>The names (hard links) of a file; 2 when they cannot be read (such a file is never edited in place).</summary>
    public static int LinksOf(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // BY_HANDLE_FILE_INFORMATION: NumberOfLinks after the attributes, three times, serial number and size
            var information = new byte[52];
            return GetFileInformationByHandle(handle, information) ? Math.Max(1, BitConverter.ToInt32(information, 40)) : 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 2;
        }
    }

    /// <summary>The disks of the volume a path lies on (read once per volume).</summary>
    public static DiskIdentity DiskOf(string path)
    {
        var volume = WindowsVolumes.VolumeOf(path);
        lock (Gate)
        {
            if (Known.TryGetValue(volume, out var known))
            {
                return known;
            }
        }

        var identity = Read(volume) ?? new DiskIdentity("volume:" + volume, SolidState: false, Detail: volume);
        lock (Gate)
        {
            Known[volume] = identity;
        }

        return identity;
    }

    // \\?\Volume{…}\ → the disk numbers of its extents, then each disk's seek penalty and bus
    private static DiskIdentity? Read(string volume)
    {
        if (!volume.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
        {
            // a network share or an unknown volume: one lane per share, never treated as a solid-state disk
            return volume.StartsWith(@"\\", StringComparison.Ordinal) ? new DiskIdentity("share:" + volume.TrimEnd('\\'), false, volume) : null;
        }

        var disks = Extents(volume.TrimEnd('\\'));
        if (disks.Count == 0)
        {
            return null;
        }

        var solid = true;
        var buses = new List<string>();
        foreach (var disk in disks)
        {
            var (seekPenalty, bus) = Describe(disk);
            solid &= seekPenalty == false;
            buses.Add(bus);
        }

        return new DiskIdentity("disk:" + string.Join("+", disks), solid, $"{string.Join("+", disks.Select(d => "#" + d))} {string.Join("/", buses.Distinct())}{(solid ? " SSD" : "")}");
    }

    private static List<int> Extents(string volume)
    {
        using var handle = CreateFile(volume, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return [];
        }

        // VOLUME_DISK_EXTENTS: count, then (disk number, offset, length) for each extent
        var buffer = new byte[8 + (24 * 32)];
        if (!DeviceIoControl(handle, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, buffer, buffer.Length, out _, IntPtr.Zero))
        {
            return [];
        }

        var count = BitConverter.ToInt32(buffer, 0);
        var disks = new SortedSet<int>();
        for (var i = 0; i < Math.Min(count, 32); i++)
        {
            disks.Add(BitConverter.ToInt32(buffer, 8 + (i * 24)));
        }

        return [.. disks];
    }

    // the seek penalty (false: SSD, NVMe) and the bus ("NVMe", "SATA", "USB" …) of a physical disk
    private static (bool? SeekPenalty, string Bus) Describe(int disk)
    {
        using var handle = CreateFile($@"\\.\PhysicalDrive{disk}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return (null, "?");
        }

        bool? seekPenalty = null;
        // STORAGE_PROPERTY_QUERY { PropertyId, QueryType = 0 (standard) }
        var query = new byte[12];
        BitConverter.GetBytes(StorageDeviceSeekPenaltyProperty).CopyTo(query, 0);
        var answer = new byte[12];
        if (DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, answer, answer.Length, out var returned, IntPtr.Zero) && returned >= 9)
        {
            // DEVICE_SEEK_PENALTY_DESCRIPTOR { Version, Size, IncursSeekPenalty }
            seekPenalty = answer[8] != 0;
        }

        var bus = "?";
        BitConverter.GetBytes(StorageDeviceProperty).CopyTo(query, 0);
        var device = new byte[1024];
        if (DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, device, device.Length, out returned, IntPtr.Zero) && returned >= 32)
        {
            // STORAGE_DEVICE_DESCRIPTOR: BusType at offset 28
            bus = BitConverter.ToInt32(device, 28) switch
            {
                1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 16 => "Spaces", 17 => "NVMe", 15 => "Virtual",
                var other => "bus " + other,
            };
        }

        return (seekPenalty, bus);
    }

    /// <summary>
    /// A background tool's input and output at a low priority (as Windows' own background tasks): the user's work on the
    /// same disk goes first, so the computer stays usable while files are written.
    /// </summary>
    public static void LowerIoPriority(Process process)
    {
        try
        {
            var low = 1; // IoPriorityLow
            _ = NtSetInformationProcess(process.Handle, ProcessIoPriority, ref low, sizeof(int));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or EntryPointNotFoundException or DllNotFoundException)
        {
            // it ended already, or the system does not offer it: the usual priority
        }
    }

    /// <summary>
    /// A copy by the system's copy engine: without the file cache for large files (the cache is not flooded by a film, the
    /// computer stays fast), block cloning on ReFS and Dev Drive volumes (a copy within one volume takes no time), the
    /// progress reported, never over an existing file.
    /// </summary>
    public static Task CopyAsync(string source, string target, IProgress<double>? progress, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var cancel = 0;
        using var registration = cancellationToken.Register(() => Volatile.Write(ref cancel, 1));
        var size = new FileInfo(source).Length;
        var flags = CopyFileFailIfExists | (size >= 256L * 1024 * 1024 ? CopyFileNoBuffering : 0);
        CopyProgressRoutine report = (total, transferred, _, _, _, _, _, _, _) =>
        {
            progress?.Report(total == 0 ? 1 : (double)transferred / total);
            return 0; // PROGRESS_CONTINUE
        };
        if (!CopyFileEx(Path.GetFullPath(source), Path.GetFullPath(target), report, IntPtr.Zero, ref cancel, flags))
        {
            var error = Marshal.GetLastPInvokeError();
            GC.KeepAlive(report);
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException($"'{source}' could not be copied to '{target}' (error {error}).", error);
        }

        GC.KeepAlive(report);
    }, cancellationToken);

    private const uint FileShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int ProcessIoPriority = 33;
    private const uint CopyFileFailIfExists = 0x00000001;
    private const uint CopyFileNoBuffering = 0x00001000;

    private delegate uint CopyProgressRoutine(
        long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred, uint streamNumber, uint callbackReason,
        IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, [Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string existing, string target, CopyProgressRoutine progress, IntPtr data, ref int cancel, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, [Out] byte[] information);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSetInformationProcess(IntPtr process, int informationClass, ref int information, int length);
}
