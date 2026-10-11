using System.Runtime.Versioning;
using System.Security;

internal enum TargetAvailability { Present, Missing, Unknown }

[SupportedOSPlatform("windows")]
internal static class LocalTargetProbe
{
    // Walk from an available local volume. A false File.Exists result alone also
    // means access denied, a disconnected mapped drive, or offline storage.
    // Delegates let tests cover those states without changing drives or ACLs.
    internal static TargetAvailability Probe(string path,
        Func<string, bool>? readyLocalDrive = null,
        Func<string, FileAttributes>? readAttributes = null,
        Func<string, IEnumerable<string>>? readEntries = null)
    {
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim());
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                return TargetAvailability.Unknown;
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full)!;
            readyLocalDrive ??= IsReadyLocalDrive;
            readAttributes ??= File.GetAttributes;
            readEntries ??= Directory.EnumerateFileSystemEntries;
            if (!readyLocalDrive(root)) return TargetAvailability.Unknown;
            FileAttributes attributes = readAttributes(root);
            if (IsUnavailable(attributes)) return TargetAvailability.Unknown;

            string current = root;
            string[] pieces = full[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pieces.Length; i++)
            {
                string parent = current;
                current = Path.Combine(parent, pieces[i]);
                try { attributes = readAttributes(current); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Confirm absence by enumerating the accessible parent. Access
                    // errors, an entry still present, or a disappearing volume are unknown.
                    if (readEntries(parent).Any(entry => Path.GetFileName(entry).Equals(pieces[i], StringComparison.OrdinalIgnoreCase)))
                        return TargetAvailability.Unknown;
                    return readyLocalDrive(root) ? TargetAvailability.Missing : TargetAvailability.Unknown;
                }
                if (IsUnavailable(attributes)) return TargetAvailability.Unknown;
                if (i < pieces.Length - 1 && !attributes.HasFlag(FileAttributes.Directory))
                    return TargetAvailability.Unknown;
            }
            return TargetAvailability.Present;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or SecurityException)
        {
            return TargetAvailability.Unknown;
        }
    }

    private static bool IsUnavailable(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0;

    private static bool IsReadyLocalDrive(string root)
    {
        var drive = new DriveInfo(root);
        return (drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram) && drive.IsReady;
    }
}
