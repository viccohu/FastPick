using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.UI.Xaml.Media;

namespace FastPick.Models;

/// <summary>
/// 照片项模型 - 支持数据绑定
/// </summary>
public class PhotoItem : INotifyPropertyChanged
{
    private string _fileName = string.Empty;
    private string _jpgPath = string.Empty;
    private string? _rawPath;
    private int _rating;
    private bool _isMarkedForDeletion;
    private bool _isSelected;
    private FileTypeEnum _fileType;
    
    /// <summary>
    /// 缩略图加载取消令牌源（用于快速滚动时取消加载）
    /// </summary>
    public CancellationTokenSource? ThumbnailCts { get; set; }

    /// <summary>
    /// 文件名（不含扩展名）
    /// </summary>
    public string FileName
    {
        get => _fileName;
        set
        {
            if (_fileName != value)
            {
                _fileName = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// JPG 文件路径
    /// </summary>
    public string JpgPath
    {
        get => _jpgPath;
        set
        {
            if (_jpgPath != value)
            {
                _jpgPath = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// RAW 文件路径
    /// </summary>
    public string? RawPath
    {
        get => _rawPath;
        set
        {
            if (_rawPath != value)
            {
                _rawPath = value;
                OnPropertyChanged();
                UpdateFileType();
            }
        }
    }

    /// <summary>
    /// 评级 (0-5)
    /// </summary>
    public int Rating
    {
        get => _rating;
        set
        {
            if (_rating != value)
            {
                _rating = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 是否标记为预删除
    /// </summary>
    public bool IsMarkedForDeletion
    {
        get => _isMarkedForDeletion;
        set
        {
            if (_isMarkedForDeletion != value)
            {
                _isMarkedForDeletion = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 是否被选中
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 文件类型
    /// </summary>
    public FileTypeEnum FileType
    {
        get => _fileType;
        private set
        {
            if (_fileType != value)
            {
                _fileType = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 缩略图（运行时缓存）
    /// </summary>
    public ImageSource? Thumbnail { get; set; }

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 图片宽度
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// 图片高度
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// 图片尺寸（如 "6000x4000"）
    /// </summary>
    public string? Dimensions { get; set; }

    /// <summary>
    /// DPI
    /// </summary>
    public int Dpi { get; set; }

    /// <summary>
    /// 拍摄时间
    /// </summary>
    public DateTime? DateTimeTaken { get; set; }

    /// <summary>
    /// 相机型号
    /// </summary>
    public string? CameraModel { get; set; }

    /// <summary>
    /// 镜头型号
    /// </summary>
    public string? LensModel { get; set; }

    /// <summary>
    /// ISO 感光度
    /// </summary>
    public int? ISO { get; set; }

    /// <summary>
    /// 光圈值
    /// </summary>
    public double? FNumber { get; set; }

    /// <summary>
    /// 快门速度（秒）
    /// </summary>
    public double? ExposureTime { get; set; }

    /// <summary>
    /// 闪光灯状态码
    /// </summary>
    public int? Flash { get; set; }

    /// <summary>
    /// 曝光偏差
    /// </summary>
    public string? ExposureBias { get; set; }

    /// <summary>
    /// 修改日期
    /// </summary>
    public DateTime ModifiedDate { get; set; }

    /// <summary>
    /// 获取显示用的文件路径（优先 JPG，确保文件存在）
    /// </summary>
    public string DisplayPath => HasJpg ? JpgPath : (HasRaw ? RawPath ?? string.Empty : string.Empty);

    /// <summary>
    /// 是否存在 JPG
    /// </summary>
    public bool HasJpg => !string.IsNullOrEmpty(JpgPath) && System.IO.File.Exists(JpgPath);

    /// <summary>
    /// 是否存在 RAW
    /// </summary>
    public bool HasRaw => !string.IsNullOrEmpty(RawPath) && System.IO.File.Exists(RawPath);

    /// <summary>
    /// 更新文件类型
    /// </summary>
    private void UpdateFileType()
    {
        if (HasJpg && HasRaw)
            FileType = FileTypeEnum.Both;
        else if (HasJpg)
            FileType = FileTypeEnum.JpgOnly;
        else if (HasRaw)
            FileType = FileTypeEnum.RawOnly;
        else
            FileType = FileTypeEnum.JpgOnly;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// 文件类型枚举
/// </summary>
public enum FileTypeEnum
{
    JpgOnly,
    RawOnly,
    Both
}

/// <summary>
/// 删除选项枚举
/// </summary>
public enum DeleteOptionEnum
{
    Both,
    JpgOnly,
    RawOnly
}

/// <summary>
/// 导出选项枚举
/// </summary>
public enum ExportOptionEnum
{
    All,
    RatedOnly,
    JpgOnly,
    RawOnly,
    Both
}
