using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Gestiona la cola y lista de espera secuencial de descargas multimedia en segundo plano con historial persistente.
/// </summary>
public class DownloadQueueService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly VideoQualityAndDownloadService _downloadService;
    private readonly string _historyFilePath;
    private readonly object _lock = new();

    private readonly SemaphoreSlim _queueSemaphore = new(1, 1);
    private CancellationTokenSource? _currentCts;
    private DownloadQueueItem? _currentlyDownloadingItem;

    public ObservableCollection<DownloadQueueItem> Items { get; } = [];

    public event Action? QueueChanged;

    public DownloadQueueService(VideoQualityAndDownloadService downloadService)
    {
        _downloadService = downloadService;
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuscadorVideos");
        Directory.CreateDirectory(appDataDir);
        _historyFilePath = Path.Combine(appDataDir, "historial_descargas_cola.json");

        LoadHistory();
    }

    public DownloadQueueItem Enqueue(
        string url,
        string title,
        bool isAudioOnly,
        string qualityLabel,
        int targetHeight,
        string targetDirectory,
        string? referer = null,
        string? origin = null,
        string? cookies = null,
        string? pageUrl = null,
        bool isHlsStream = false)
    {
        var item = new DownloadQueueItem
        {
            Url = url.Trim(),
            PageUrl = pageUrl,
            Referer = referer,
            Origin = origin,
            Cookies = cookies,
            IsHlsStream = isHlsStream,
            Title = string.IsNullOrWhiteSpace(title) ? (isAudioOnly ? "Audio MP3" : "Video MP4") : title.Trim(),
            IsAudioOnly = isAudioOnly,
            QualityLabel = qualityLabel,
            TargetHeight = targetHeight,
            TargetDirectory = targetDirectory,
            Status = DownloadQueueStatus.Pending,
            StatusText = "En lista de espera..."
        };

        RunOnUi(() =>
        {
            Items.Insert(0, item);
        });

        SaveHistory();
        QueueChanged?.Invoke();

        // Iniciar procesamiento en background si no está corriendo
        _ = ProcessQueueAsync();

        return item;
    }

    public void CancelItem(DownloadQueueItem item)
    {
        lock (_lock)
        {
            if (item == _currentlyDownloadingItem)
            {
                _currentCts?.Cancel();
            }
            else if (item.Status == DownloadQueueStatus.Pending)
            {
                item.Status = DownloadQueueStatus.Canceled;
                item.StatusText = "Cancelado";
                SaveHistory();
                QueueChanged?.Invoke();
            }
        }
    }

    public void RetryItem(DownloadQueueItem item)
    {
        lock (_lock)
        {
            if (item.Status == DownloadQueueStatus.Failed || item.Status == DownloadQueueStatus.Canceled)
            {
                item.Status = DownloadQueueStatus.Pending;
                item.StatusText = "En lista de espera...";
                item.ProgressPercent = 0;
                item.ErrorMessage = null;
                SaveHistory();
                QueueChanged?.Invoke();
                _ = ProcessQueueAsync();
            }
        }
    }

    public void RemoveItem(DownloadQueueItem item)
    {
        lock (_lock)
        {
            CancelItem(item);
            RunOnUi(() => Items.Remove(item));
            SaveHistory();
            QueueChanged?.Invoke();
        }
    }

    public void ClearCompleted()
    {
        lock (_lock)
        {
            RunOnUi(() =>
            {
                var toRemove = Items.Where(x => x.Status == DownloadQueueStatus.Completed || x.Status == DownloadQueueStatus.Canceled).ToList();
                foreach (var it in toRemove)
                    Items.Remove(it);
            });
            SaveHistory();
            QueueChanged?.Invoke();
        }
    }

    private async Task ProcessQueueAsync()
    {
        // Intentar adquirir el semáforo sin bloquear hilo UI
        if (!await _queueSemaphore.WaitAsync(0))
            return;

        try
        {
            while (true)
            {
                DownloadQueueItem? nextItem;
                lock (_lock)
                {
                    // Tomar el elemento pendiente más antiguo
                    nextItem = Items
                        .Where(x => x.Status == DownloadQueueStatus.Pending)
                        .OrderBy(x => x.CreatedAt)
                        .FirstOrDefault();

                    if (nextItem == null)
                        break;

                    _currentlyDownloadingItem = nextItem;
                    _currentCts = new CancellationTokenSource();
                }

                var ct = _currentCts.Token;

                RunOnUi(() =>
                {
                    nextItem.Status = DownloadQueueStatus.Downloading;
                    nextItem.StatusText = "Iniciando descarga...";
                    nextItem.ProgressPercent = 2;
                });
                QueueChanged?.Invoke();

                var progress = new Progress<(double Percent, string Status)>(p =>
                {
                    RunOnUi(() =>
                    {
                        nextItem.ProgressPercent = Math.Max(0, Math.Min(100, p.Percent));
                        nextItem.StatusText = p.Status;
                    });
                });

                try
                {
                    var qualityOpt = new QualityOption
                    {
                        Label = nextItem.QualityLabel,
                        TargetHeight = nextItem.TargetHeight,
                        YouTubeQualityCode = nextItem.TargetHeight > 0 ? $"hd{nextItem.TargetHeight}" : "default"
                    };

                    var downloadedFile = await _downloadService.DownloadDirectUrlAsync(
                        nextItem.Url,
                        nextItem.IsAudioOnly,
                        qualityOpt,
                        nextItem.TargetDirectory,
                        progress,
                        ct,
                        isPlaylist: false,
                        customTitle: nextItem.Title,
                        customReferer: nextItem.Referer,
                        customOrigin: nextItem.Origin,
                        customCookies: nextItem.Cookies,
                        pageUrl: nextItem.PageUrl,
                        isHlsStream: nextItem.IsHlsStream);

                    RunOnUi(() =>
                    {
                        nextItem.DestinationPath = downloadedFile;
                        nextItem.Status = DownloadQueueStatus.Completed;
                        nextItem.StatusText = "✓ Descarga completada con éxito";
                        nextItem.ProgressPercent = 100;
                        nextItem.CompletedAt = DateTime.UtcNow;

                        // Si el título original era una URL cruda o genérico, tomar el nombre del archivo descargado
                        if (File.Exists(downloadedFile) &&
                            (nextItem.Title.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                             nextItem.Title.Equals("Video MP4", StringComparison.OrdinalIgnoreCase) ||
                             nextItem.Title.Equals("Audio MP3", StringComparison.OrdinalIgnoreCase)))
                        {
                            var fn = Path.GetFileNameWithoutExtension(downloadedFile);
                            if (!string.IsNullOrWhiteSpace(fn))
                                nextItem.Title = fn;
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    RunOnUi(() =>
                    {
                        nextItem.Status = DownloadQueueStatus.Canceled;
                        nextItem.StatusText = "Descarga cancelada";
                    });
                }
                catch (Exception ex)
                {
                    RunOnUi(() =>
                    {
                        nextItem.Status = DownloadQueueStatus.Failed;
                        nextItem.ErrorMessage = ex.Message;
                        nextItem.StatusText = $"Error: {ex.Message}";
                    });
                }
                finally
                {
                    lock (_lock)
                    {
                        _currentlyDownloadingItem = null;
                        _currentCts?.Dispose();
                        _currentCts = null;
                    }
                    SaveHistory();
                    QueueChanged?.Invoke();
                }

                // Pequeño intervalo de respiro entre descargas
                await Task.Delay(500);
            }
        }
        finally
        {
            _queueSemaphore.Release();
        }
    }

    private void LoadHistory()
    {
        lock (_lock)
        {
            if (File.Exists(_historyFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_historyFilePath);
                    var list = JsonSerializer.Deserialize<List<DownloadQueueItem>>(json, JsonOptions);
                    if (list != null)
                    {
                        foreach (var it in list.OrderByDescending(x => x.CreatedAt).Take(50))
                        {
                            // Si quedó marcado como en progreso por cierre previo inesperado, marcar como pendiente o cancelado
                            if (it.Status == DownloadQueueStatus.Downloading)
                            {
                                it.Status = DownloadQueueStatus.Failed;
                                it.StatusText = "Interrumpido al cerrar la app";
                            }
                            Items.Add(it);
                        }
                    }
                }
                catch
                {
                }
            }
        }
    }

    private void SaveHistory()
    {
        lock (_lock)
        {
            try
            {
                var list = Items.Take(60).ToList();
                var json = JsonSerializer.Serialize(list, JsonOptions);
                File.WriteAllText(_historyFilePath, json);
            }
            catch
            {
            }
        }
    }

    private static void RunOnUi(Action action)
    {
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }
}
