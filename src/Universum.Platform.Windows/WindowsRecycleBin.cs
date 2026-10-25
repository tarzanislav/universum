using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Universum.Core.Execution;

namespace Universum.Platform.Windows;

/// <summary>Moves files to the Windows Recycle Bin (SHFileOperation with undo).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>Local fixed and removable drives have a Recycle Bin; network shares do not (deleting there is permanent).</summary>
    public bool CanRecycle(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return new DriveInfo(Path.GetPathRoot(full)!).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void MoveToRecycleBin(string path)
    {
        var from = Marshal.StringToHGlobalUni(Path.GetFullPath(path) + "\0"); // the list ends with a second null
        try
        {
            var operation = new ShFileOpStruct
            {
                Func = FoDelete,
                From = from,
                Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
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
