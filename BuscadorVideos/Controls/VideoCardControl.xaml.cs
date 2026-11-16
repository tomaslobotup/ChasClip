using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BuscadorVideos.Models;
using BuscadorVideos.Services;

namespace BuscadorVideos.Controls;

/// <summary>
/// Tarjeta individual de video para la cuadrícula de 3x4.
/// Muestra portada estática con fallback multicapa, preview animada/video al pasar el mouse y título en su idioma original.
/// </summary>
public partial class VideoCardControl : UserControl
{
    public static readonly DependencyProperty VideoProperty =
        DependencyProperty.Register(
            nameof(Video),
            typeof(VideoResult),
            typeof(VideoCardControl),
            new PropertyMetadata(null, OnVideoChanged));

    public VideoResult? Video
    {
        get => (VideoResult?)GetValue(VideoProperty);
        set => SetValue(VideoProperty, value);
    }

    public event EventHandler<VideoResult>? VideoSelected;
    public event EventHandler<VideoResult>? FavoriteToggled;

    private static readonly FavoritesService _favoritesService = new();
    public bool IsFavorite { get; private set; }

    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _hoverCts;
    private DispatcherTimer? _frameTimer;
    private IReadOnlyList<AnimatedFrame>? _activeFrames;
    private int _currentFrameIndex;
    private bool _isHovered;

    public VideoCardControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateFavoriteVisual(IsFavorite);
    }

    private static void OnVideoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VideoCardControl control)
        {
            control.BindVideo(e.NewValue as VideoResult);
        }
    }

    private async void BindVideo(VideoResult? video)
    {
        StopHoverPreview();
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = null;

        CoverImage.BeginAnimation(OpacityProperty, null);
        CoverImage.Opacity = 0;
        CoverImage.Source = null;
        BackdropCoverImage.Source = null;
        PlaceholderPanel.Visibility = Visibility.Visible;
        PlaceholderSubtext.Visibility = Visibility.Collapsed;

        if (video == null)
        {
            TitleTextBlock.Text = string.Empty;
            SubtitleTextBlock.Text = string.Empty;
            IsFavorite = false;
            UpdateFavoriteVisual(false);
            return;
        }

        IsFavorite = _favoritesService.IsFavorite(video.VideoPageUrl);
        UpdateFavoriteVisual(IsFavorite);

        TitleTextBlock.Text = video.Title;
        TitleTextBlock.ToolTip = video.Title;
        SubtitleTextBlock.Text = video.SubtitleText;
        SiteBadgeText.Text = video.SourceSiteName;

        var accentColor = AppThemeService.ParseColor(AppThemeService.PaletteNeutralHex, Color.FromRgb(155, 160, 171));
        var accentBrush = new SolidColorBrush(accentColor);
        SiteAccentDot.Fill = accentBrush;
        PreviewProgressBar.Foreground = accentBrush;

        if (!string.IsNullOrWhiteSpace(video.Duration))
        {
            DurationText.Text = video.Duration;
            DurationBadge.Visibility = Visibility.Visible;
        }
        else
        {
            DurationBadge.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(video.ThumbnailUrl) || video.FallbackFrameUrls.Count > 0)
        {
            var cts = new CancellationTokenSource();
            _thumbCts = cts;
            var bmp = await HoverPreviewService.LoadBestThumbnailAsync(video, cts.Token);
            if (!cts.IsCancellationRequested && Video == video)
            {
                if (bmp != null)
                {
                    CoverImage.Source = bmp;
                    BackdropCoverImage.Source = bmp;
                    PlaceholderPanel.Visibility = Visibility.Collapsed;
                    CoverImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)));
                }
                else
                {
                    PlaceholderSubtext.Text = video.SourceSiteName;
                    PlaceholderSubtext.Visibility = Visibility.Visible;
                }
            }
        }
        else
        {
            PlaceholderSubtext.Text = video.SourceSiteName;
            PlaceholderSubtext.Visibility = Visibility.Visible;
        }
    }

    private async void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        var video = Video;
        if (video == null) return;

        _isHovered = true;
        AnimateCardHover(true, video.AccentHex);

        _hoverCts?.Cancel();
        _hoverCts?.Dispose();
        var cts = new CancellationTokenSource();
        _hoverCts = cts;

        try
        {
            await Task.Delay(170, cts.Token);
            if (!_isHovered || cts.IsCancellationRequested) return;

            PreviewStatusText.Text = "⏳ PREVIEW...";
            PreviewStatusBadge.Background = ParseBrush(AppThemeService.PaletteAccentHex, Color.FromRgb(57, 0, 7), 0.94);
            PreviewStatusBadge.Visibility = Visibility.Visible;
            StartSubtleCoverZoom();

            var payload = await HoverPreviewService.PreparePreviewAsync(video, cts.Token);
            if (!_isHovered || cts.IsCancellationRequested || Video != video)
                return;

            if (payload?.LocalMp4Path != null)
            {
                PreviewStatusText.Text = "▶ PREVIEW";
                PreviewProgressBar.IsIndeterminate = true;
                PreviewProgressBar.Visibility = Visibility.Visible;

                PreviewMediaElement.Source = new Uri(payload.LocalMp4Path, UriKind.Absolute);
                PreviewMediaElement.Visibility = Visibility.Visible;
                PreviewMediaElement.Play();
            }
            else if (payload?.Frames is { Count: > 1 } frames)
            {
                PreviewStatusText.Text = "▶ PREVIEW";
                StartFrameSequenceAnimation(frames);
            }
            else
            {
                PreviewStatusText.Text = "▶ REPRODUCIR";
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelación normal al quitar el mouse
        }
        catch
        {
            // Ignorar fallos de red puntuales en preview
        }
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHovered = false;
        AnimateCardHover(false, null);
        StopHoverPreview();
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        _isHovered = false;
        _thumbCts?.Cancel();
        StopHoverPreview();
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Video != null)
        {
            StopHoverPreview();
            VideoSelected?.Invoke(this, Video);
        }
    }

    private void StartFrameSequenceAnimation(IReadOnlyList<AnimatedFrame> frames)
    {
        _frameTimer?.Stop();
        _activeFrames = frames;
        _currentFrameIndex = 0;

        PreviewFrameImage.Source = frames[0].Bitmap;
        PreviewFrameImage.Visibility = Visibility.Visible;

        PreviewProgressBar.IsIndeterminate = false;
        PreviewProgressBar.Maximum = frames.Count;
        PreviewProgressBar.Value = 1;
        PreviewProgressBar.Visibility = Visibility.Visible;

        _frameTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(frames[0].DelayMs)
        };
        _frameTimer.Tick += FrameTimer_Tick;
        _frameTimer.Start();
    }

    private void FrameTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isHovered || _activeFrames == null || _activeFrames.Count == 0 || _frameTimer == null)
        {
            StopHoverPreview();
            return;
        }

        _currentFrameIndex = (_currentFrameIndex + 1) % _activeFrames.Count;
        var frame = _activeFrames[_currentFrameIndex];
        PreviewFrameImage.Source = frame.Bitmap;
        PreviewProgressBar.Value = _currentFrameIndex + 1;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(frame.DelayMs);
    }

    private void PreviewMediaElement_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!_isHovered)
        {
            StopHoverPreview();
        }
    }

    private void PreviewMediaElement_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_isHovered)
        {
            PreviewMediaElement.Position = TimeSpan.Zero;
            PreviewMediaElement.Play();
        }
    }

    private void PreviewMediaElement_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        PreviewMediaElement.Visibility = Visibility.Collapsed;
    }

    private void StopHoverPreview()
    {
        _hoverCts?.Cancel();
        _hoverCts?.Dispose();
        _hoverCts = null;

        if (_frameTimer != null)
        {
            _frameTimer.Stop();
            _frameTimer.Tick -= FrameTimer_Tick;
            _frameTimer = null;
        }

        _activeFrames = null;
        PreviewFrameImage.Visibility = Visibility.Collapsed;
        PreviewFrameImage.Source = null;

        try
        {
            PreviewMediaElement.Stop();
            PreviewMediaElement.Close();
            PreviewMediaElement.Source = null;
        }
        catch
        {
            // Ignorar si MediaElement no tenía recurso abierto
        }

        PreviewMediaElement.Visibility = Visibility.Collapsed;
        PreviewStatusBadge.Visibility = Visibility.Collapsed;
        PreviewProgressBar.IsIndeterminate = false;
        PreviewProgressBar.Visibility = Visibility.Collapsed;

        CoverZoomTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CoverZoomTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CoverZoomTransform.ScaleX = 1.0;
        CoverZoomTransform.ScaleY = 1.0;
    }

    private void AnimateCardHover(bool hovered, string? accentHex)
    {
        double targetScale = hovered ? 1.022 : 1.0;
        var dur = new Duration(TimeSpan.FromMilliseconds(160));

        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(targetScale, dur));
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(targetScale, dur));

        if (hovered)
        {
            CardBorder.BorderBrush = Application.Current?.Resources["AppAccentBorderBrush"] as Brush
                                     ?? ParseBrush(AppThemeService.PaletteNeutralHex, Color.FromRgb(155, 160, 171));
        }
        else
        {
            CardBorder.ClearValue(Border.BorderBrushProperty);
        }
    }

    private void StartSubtleCoverZoom()
    {
        var anim = new DoubleAnimation(1.0, 1.08, new Duration(TimeSpan.FromSeconds(2.8)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        CoverZoomTransform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        CoverZoomTransform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Video == null) return;
        IsFavorite = _favoritesService.ToggleFavorite(Video);
        UpdateFavoriteVisual(IsFavorite);
        FavoriteToggled?.Invoke(this, Video);
    }

    private void UpdateFavoriteVisual(bool isFav)
    {
        var cDark = AppThemeService.ParseColor(AppThemeService.PaletteDarkHex, Color.FromRgb(14, 14, 15));
        var cAccent = AppThemeService.ParseColor(AppThemeService.PaletteAccentHex, Color.FromRgb(57, 0, 7));
        var cNeutral = AppThemeService.ParseColor(AppThemeService.PaletteNeutralHex, Color.FromRgb(155, 160, 171));
        var cLight = AppThemeService.ParseColor(AppThemeService.PaletteLightHex, Color.FromRgb(240, 244, 245));

        if (FavoriteButton.Template?.FindName("favIcon", FavoriteButton) is TextBlock tb)
        {
            tb.Text = isFav ? "★" : "☆";
            tb.Foreground = new SolidColorBrush(cLight);
        }
        if (FavoriteButton.Template?.FindName("favBd", FavoriteButton) is Border bd)
        {
            bd.BorderBrush = isFav
                ? new SolidColorBrush(cLight)
                : new SolidColorBrush(AppThemeService.WithAlpha(cNeutral, 120));
            bd.Background = isFav
                ? new SolidColorBrush(AppThemeService.WithAlpha(cAccent, 235))
                : new SolidColorBrush(AppThemeService.WithAlpha(cDark, 190));
        }
        FavoriteButton.ToolTip = isFav ? "Quitar de favoritos" : "Marcar como favorito";
    }

    private static SolidColorBrush ParseBrush(string? hex, Color fallback, double opacity = 1.0)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c)
            {
                return new SolidColorBrush(c) { Opacity = opacity };
            }
        }
        catch
        {
            // Usar color por defecto
        }

        return new SolidColorBrush(fallback) { Opacity = opacity };
    }
}
