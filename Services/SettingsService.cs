using System.IO;
using System.Text.Json;
using FastPick.Services;

namespace FastPick.Services;

public enum PreviewLoadMode
{
    OnDemand,
    Hierarchical
}

public class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    public event EventHandler<FolderNameChangedEventArgs>? FolderNameChanged;

    public class FolderNameChangedEventArgs : EventArgs
    {
        public string? JpgFolderName { get; set; }
        public string? RawFolderName { get; set; }
    }

    private class SettingsData
    {
        public string Path1 { get; set; } = string.Empty;
        public string Path2 { get; set; } = string.Empty;
        public string ExportPath { get; set; } = string.Empty;
        public bool EnableRawHighResDecode { get; set; } = true;
        public bool AutoLoadLastPath { get; set; } = true;
        public string JpgFolderName { get; set; } = "JPG";
        public string RawFolderName { get; set; } = "RAW";
        public bool DeleteToRecycleBin { get; set; } = true;
        public bool UseRawForHighResDecode { get; set; } = false;
        public PreviewLoadMode PreviewLoadMode { get; set; } = PreviewLoadMode.OnDemand;
        public bool EnableBackgroundThumbnailDecoding { get; set; } = true;
    }

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FastPick",
        "settings.json");

    private SettingsData _data = new();
    private bool _isLoaded = false;

    private SettingsService() { }

    public string Path1
    {
        get
        {
            EnsureLoaded();
            return _data.Path1;
        }
        set
        {
            _data.Path1 = value ?? string.Empty;
            Save();
        }
    }

    public string Path2
    {
        get
        {
            EnsureLoaded();
            return _data.Path2;
        }
        set
        {
            _data.Path2 = value ?? string.Empty;
            Save();
        }
    }

    public string ExportPath
    {
        get
        {
            EnsureLoaded();
            return _data.ExportPath;
        }
        set
        {
            _data.ExportPath = value ?? string.Empty;
            Save();
        }
    }

    public bool EnableRawHighResDecode
    {
        get
        {
            EnsureLoaded();
            return _data.EnableRawHighResDecode;
        }
        set
        {
            _data.EnableRawHighResDecode = value;
            Save();
        }
    }

    public bool AutoLoadLastPath
    {
        get
        {
            EnsureLoaded();
            return _data.AutoLoadLastPath;
        }
        set
        {
            _data.AutoLoadLastPath = value;
            Save();
        }
    }

    public string JpgFolderName
    {
        get
        {
            EnsureLoaded();
            return _data.JpgFolderName;
        }
        set
        {
            var newValue = value ?? "JPG";
            if (_data.JpgFolderName != newValue)
            {
                _data.JpgFolderName = newValue;
                Save();
                FolderNameChanged?.Invoke(this, new FolderNameChangedEventArgs { JpgFolderName = newValue });
            }
        }
    }

    public string RawFolderName
    {
        get
        {
            EnsureLoaded();
            return _data.RawFolderName;
        }
        set
        {
            var newValue = value ?? "RAW";
            if (_data.RawFolderName != newValue)
            {
                _data.RawFolderName = newValue;
                Save();
                FolderNameChanged?.Invoke(this, new FolderNameChangedEventArgs { RawFolderName = newValue });
            }
        }
    }

    public bool DeleteToRecycleBin
    {
        get
        {
            EnsureLoaded();
            return _data.DeleteToRecycleBin;
        }
        set
        {
            _data.DeleteToRecycleBin = value;
            Save();
        }
    }

    public bool UseRawForHighResDecode
    {
        get
        {
            EnsureLoaded();
            return _data.UseRawForHighResDecode;
        }
        set
        {
            _data.UseRawForHighResDecode = value;
            Save();
        }
    }

    public PreviewLoadMode PreviewLoadMode
    {
        get
        {
            EnsureLoaded();
            return _data.PreviewLoadMode;
        }
        set
        {
            _data.PreviewLoadMode = value;
            Save();
        }
    }

    public bool EnableBackgroundThumbnailDecoding
    {
        get
        {
            EnsureLoaded();
            return _data.EnableBackgroundThumbnailDecoding;
        }
        set
        {
            _data.EnableBackgroundThumbnailDecoding = value;
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_isLoaded) return;
        
        Load();
        _isLoaded = true;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                _data = JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
            }
        }
        catch (System.Exception ex)
        {
            DebugService.WriteLine($"[SettingsService] 加载文件失败: {ex.Message}");
            _data = new SettingsData();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch (System.Exception ex)
        {
            DebugService.WriteLine($"[SettingsService] 保存文件失败: {ex.Message}");
        }
    }

    public void SaveAllPaths(string path1, string path2, string exportPath)
    {
        EnsureLoaded();
        
        _data.Path1 = path1 ?? string.Empty;
        _data.Path2 = path2 ?? string.Empty;
        _data.ExportPath = exportPath ?? string.Empty;
        
        Save();
    }
}
