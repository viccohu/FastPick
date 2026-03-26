using System.IO;
using System.Runtime.InteropServices;

namespace FastPick.Services;

public enum DriveMediaType
{
    Unknown,
    Fixed,
    Removable,
    Network,
    CDRom
}

public class DriveTypeService
{
    private static readonly Lazy<DriveTypeService> _instance = new(() => new DriveTypeService());
    public static DriveTypeService Instance => _instance.Value;

    private readonly Dictionary<string, DriveMediaType> _driveTypeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    private DriveTypeService() { }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        System.Text.StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        System.Text.StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetDriveType(string lpRootPathName);

    private const int DRIVE_REMOVABLE = 2;
    private const int DRIVE_FIXED = 3;
    private const int DRIVE_REMOTE = 4;
    private const int DRIVE_CDROM = 5;

    public DriveMediaType GetDriveMediaType(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DriveMediaType.Unknown;

        try
        {
            var rootPath = GetRootPath(path);
            if (string.IsNullOrEmpty(rootPath))
                return DriveMediaType.Unknown;

            lock (_cacheLock)
            {
                if (_driveTypeCache.TryGetValue(rootPath, out var cachedType))
                    return cachedType;
            }

            var driveType = DetectDriveType(rootPath);

            lock (_cacheLock)
            {
                _driveTypeCache[rootPath] = driveType;
            }

            return driveType;
        }
        catch
        {
            return DriveMediaType.Unknown;
        }
    }

    public bool IsRemovableDrive(string path)
    {
        var mediaType = GetDriveMediaType(path);
        return mediaType == DriveMediaType.Removable;
    }

    public bool SupportsRecycleBin(string path)
    {
        var mediaType = GetDriveMediaType(path);
        return mediaType == DriveMediaType.Fixed || mediaType == DriveMediaType.Network;
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _driveTypeCache.Clear();
        }
    }

    private string GetRootPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return root;
        }
        catch
        {
            return string.Empty;
        }
    }

    private DriveMediaType DetectDriveType(string rootPath)
    {
        try
        {
            var driveType = GetDriveType(rootPath);

            return driveType switch
            {
                DRIVE_REMOVABLE => DriveMediaType.Removable,
                DRIVE_FIXED => DriveMediaType.Fixed,
                DRIVE_REMOTE => DriveMediaType.Network,
                DRIVE_CDROM => DriveMediaType.CDRom,
                _ => DriveMediaType.Unknown
            };
        }
        catch
        {
            return DriveMediaType.Unknown;
        }
    }

    public string GetDriveDescription(string path)
    {
        var mediaType = GetDriveMediaType(path);
        return mediaType switch
        {
            DriveMediaType.Removable => "可移动磁盘",
            DriveMediaType.Fixed => "本地磁盘",
            DriveMediaType.Network => "网络驱动器",
            DriveMediaType.CDRom => "光驱",
            _ => "未知类型"
        };
    }
}
