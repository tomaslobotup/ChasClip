using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BuscadorVideos.Models;

public enum DownloadQueueStatus
{
    Pending,     // En lista de espera
    Downloading, // Descargando actualmente
    Completed,   // Finalizado con éxito
    Failed,      // Error en la descarga
    Canceled     // Cancelado por el usuario
}

/// <summary>
/// Elemento de la cola / lista de espera de descargas.
/// </summary>
public class DownloadQueueItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _id = Guid.NewGuid().ToString("N");
    private string _title = "Video";
    private string _url = string.Empty;
    private string? _pageUrl;
    private string? _referer;
    private string? _origin;
    private string? _cookies;
    private bool _isHlsStream;
    private bool _isAudioOnly;
    private string _qualityLabel = "1080p";
    private int _targetHeight = 1080;
    private string _targetDirectory = string.Empty;
    private string? _destinationPath;
    private DownloadQueueStatus _status = DownloadQueueStatus.Pending;
    private double _progressPercent;
    private string _statusText = "En lista de espera...";
    private string? _errorMessage;
    private DateTime _createdAt = DateTime.UtcNow;
    private DateTime? _completedAt;

    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Url
    {
        get => _url;
        set => SetField(ref _url, value);
    }

    public string? PageUrl
    {
        get => _pageUrl;
        set => SetField(ref _pageUrl, value);
    }

    public string? Referer
    {
        get => _referer;
        set => SetField(ref _referer, value);
    }

    public string? Origin
    {
        get => _origin;
        set => SetField(ref _origin, value);
    }

    public string? Cookies
    {
        get => _cookies;
        set => SetField(ref _cookies, value);
    }

    public bool IsHlsStream
    {
        get => _isHlsStream;
        set => SetField(ref _isHlsStream, value);
    }

    public bool IsAudioOnly
    {
        get => _isAudioOnly;
        set => SetField(ref _isAudioOnly, value);
    }

    public string QualityLabel
    {
        get => _qualityLabel;
        set => SetField(ref _qualityLabel, value);
    }

    public int TargetHeight
    {
        get => _targetHeight;
        set => SetField(ref _targetHeight, value);
    }

    public string TargetDirectory
    {
        get => _targetDirectory;
        set => SetField(ref _targetDirectory, value);
    }

    public string? DestinationPath
    {
        get => _destinationPath;
        set => SetField(ref _destinationPath, value);
    }

    public DownloadQueueStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsActive));
            }
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetField(ref _progressPercent, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public DateTime CreatedAt
    {
        get => _createdAt;
        set => SetField(ref _createdAt, value);
    }

    public DateTime? CompletedAt
    {
        get => _completedAt;
        set => SetField(ref _completedAt, value);
    }

    public string FormatBadgeText => IsAudioOnly ? "MP3" : "MP4";

    public bool IsPending => Status == DownloadQueueStatus.Pending;
    public bool IsDownloading => Status == DownloadQueueStatus.Downloading;
    public bool IsCompleted => Status == DownloadQueueStatus.Completed;
    public bool IsFailed => Status == DownloadQueueStatus.Failed || Status == DownloadQueueStatus.Canceled;
    public bool IsActive => Status == DownloadQueueStatus.Pending || Status == DownloadQueueStatus.Downloading;

    public string FormattedTime => CreatedAt.ToLocalTime().ToString("HH:mm:ss");

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
