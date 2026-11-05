using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BuscadorVideos.Models;
using BuscadorVideos.Services;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace BuscadorVideos;

/// <summary>
/// Ventana principal de BUSCAPP — Buscador Multisitio Universal de Videos.
/// Coordina búsqueda simultánea, panel lateral colapsable, bloqueador de anuncios con advertencia,
/// historial desplegable borrable, reversión de búsquedas y sitios borrados, y reproductor con regulador de calidad y descarga.
/// </summary>
public partial class MainWindow : Window, IBrowserScraper
{
    public static readonly DependencyProperty SelectedGridColumnsProperty =
        DependencyProperty.Register(
            nameof(SelectedGridColumns),
            typeof(int),
            typeof(MainWindow),
            new PropertyMetadata(3));

    public int SelectedGridColumns
    {
        get => (int)GetValue(SelectedGridColumnsProperty);
        set => SetValue(SelectedGridColumnsProperty, value);
    }

    private int VideosPerPage => SelectedGridColumns switch
    {
        6 => 24, // 6 x 4
        4 => 16, // 4 x 4
        _ => 12  // 3 x 4
    };

    private const double ExpandedSidebarWidth = 340.0;
    private const string CafecitoDonationUrl = "https://cafecito.app/atomichead";

    private readonly AppSettingsService _settingsService = new();
    private readonly SiteStorageService _storageService = new();
    private readonly PornDudeCatalogService _nsfwService = new();
    private readonly AdBlockerService _adBlocker = new();
    private readonly VideoQualityAndDownloadService _qualityAndDownloadService = new();
    private readonly VideoSearchCoordinator _searchCoordinator;
    private readonly FavoritesService _favoritesService = new();
    private readonly PlaybackHistoryService _playbackHistoryService = new();

    private readonly ObservableCollection<SavedSite> _savedSites = [];
    private readonly ObservableCollection<NsfwCategory> _nsfwCategories = [];
    private readonly ObservableCollection<SearchHistoryEntry> _searchHistory = [];
    private readonly ObservableCollection<RecentDownloadItem> _recentDownloads = [];
    private readonly Stack<(SavedSite Site, int OriginalIndex)> _deletedSitesUndoStack = new();
    private readonly Stack<SearchSessionSnapshot> _previousSearchesStack = new();

    private CancellationTokenSource? _downloaderCts;
    private string _currentDownloadDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads");
    private string? _lastDownloadedFilePath;
    private readonly ConcurrentQueue<(string Url, string? Referer)> _interceptedPlayerMediaUrls = new();
    private readonly SemaphoreSlim _scraperLock = new(1, 1);

    private AppSettings _appSettings;
    private List<VideoResult> _allSearchResults = [];
    private List<VideoResult> _filteredResults = [];
    private List<SavedSite> _lastSearchedSites = [];
    private List<DiscoveredVideoStream> _activeVideoStreams = [];
    private string _currentSearchQuery = string.Empty;
    private string? _lastIncompatibleDirectUrl;
    private string? _selectedFilterSiteId;
    private int _currentPage = 1;

    private SavedSite? _siteBeingEdited;
    private VideoResult? _activePlayerVideo;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _downloadCts;
    private CoreWebView2Environment? _webViewEnv;
    private bool _playerWebViewReady;
    private bool _scraperWebViewReady;
    private bool _isNsfwGlowing;
    private bool _isSidebarCollapsed;
    private bool _suppressQualitySelectionHandler;

    public MainWindow()
    {
        _appSettings = _settingsService.LoadSettings();
        _searchCoordinator = new VideoSearchCoordinator(this);
        InitializeComponent();
        _adBlocker.IsEnabled = _appSettings.IsAdBlockerEnabled;

        // En la primera carga tras actualizar a V2, si la lista estaba vacía, poblar la lista predeterminada verificada
        bool forceSeedDefaults = !_appSettings.DefaultsSeededV2;
        var loadedSites = _storageService.LoadSites(forceSeedIfEmpty: forceSeedDefaults);
        if (!_appSettings.DefaultsSeededV2)
        {
            _appSettings.DefaultsSeededV2 = true;
            _settingsService.SaveSettings(_appSettings);
        }

        foreach (var site in loadedSites)
            _savedSites.Add(site);

        foreach (var cat in _nsfwService.LoadCachedCategories())
            _nsfwCategories.Add(cat);

        foreach (var entry in _settingsService.LoadSearchHistory())
            _searchHistory.Add(entry);

        _isNsfwGlowing = _nsfwCategories.SelectMany(c => c.Sites).Any(s => s.IsEnabled);

        SavedSitesItemsControl.ItemsSource = _savedSites;
        NsfwCategoriesItemsControl.ItemsSource = _nsfwCategories;
        SearchHistoryItemsControl.ItemsSource = _searchHistory;

        InitializeQualitySelector();
        ApplySidebarStateImmediate(_appSettings.IsSidebarCollapsed);
        UpdateAdBlockerButtonAppearance();
        UpdateNsfwButtonAppearance();
        UpdateUndoDeletedSiteButtonState();
        UpdateSearchSessionButtonsState();
        UpdateSaveStatusText();

        SelectedGridColumns = _appSettings.GridColumns is 3 or 4 or 6 ? _appSettings.GridColumns : 3;
        UpdateGridDensityButtons();

        // Inicializar directorio de descargas y opciones del apartado de descargas
        if (!Directory.Exists(_currentDownloadDirectory))
        {
            _currentDownloadDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (!Directory.Exists(_currentDownloadDirectory))
                _currentDownloadDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }

        if (!string.IsNullOrWhiteSpace(_appSettings.DefaultDownloadDirectory) && Directory.Exists(_appSettings.DefaultDownloadDirectory))
        {
            _currentDownloadDirectory = _appSettings.DefaultDownloadDirectory;
        }

        DownloaderFolderTextBox.Text = _currentDownloadDirectory;
        DownloaderQualityComboBox.ItemsSource = QualityOption.DefaultOptions;
        DownloaderQualityComboBox.SelectedIndex = 0;
        DownloaderRecentItemsControl.ItemsSource = _recentDownloads;

        if (Enum.TryParse<AppTheme>(_appSettings.Theme, out var currentTheme))
        {
            AppThemeService.ApplyTheme(currentTheme, _appSettings.CustomAccentColor);
        }
        else
        {
            AppThemeService.ApplyTheme(AppTheme.Dark);
        }
        UpdateIncognitoBadgeVisual();

        ToggleNsfwPanelButton.Visibility = _appSettings.IsAdultContentEnabled ? Visibility.Visible : Visibility.Collapsed;
        LocalizationService.CurrentLanguage = _appSettings.Language;
        ApplyLanguage();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var animTask = PlayStartupAnimationAsync();
        var webViewTask = InitializeWebViewsAsync();
        await animTask;
        await webViewTask;
    }

    // ==================== ANIMACIÓN DE APERTURA DEL PROGRAMA ====================

    private async Task PlayStartupAnimationAsync()
    {
        try
        {
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var introDur = new Duration(TimeSpan.FromMilliseconds(420));

            SplashContentPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, introDur) { EasingFunction = easeOut });
            SplashScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1.0, introDur) { EasingFunction = easeOut });
            SplashScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1.0, introDur) { EasingFunction = easeOut });

            await Task.Delay(720);

            var outroDur = new Duration(TimeSpan.FromMilliseconds(360));
            MainLayoutGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, outroDur) { EasingFunction = easeOut });
            MainLayoutTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, outroDur) { EasingFunction = easeOut });

            var splashFadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(320))) { EasingFunction = easeOut };
            SplashScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.05, outroDur) { EasingFunction = easeOut });
            SplashScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.05, outroDur) { EasingFunction = easeOut });
            StartupSplashOverlay.BeginAnimation(OpacityProperty, splashFadeOut);

            await Task.Delay(330);
            StartupSplashOverlay.Visibility = Visibility.Collapsed;
        }
        catch
        {
            MainLayoutGrid.Opacity = 1;
            MainLayoutTranslate.Y = 0;
            StartupSplashOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // ==================== PANEL LATERAL OCULTABLE ====================

    private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e)
        => SetSidebarCollapsed(!_isSidebarCollapsed, animate: true);

    private void ApplySidebarStateImmediate(bool collapsed)
    {
        _isSidebarCollapsed = collapsed;
        SidebarColumn.Width = new GridLength(collapsed ? 0 : ExpandedSidebarWidth);
        SidebarBorder.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarBorder.Opacity = collapsed ? 0 : 1;
        SidebarTranslate.X = 0;
        UpdateSidebarToggleButtons();
    }

    private async void SetSidebarCollapsed(bool collapse, bool animate)
    {
        if (_isSidebarCollapsed == collapse)
            return;

        _isSidebarCollapsed = collapse;
        _appSettings.IsSidebarCollapsed = collapse;
        _settingsService.SaveSettings(_appSettings);
        UpdateSidebarToggleButtons();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(220));

        if (!animate)
        {
            ApplySidebarStateImmediate(collapse);
            return;
        }

        if (collapse)
        {
            SidebarBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, dur) { EasingFunction = ease });
            SidebarTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -40, dur) { EasingFunction = ease });
            await Task.Delay(200);
            if (_isSidebarCollapsed)
            {
                SidebarColumn.Width = new GridLength(0);
                SidebarBorder.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            SidebarBorder.Visibility = Visibility.Visible;
            SidebarColumn.Width = new GridLength(ExpandedSidebarWidth);
            SidebarBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            SidebarTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-40, 0, dur) { EasingFunction = ease });
        }
    }

    private void UpdateSidebarToggleButtons()
    {
        TopToggleSidebarButton.Content = _isSidebarCollapsed ? "☰ Panel" : "☰";
        TopToggleSidebarButton.ToolTip = _isSidebarCollapsed
            ? "Mostrar panel lateral de páginas (Ctrl+B)"
            : "Ocultar panel lateral de páginas (Ctrl+B)";
    }

    // ==================== INICIALIZACIÓN DE MOTORES WEBVIEW2 ====================

    private async Task InitializeWebViewsAsync()
    {
        try
        {
            var userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BuscadorVideos",
                "WebView2Data");

            Directory.CreateDirectory(userDataDir);
            var envOptions = new CoreWebView2EnvironmentOptions("--disable-backgrounding-occluded-windows --disable-renderer-backgrounding");
            _webViewEnv = await CoreWebView2Environment.CreateAsync(null, userDataDir, envOptions);

            await ScraperWebView.EnsureCoreWebView2Async(_webViewEnv);
            ScraperWebView.CoreWebView2.IsMuted = true;
            ScraperWebView.CoreWebView2.Settings.UserAgent = HttpHelper.BrowserUserAgent;
            await AttachAdBlockerAndSnifferAsync(ScraperWebView.CoreWebView2, sniffMediaStreams: false);
            _scraperWebViewReady = true;

            await PlayerWebView.EnsureCoreWebView2Async(_webViewEnv);
            PlayerWebView.CoreWebView2.Settings.UserAgent = HttpHelper.BrowserUserAgent;
            await AttachAdBlockerAndSnifferAsync(PlayerWebView.CoreWebView2, sniffMediaStreams: true);
            PlayerWebView.CoreWebView2.NavigationCompleted += PlayerWebView_NavigationCompleted;
            _playerWebViewReady = true;
        }
        catch
        {
            // El motor HTTP nativo permanece operativo si el runtime WebView2 no estuviera instalado
        }
    }

    // ==================== BLOQUEADOR DE ANUNCIOS CON ADVERTENCIA AL DESACTIVAR ====================

    private async Task AttachAdBlockerAndSnifferAsync(CoreWebView2 wv, bool sniffMediaStreams)
    {
        wv.NewWindowRequested += (_, args) =>
        {
            if (_adBlocker.IsEnabled)
                args.Handled = true;
        };

        await wv.AddScriptToExecuteOnDocumentCreatedAsync(
            AdBlockerService.BuildDocumentCreatedProtectionScript(_adBlocker.IsEnabled));

        wv.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        wv.WebResourceRequested += (_, args) =>
        {
            var uri = args.Request.Uri;
            if (string.IsNullOrEmpty(uri)) return;

            if (_adBlocker.ShouldBlockRequest(uri))
            {
                if (_webViewEnv != null)
                    args.Response = _webViewEnv.CreateWebResourceResponse(Stream.Null, 204, "No Content", "");
                return;
            }

            // Inyectar Referer en solicitudes a Dailymotion para evitar bloqueo 403 Forbidden
            if (uri.Contains("dailymotion.com", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!args.Request.Headers.Contains("Referer") || string.IsNullOrWhiteSpace(args.Request.Headers.GetHeader("Referer")))
                    {
                        args.Request.Headers.SetHeader("Referer", "https://www.dailymotion.com/");
                    }
                }
                catch { }
            }

            if (sniffMediaStreams &&
                (uri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                 uri.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
                 uri.Contains(".webm", StringComparison.OrdinalIgnoreCase)))
            {
                string? reqRef = null;
                try
                {
                    if (args.Request.Headers.Contains("Referer"))
                        reqRef = args.Request.Headers.GetHeader("Referer");
                }
                catch { }

                if (_interceptedPlayerMediaUrls.Count < 40)
                    _interceptedPlayerMediaUrls.Enqueue((uri, reqRef));
            }
        };
    }

    private void ToggleAdBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_adBlocker.IsEnabled)
        {
            // Mostrar advertencia explícita de que desactivarlo puede causar errores
            AdBlockWarningModalOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            SetAdBlockerState(enabled: true);
        }
    }

    private void CancelDisableAdBlock_Click(object sender, RoutedEventArgs e)
    {
        AdBlockWarningModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void ConfirmDisableAdBlock_Click(object sender, RoutedEventArgs e)
    {
        AdBlockWarningModalOverlay.Visibility = Visibility.Collapsed;
        SetAdBlockerState(enabled: false);
    }

    private async void SetAdBlockerState(bool enabled)
    {
        _adBlocker.IsEnabled = enabled;
        _appSettings.IsAdBlockerEnabled = enabled;
        _settingsService.SaveSettings(_appSettings);
        UpdateAdBlockerButtonAppearance();

        var syncJs = $"window.__buscappAdBlockEnabled = {(enabled ? "true" : "false")};";
        try
        {
            if (_scraperWebViewReady && ScraperWebView.CoreWebView2 != null)
                await ScraperWebView.CoreWebView2.ExecuteScriptAsync(syncJs);
            if (_playerWebViewReady && PlayerWebView.CoreWebView2 != null)
                await PlayerWebView.CoreWebView2.ExecuteScriptAsync(syncJs);
        }
        catch { }
    }

    private void UpdateAdBlockerButtonAppearance()
    {
        if (_adBlocker.IsEnabled)
        {
            ToggleAdBlockButton.Content = "🛡️ AdBlock: ON";
            ToggleAdBlockButton.ToolTip = "Bloqueador de anuncios y popups ACTIVO (evita errores). Haz clic si deseas desactivarlo.";
            ToggleAdBlockButton.Style = (Style)FindResource("AdBlockOnButtonStyle");
        }
        else
        {
            ToggleAdBlockButton.Content = "⚠️ AdBlock: OFF";
            ToggleAdBlockButton.ToolTip = "ADVERTENCIA: Bloqueador desactivado (los anuncios externos pueden causar errores). Haz clic para reactivarlo.";
            ToggleAdBlockButton.Style = (Style)FindResource("AdBlockOffButtonStyle");
        }
    }

    // ==================== DONACIONES (CAFECITO) ====================

    private void DonateCafecitoButton_Click(object sender, RoutedEventArgs e)
        => OpenUrlInExternalBrowser(CafecitoDonationUrl);

    // ==================== LISTA EDITABLE, PREDETERMINADAS Y REVERTIR BORRADO ====================

    private void AddSiteButton_Click(object sender, RoutedEventArgs e) => AddSiteFromInput();

    private void NewSiteInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddSiteFromInput();
            e.Handled = true;
        }
    }

    private void AddSiteFromInput()
    {
        var nameInput = NewSiteNameTextBox.Text.Trim();
        var urlInput = NewSiteUrlTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(urlInput))
        {
            NewSiteUrlTextBox.Focus();
            return;
        }

        try
        {
            var newSite = SavedSite.Create(nameInput, urlInput);

            var existing = _savedSites.FirstOrDefault(s =>
                string.Equals(s.Url, newSite.Url, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Domain, newSite.Domain, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.IsEnabled = true;
                existing.Url = newSite.Url;
                existing.DisplayName = newSite.DisplayName;
            }
            else
            {
                _savedSites.Add(newSite);
            }

            NewSiteNameTextBox.Clear();
            NewSiteUrlTextBox.Clear();
            PersistSavedSites($"✓ Guardado: {newSite.DisplayName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Enlace no válido", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SiteCheckBox_Click(object sender, RoutedEventArgs e) => PersistSavedSites();

    private void SelectAllSites_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in _savedSites)
            s.IsEnabled = true;
        PersistSavedSites("✓ Todas activadas");
    }

    private void DeselectAllSites_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in _savedSites)
            s.IsEnabled = false;
        PersistSavedSites("✓ Desmarcadas");
    }

    private void RestoreDefaultSites_Click(object sender, RoutedEventArgs e)
    {
        var defaults = SiteStorageService.CreateDefaultVerifiedSites();
        int added = 0;

        foreach (var def in defaults)
        {
            bool exists = _savedSites.Any(s => string.Equals(s.RootDomain, def.RootDomain, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                _savedSites.Add(def);
                added++;
            }
        }

        PersistSavedSites(added > 0
            ? $"✓ {added} buscadores predeterminados restaurados"
            : "✓ Los buscadores base ya están en tu lista");
    }

    private void DeleteSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SavedSite site })
        {
            int idx = _savedSites.IndexOf(site);
            if (idx >= 0)
            {
                _deletedSitesUndoStack.Push((site.Clone(), idx));
                _savedSites.RemoveAt(idx);
                UpdateUndoDeletedSiteButtonState();
                PersistSavedSites($"✓ Eliminado: {site.DisplayName}");
            }
        }
    }

    private void UndoDeletedSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deletedSitesUndoStack.Count == 0)
            return;

        var (restoredSite, originalIndex) = _deletedSitesUndoStack.Pop();
        var duplicate = _savedSites.FirstOrDefault(s =>
            string.Equals(s.Url, restoredSite.Url, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.RootDomain, restoredSite.RootDomain, StringComparison.OrdinalIgnoreCase));

        if (duplicate == null)
        {
            int insertAt = Math.Clamp(originalIndex, 0, _savedSites.Count);
            _savedSites.Insert(insertAt, restoredSite);
        }
        else
        {
            duplicate.IsEnabled = restoredSite.IsEnabled;
        }

        UpdateUndoDeletedSiteButtonState();
        PersistSavedSites($"✓ Restaurado: {restoredSite.DisplayName}");
    }

    private void UpdateUndoDeletedSiteButtonState()
    {
        if (_deletedSitesUndoStack.TryPeek(out var top))
        {
            UndoDeletedSiteButton.IsEnabled = true;
            UndoDeletedSiteButton.Content = $"↶ Revertir borrado ({top.Site.DisplayName})";
            UndoDeletedSiteButton.ToolTip = $"Restaurar '{top.Site.DisplayName}' en tu lista ({_deletedSitesUndoStack.Count} en historial de borrado)";
        }
        else
        {
            UndoDeletedSiteButton.IsEnabled = false;
            UndoDeletedSiteButton.Content = "↶ Revertir borrado en mi lista";
            UndoDeletedSiteButton.ToolTip = "No hay elementos eliminados recientemente";
        }
    }

    private void EditSiteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SavedSite site })
        {
            _siteBeingEdited = site;
            EditSiteNameTextBox.Text = site.DisplayName;
            EditSiteDomainTextBox.Text = site.Url;
            EditSiteModalOverlay.Visibility = Visibility.Visible;
            EditSiteNameTextBox.Focus();
            EditSiteNameTextBox.SelectAll();
        }
    }

    private void CancelEditSite_Click(object sender, RoutedEventArgs e)
    {
        _siteBeingEdited = null;
        EditSiteModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void SaveEditSite_Click(object sender, RoutedEventArgs e)
    {
        if (_siteBeingEdited == null) return;

        var newName = EditSiteNameTextBox.Text.Trim();
        var rawUrl = EditSiteDomainTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            MessageBox.Show(this, "Ingresa un enlace válido (ej. https://www.youtube.com).", "Editar página",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var updated = SavedSite.Create(newName, rawUrl);
        _siteBeingEdited.Url = updated.Url;
        _siteBeingEdited.Domain = updated.Domain;
        _siteBeingEdited.DisplayName = updated.DisplayName;

        PersistSavedSites($"✓ Editado: {_siteBeingEdited.DisplayName}");

        _siteBeingEdited = null;
        EditSiteModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void PersistSavedSites(string? customStatus = null)
    {
        _storageService.SaveSites(_savedSites);
        UpdateSaveStatusText(customStatus);
    }

    private void UpdateSaveStatusText(string? customStatus = null)
    {
        int customActive = _savedSites.Count(s => s.IsEnabled);
        int nsfwActive = _nsfwCategories.SelectMany(c => c.Sites).Count(s => s.IsEnabled);
        int totalActive = customActive + nsfwActive;

        string summary = nsfwActive > 0
            ? $"{customActive} propias + {nsfwActive} NSFW"
            : $"{totalActive} activas";

        SaveStatusTextBlock.Text = customStatus != null
            ? $"{customStatus} ({summary})"
            : $"✓ Autoguardado ({summary})";
        SaveStatusTextBlock.ToolTip = $"Páginas: {_storageService.StorageFilePath}\nCatálogo NSFW: {_nsfwService.CacheFilePath}";
    }

    // ==================== COLECCIONES NSFW (SIN HENTAI) ====================

    private void ShowCustomSitesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_nsfwCategories.SelectMany(c => c.Sites).Any(s => s.IsEnabled))
            _isNsfwGlowing = false;

        SetSidebarMode(showNsfw: false);
        UpdateNsfwButtonAppearance();
    }

    private void ToggleNsfwPanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isNsfwGlowing)
        {
            _isNsfwGlowing = true;
            SetSidebarMode(showNsfw: true);
            UpdateNsfwButtonAppearance();
            return;
        }

        if (NsfwPanelGrid.Visibility != Visibility.Visible)
        {
            SetSidebarMode(showNsfw: true);
            return;
        }

        foreach (var cat in _nsfwCategories)
            cat.IsCategoryChecked = false;

        _isNsfwGlowing = false;
        SetSidebarMode(showNsfw: false);
        PersistNsfwCategories("✓ NSFW desmarcadas");
    }

    private void SetSidebarMode(bool showNsfw)
    {
        var customVis = showNsfw ? Visibility.Collapsed : Visibility.Visible;
        CustomSiteFormPanel.Visibility = customVis;
        CustomSiteActionsPanel.Visibility = customVis;
        CustomSitesScrollViewer.Visibility = customVis;
        NsfwPanelGrid.Visibility = showNsfw ? Visibility.Visible : Visibility.Collapsed;

        var primaryStyle = (Style)FindResource("PrimaryButtonStyle");
        var secondaryStyle = (Style)FindResource("SecondaryButtonStyle");
        ShowCustomSitesButton.Style = showNsfw ? secondaryStyle : primaryStyle;
    }

    private void NsfwCategoryCheckBox_Click(object sender, RoutedEventArgs e) => SyncNsfwGlowWithSelection();
    private void NsfwSiteCheckBox_Click(object sender, RoutedEventArgs e) => SyncNsfwGlowWithSelection();

    private void SyncNsfwGlowWithSelection()
    {
        int nsfwActive = _nsfwCategories.SelectMany(c => c.Sites).Count(s => s.IsEnabled);
        _isNsfwGlowing = nsfwActive > 0;
        PersistNsfwCategories(nsfwActive > 0 ? "✓ Selección NSFW activa" : "✓ NSFW desmarcadas");
    }

    private void PersistNsfwCategories(string? status = null)
    {
        _nsfwService.SaveCategories(_nsfwCategories);
        UpdateNsfwButtonAppearance();
        UpdateSaveStatusText(status);
    }

    private void UpdateNsfwButtonAppearance()
    {
        int nsfwActive = _nsfwCategories.SelectMany(c => c.Sites).Count(s => s.IsEnabled);
        ToggleNsfwPanelButton.Content = nsfwActive > 0
            ? $"🔞 Páginas NSFW ({nsfwActive})"
            : "🔞 Páginas NSFW";
        ToggleNsfwPanelButton.Style = (Style)FindResource(_isNsfwGlowing ? "NsfwGlowButtonStyle" : "SecondaryButtonStyle");
    }

    private List<SavedSite> GetAllActiveSites()
    {
        var list = new List<SavedSite>();
        var seenDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in _savedSites.Where(s => s.IsEnabled))
        {
            if (seenDomains.Add(s.RootDomain))
                list.Add(s);
        }

        foreach (var s in _nsfwCategories.SelectMany(c => c.Sites).Where(s => s.IsEnabled))
        {
            if (seenDomains.Add(s.RootDomain))
                list.Add(s);
        }

        return list;
    }

    // ==================== HISTORIAL DESPLEGABLE EN LA BÚSQUEDA + REVERTIR Y BORRAR BÚSQUEDA ====================

    private void SearchQueryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearQueryTextButton.Visibility = string.IsNullOrEmpty(SearchQueryTextBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ClearQueryTextButton_Click(object sender, RoutedEventArgs e)
    {
        SearchQueryTextBox.Clear();
        SearchQueryTextBox.Focus();
    }

    private void SearchQueryTextBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_searchHistory.Count > 0 && !SearchHistoryPopup.IsOpen)
        {
            RefreshHistoryPopupState();
            SearchHistoryPopup.IsOpen = true;
        }
    }

    private void ToggleHistoryDropdownButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshHistoryPopupState();
        SearchHistoryPopup.IsOpen = !SearchHistoryPopup.IsOpen;
    }

    private void RefreshHistoryPopupState()
    {
        bool hasItems = _searchHistory.Count > 0;
        EmptyHistoryText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        ClearAllHistoryButton.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        SearchHistoryItemsControl.Items.Refresh();
    }

    private async void HistoryItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchHistoryEntry entry })
        {
            SearchHistoryPopup.IsOpen = false;
            SearchQueryTextBox.Text = entry.Query;
            SearchQueryTextBox.CaretIndex = entry.Query.Length;
            await ExecuteSearchAsync();
        }
    }

    private void DeleteSingleHistoryItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchHistoryEntry entry })
        {
            _searchHistory.Remove(entry);
            _settingsService.SaveSearchHistory(_searchHistory);
            RefreshHistoryPopupState();
            e.Handled = true;
        }
    }

    private void ClearAllHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _searchHistory.Clear();
        _settingsService.SaveSearchHistory(_searchHistory);
        RefreshHistoryPopupState();
        SearchHistoryPopup.IsOpen = false;
    }

    private void RecordSearchInHistory(string query, int resultCount)
    {
        if (_appSettings.IsIncognitoMode)
            return;

        var existing = _searchHistory.FirstOrDefault(x => string.Equals(x.Query, query, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            _searchHistory.Remove(existing);

        _searchHistory.Insert(0, new SearchHistoryEntry
        {
            Query = query,
            SearchedAtUtc = DateTime.UtcNow,
            ResultCount = resultCount
        });

        while (_searchHistory.Count > 30)
            _searchHistory.RemoveAt(_searchHistory.Count - 1);

        _settingsService.SaveSearchHistory(_searchHistory);
    }

    private void RevertSearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previousSearchesStack.Count == 0)
            return;

        _searchCts?.Cancel();
        var prev = _previousSearchesStack.Pop();

        _currentSearchQuery = prev.Query;
        SearchQueryTextBox.Text = prev.Query;
        _allSearchResults = prev.AllResults;
        _lastSearchedSites = prev.SearchedSites;
        _selectedFilterSiteId = prev.SelectedFilterSiteId;
        _currentPage = prev.CurrentPage;

        IncompatibleSearchOverlay.Visibility = Visibility.Collapsed;
        SearchWarningBanner.Visibility = Visibility.Collapsed;

        RebuildFilterChips(_lastSearchedSites);
        ApplyFilterAndRenderPage();
        UpdateSearchSessionButtonsState();
    }

    private void ClearSearchSessionButton_Click(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
        _previousSearchesStack.Clear();
        _allSearchResults = [];
        _filteredResults = [];
        _lastSearchedSites = [];
        _selectedFilterSiteId = null;
        _currentSearchQuery = string.Empty;
        _currentPage = 1;

        SearchQueryTextBox.Clear();
        VideosGridItemsControl.ItemsSource = null;
        FilterBarBorder.Visibility = Visibility.Collapsed;
        PaginationBarBorder.Visibility = Visibility.Collapsed;
        SearchWarningBanner.Visibility = Visibility.Collapsed;
        IncompatibleSearchOverlay.Visibility = Visibility.Collapsed;

        EmptyStateIcon.Text = "🎞️";
        EmptyStateTitle.Text = "Búsqueda borrada";
        EmptyStateSubtitle.Text = "Escribe una nueva búsqueda arriba o despliega el historial (🕒) para volver a consultar.";
        EmptyStatePanel.Visibility = Visibility.Visible;

        UpdateSearchSessionButtonsState();
        SearchQueryTextBox.Focus();
    }

    private void UpdateSearchSessionButtonsState()
    {
        if (_previousSearchesStack.TryPeek(out var prev))
        {
            RevertSearchButton.IsEnabled = true;
            RevertSearchButton.ToolTip = $"Revertir a la búsqueda anterior: \"{prev.Query}\" ({prev.AllResults.Count} videos)";
        }
        else
        {
            RevertSearchButton.IsEnabled = false;
            RevertSearchButton.ToolTip = "No hay una búsqueda previa en esta sesión para revertir";
        }

        ClearSearchSessionButton.IsEnabled =
            _allSearchResults.Count > 0 ||
            _previousSearchesStack.Count > 0 ||
            !string.IsNullOrWhiteSpace(_currentSearchQuery);
    }

    // ==================== BÚSQUEDA MULTISITIO EN INTERNET ====================

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await ExecuteSearchAsync();

    private async void SearchQueryTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SearchHistoryPopup.IsOpen = false;
            e.Handled = true;
            await ExecuteSearchAsync();
        }
        else if (e.Key == Key.Down && _searchHistory.Count > 0 && !SearchHistoryPopup.IsOpen)
        {
            RefreshHistoryPopupState();
            SearchHistoryPopup.IsOpen = true;
            e.Handled = true;
        }
    }

    private void CancelSearchButton_Click(object sender, RoutedEventArgs e) => _searchCts?.Cancel();

    private void DismissSearchWarningBanner_Click(object sender, RoutedEventArgs e)
        => SearchWarningBanner.Visibility = Visibility.Collapsed;

    private void CloseIncompatibleSearchOverlay_Click(object sender, RoutedEventArgs e)
    {
        IncompatibleSearchOverlay.Visibility = Visibility.Collapsed;
        if (_allSearchResults.Count == 0)
            EmptyStatePanel.Visibility = Visibility.Visible;
    }

    private void IncompatibleOpenBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastIncompatibleDirectUrl))
            OpenUrlInExternalBrowser(_lastIncompatibleDirectUrl);
    }

    private async Task ExecuteSearchAsync()
    {
        var query = SearchQueryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchQueryTextBox.Focus();
            return;
        }

        SearchHistoryPopup.IsOpen = false;

        bool isDirectUrl = VideoSearchCoordinator.IsDirectWebUrl(query);
        var activeSites = GetAllActiveSites();
        if (!isDirectUrl && activeSites.Count == 0)
        {
            MessageBox.Show(
                this,
                "Marca al menos un buscador en 'Mis Páginas' o en '🔞 Páginas NSFW' antes de buscar.",
                "Sin buscadores activos",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Guardar instantánea de la búsqueda actual para poder revertirla con un clic
        if (_allSearchResults.Count > 0 && !string.IsNullOrWhiteSpace(_currentSearchQuery))
        {
            _previousSearchesStack.Push(new SearchSessionSnapshot
            {
                Query = _currentSearchQuery,
                AllResults = [.. _allSearchResults],
                SearchedSites = [.. _lastSearchedSites],
                SelectedFilterSiteId = _selectedFilterSiteId,
                CurrentPage = _currentPage
            });
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        SearchButton.IsEnabled = false;
        CancelSearchButton.Visibility = Visibility.Visible;
        SearchWarningBanner.Visibility = Visibility.Collapsed;
        IncompatibleSearchOverlay.Visibility = Visibility.Collapsed;

        string targetSummary = activeSites.Count <= 5
            ? string.Join(", ", activeSites.Select(s => s.DisplayName))
            : $"{activeSites.Count} páginas activas";

        LoadingStatusText.Text = isDirectUrl
            ? "Analizando enlace y extrayendo videos compatibles..."
            : $"Buscando \"{query}\" en {targetSummary}...";
        LoadingOverlay.Visibility = Visibility.Visible;
        EmptyStatePanel.Visibility = Visibility.Collapsed;

        try
        {
            var (results, warnings) = await _searchCoordinator.SearchAllAsync(activeSites, query, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            _currentSearchQuery = query;
            _allSearchResults = results;
            _lastSearchedSites = activeSites.Select(s => s.Clone()).ToList();
            _selectedFilterSiteId = null;
            _currentPage = 1;

            RecordSearchInHistory(query, results.Count);
            RebuildFilterChips(_lastSearchedSites);
            ApplyFilterAndRenderPage();
            UpdateSearchSessionButtonsState();

            if (_allSearchResults.Count == 0)
            {
                if (isDirectUrl || warnings.Count > 0)
                {
                    // Mostrar cartel explícito de no compatible cuando no se puede obtener video del enlace o sitio
                    _lastIncompatibleDirectUrl = isDirectUrl ? query : activeSites.FirstOrDefault()?.BaseUrl;
                    IncompatibleSearchTitle.Text = isDirectUrl
                        ? "No se pudo obtener el video — Enlace no compatible"
                        : "Página o formato de video no compatible";
                    IncompatibleSearchMessage.Text = warnings.Count > 0
                        ? $"No fue posible extraer videos compatibles desde la fuente seleccionada.\nDetalle: {string.Join(" | ", warnings)}"
                        : "Este sitio web no expone videos compatibles, requiere inicio de sesión o utiliza cifrado DRM cerrado.";
                    IncompatibleOpenBrowserButton.Visibility = !string.IsNullOrWhiteSpace(_lastIncompatibleDirectUrl)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    IncompatibleSearchOverlay.Visibility = Visibility.Visible;
                    EmptyStatePanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    EmptyStateIcon.Text = "🔍";
                    EmptyStateTitle.Text = $"No se encontraron videos para \"{query}\"";
                    EmptyStateSubtitle.Text = "Prueba con otros términos de búsqueda o activa más buscadores en el panel lateral.";
                    EmptyStatePanel.Visibility = Visibility.Visible;
                }
            }
            else if (warnings.Count > 0)
            {
                SearchWarningBannerText.Text = $"⚠️ Algunas páginas no fueron compatibles o no respondieron: {string.Join(" • ", warnings)}";
                SearchWarningBanner.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            if (_allSearchResults.Count == 0)
                EmptyStatePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _lastIncompatibleDirectUrl = isDirectUrl ? query : null;
            IncompatibleSearchTitle.Text = "No se pudo obtener video — Fuente no compatible";
            IncompatibleSearchMessage.Text = $"Ocurrió un error al intentar extraer videos compatibles: {ex.Message}";
            IncompatibleOpenBrowserButton.Visibility = isDirectUrl ? Visibility.Visible : Visibility.Collapsed;
            IncompatibleSearchOverlay.Visibility = Visibility.Visible;
        }
        finally
        {
            if (_searchCts == cts)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                CancelSearchButton.Visibility = Visibility.Collapsed;
                SearchButton.IsEnabled = true;
                UpdateSearchSessionButtonsState();
            }
        }
    }

    // ==================== FILTROS, CUADRÍCULA 3x4 Y PAGINACIÓN DINÁMICA ====================

    private void RebuildFilterChips(IReadOnlyList<SavedSite> searchedSites)
    {
        if (_allSearchResults.Count == 0)
        {
            FilterBarBorder.Visibility = Visibility.Collapsed;
            FilterChipsItemsControl.ItemsSource = null;
            return;
        }

        var chips = new List<SiteFilterChip>
        {
            new()
            {
                SiteId = null,
                Label = "Todas",
                Count = _allSearchResults.Count,
                IsSelected = _selectedFilterSiteId == null,
                AccentHex = "#8B7CFF"
            }
        };

        var addedSiteIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var site in searchedSites)
        {
            addedSiteIds.Add(site.Id);
            int count = _allSearchResults.Count(v => v.SourceSiteId == site.Id);
            chips.Add(new SiteFilterChip
            {
                SiteId = site.Id,
                Label = site.DisplayName,
                Count = count,
                IsSelected = _selectedFilterSiteId == site.Id,
                AccentHex = site.AccentHex
            });
        }

        foreach (var extraGroup in _allSearchResults.Where(v => !addedSiteIds.Contains(v.SourceSiteId)).GroupBy(v => v.SourceSiteId))
        {
            var first = extraGroup.First();
            chips.Add(new SiteFilterChip
            {
                SiteId = first.SourceSiteId,
                Label = first.SourceSiteName,
                Count = extraGroup.Count(),
                IsSelected = _selectedFilterSiteId == first.SourceSiteId,
                AccentHex = first.AccentHex
            });
        }

        FilterChipsItemsControl.ItemsSource = chips;
        FilterBarBorder.Visibility = Visibility.Visible;
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            _selectedFilterSiteId = btn.Tag as string;
            _currentPage = 1;

            RebuildFilterChips(_lastSearchedSites);
            ApplyFilterAndRenderPage();
        }
    }

    private void ApplyFilterAndRenderPage()
    {
        _filteredResults = string.IsNullOrWhiteSpace(_selectedFilterSiteId)
            ? _allSearchResults
            : _allSearchResults.Where(v => v.SourceSiteId == _selectedFilterSiteId).ToList();

        if (_filteredResults.Count == 0)
        {
            VideosGridItemsControl.ItemsSource = null;
            PaginationBarBorder.Visibility = Visibility.Collapsed;
            if (_allSearchResults.Count > 0)
            {
                EmptyStateIcon.Text = "📭";
                EmptyStateTitle.Text = "Sin resultados en esta página específica";
                EmptyStateSubtitle.Text = "Selecciona el filtro 'Todas' u otro buscador arriba para ver los demás videos encontrados.";
                EmptyStatePanel.Visibility = Visibility.Visible;
            }
            return;
        }

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        IncompatibleSearchOverlay.Visibility = Visibility.Collapsed;

        int totalPages = Math.Max(1, (int)Math.Ceiling(_filteredResults.Count / (double)VideosPerPage));
        _currentPage = Math.Clamp(_currentPage, 1, totalPages);

        int skip = (_currentPage - 1) * VideosPerPage;
        var pageItems = _filteredResults.Skip(skip).Take(VideosPerPage).ToList();

        VideosGridItemsControl.ItemsSource = pageItems;
        GridScrollViewer.ScrollToTop();

        int fromIdx = skip + 1;
        int toIdx = skip + pageItems.Count;
        PaginationSummaryText.Text =
            $"Mostrando {fromIdx}–{toIdx} de {_filteredResults.Count} videos encontrados  •  Página {_currentPage} de {totalPages}";

        PrevPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _currentPage < totalPages;

        PageSelectorsItemsControl.ItemsSource = BuildPageSelectorItems(_currentPage, totalPages);
        PaginationBarBorder.Visibility = Visibility.Visible;
    }

    private static List<PageSelectorItem> BuildPageSelectorItems(int currentPage, int totalPages)
    {
        var items = new List<PageSelectorItem>();

        if (totalPages <= 9)
        {
            for (int p = 1; p <= totalPages; p++)
            {
                items.Add(new PageSelectorItem
                {
                    PageNumber = p,
                    Label = p.ToString(),
                    IsCurrent = p == currentPage
                });
            }
            return items;
        }

        var pagesToShow = new SortedSet<int> { 1, totalPages };
        for (int p = Math.Max(1, currentPage - 2); p <= Math.Min(totalPages, currentPage + 2); p++)
            pagesToShow.Add(p);

        int prev = 0;
        foreach (var p in pagesToShow)
        {
            if (prev > 0 && p - prev > 1)
            {
                items.Add(new PageSelectorItem
                {
                    PageNumber = -1,
                    Label = "…",
                    IsEllipsis = true
                });
            }

            items.Add(new PageSelectorItem
            {
                PageNumber = p,
                Label = p.ToString(),
                IsCurrent = p == currentPage
            });

            prev = p;
        }

        return items;
    }

    private void PrevPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 1)
        {
            _currentPage--;
            ApplyFilterAndRenderPage();
        }
    }

    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        int totalPages = Math.Max(1, (int)Math.Ceiling(_filteredResults.Count / (double)VideosPerPage));
        if (_currentPage < totalPages)
        {
            _currentPage++;
            ApplyFilterAndRenderPage();
        }
    }

    private void PageSelectorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PageSelectorItem { IsEllipsis: false } item } && item.PageNumber != _currentPage)
        {
            _currentPage = item.PageNumber;
            ApplyFilterAndRenderPage();
        }
    }

    private void GridDensityButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int cols))
        {
            if (cols is 3 or 4 or 6 && SelectedGridColumns != cols)
            {
                SelectedGridColumns = cols;
                _appSettings.GridColumns = cols;
                _settingsService.SaveSettings(_appSettings);
                UpdateGridDensityButtons();
                _currentPage = 1;
                ApplyFilterAndRenderPage();
            }
        }
    }

    private void UpdateGridDensityButtons()
    {
        var activeBg = (Brush)new BrushConverter().ConvertFromString("#6D5DFB")!;
        var activeFg = Brushes.White;
        var activeBorder = (Brush)new BrushConverter().ConvertFromString("#9B8EFF")!;

        var normalBg = (Brush)new BrushConverter().ConvertFromString("#1A2035")!;
        var normalFg = (Brush)new BrushConverter().ConvertFromString("#9AA5C4")!;
        var normalBorder = (Brush)new BrushConverter().ConvertFromString("#2D3754")!;

        void StyleButton(Button? btn, bool isActive)
        {
            if (btn == null) return;
            btn.Background = isActive ? activeBg : normalBg;
            btn.Foreground = isActive ? activeFg : normalFg;
            btn.BorderBrush = isActive ? activeBorder : normalBorder;
            btn.FontWeight = isActive ? FontWeights.Bold : FontWeights.SemiBold;
        }

        StyleButton(GridCols3Btn, SelectedGridColumns == 3);
        StyleButton(GridCols4Btn, SelectedGridColumns == 4);
        StyleButton(GridCols6Btn, SelectedGridColumns == 6);

        StyleButton(BottomGridCols3Btn, SelectedGridColumns == 3);
        StyleButton(BottomGridCols4Btn, SelectedGridColumns == 4);
        StyleButton(BottomGridCols6Btn, SelectedGridColumns == 6);
    }

    // ==================== NAVEGACIÓN ENTRE MENÚ INICIAL, BUSCADOR Y DESCARGADOR ====================

    private void NavToHomeMenu_Click(object sender, RoutedEventArgs e) => ShowHomeMenu();
    private void NavToSearchArea_Click(object sender, RoutedEventArgs e) => ShowSearchArea();
    private void NavToDownloaderArea_Click(object sender, RoutedEventArgs e) => ShowDownloaderArea();
    private void HomeMenu_OpenSearch_Click(object sender, RoutedEventArgs e) => ShowSearchArea();
    private void HomeMenu_OpenDownloader_Click(object sender, RoutedEventArgs e) => ShowDownloaderArea();

    private void ShowHomeMenu()
    {
        HomeMenuGrid.Visibility = Visibility.Visible;
        SearchAreaGrid.Visibility = Visibility.Collapsed;
        DownloaderAreaGrid.Visibility = Visibility.Collapsed;
    }

    private void ShowSearchArea()
    {
        HomeMenuGrid.Visibility = Visibility.Collapsed;
        SearchAreaGrid.Visibility = Visibility.Visible;
        DownloaderAreaGrid.Visibility = Visibility.Collapsed;
        SearchQueryTextBox.Focus();
    }

    private void ShowDownloaderArea()
    {
        HomeMenuGrid.Visibility = Visibility.Collapsed;
        SearchAreaGrid.Visibility = Visibility.Collapsed;
        DownloaderAreaGrid.Visibility = Visibility.Visible;
        DownloaderUrlTextBox.Focus();
    }

    // ==================== APARTADO DEDICADO DE DESCARGAS (VIDEO MP4 & AUDIO MP3) ====================

    private void DownloaderFormatRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (DownloaderQualityPanel == null || DownloaderAudioInfoPanel == null) return;
        bool isMp3 = DownloaderFormatAudioRadio.IsChecked == true;
        DownloaderQualityPanel.Visibility = isMp3 ? Visibility.Collapsed : Visibility.Visible;
        DownloaderAudioInfoPanel.Visibility = isMp3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DownloaderUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = (DownloaderUrlTextBox.Text ?? string.Empty).Trim();
        DownloaderClearUrlBtn.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

        if (string.IsNullOrWhiteSpace(text))
        {
            DownloaderDetectedPlatformBorder.Visibility = Visibility.Collapsed;
            return;
        }

        string detected = "";
        string colorHex = "#A78BFA";
        if (text.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || text.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            detected = "YouTube (Videos & Shorts)";
            colorHex = "#F87171";
        }
        else if (text.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            detected = "Twitch (Clips & VODs)";
            colorHex = "#C084FC";
        }
        else if (text.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
        {
            detected = "TikTok (Videos y clips)";
            colorHex = "#2DD4BF";
        }
        else if (text.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            detected = "Instagram (Reels & Publicaciones)";
            colorHex = "#F472B6";
        }
        else if (text.Contains("dailymotion.com", StringComparison.OrdinalIgnoreCase))
        {
            detected = "Dailymotion (Full HD)";
            colorHex = "#60A5FA";
        }
        else if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            detected = "Enlace Web Directo";
            colorHex = "#34D399";
        }

        if (!string.IsNullOrEmpty(detected))
        {
            DownloaderDetectedPlatformText.Text = detected;
            DownloaderDetectedPlatformText.Foreground = (Brush)new BrushConverter().ConvertFromString(colorHex)!;
            DownloaderDetectedPlatformBorder.Visibility = Visibility.Visible;
        }
        else
        {
            DownloaderDetectedPlatformBorder.Visibility = Visibility.Collapsed;
        }

        bool isYtPlaylist = (text.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || text.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)) &&
                            (text.Contains("list=", StringComparison.OrdinalIgnoreCase) || text.Contains("/playlist", StringComparison.OrdinalIgnoreCase));

        DownloaderPlaylistDetectedBorder.Visibility = isYtPlaylist ? Visibility.Visible : Visibility.Collapsed;
        if (isYtPlaylist)
        {
            DownloaderDownloadFullPlaylistCheckBox.IsChecked = _appSettings.DownloadPlaylistsComplete;
        }
    }

    private void DownloaderClearUrlBtn_Click(object sender, RoutedEventArgs e)
    {
        DownloaderUrlTextBox.Text = string.Empty;
        DownloaderUrlTextBox.Focus();
    }

    private void DownloaderPasteUrlBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var txt = Clipboard.GetText();
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    DownloaderUrlTextBox.Text = txt.Trim();
                    DownloaderUrlTextBox.CaretIndex = DownloaderUrlTextBox.Text.Length;
                }
            }
        }
        catch { }
    }

    private void DownloaderUrlTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            StartDedicatedDownload();
        }
    }

    private void DownloaderBrowseFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Selecciona la carpeta donde guardar tus descargas",
                InitialDirectory = _currentDownloadDirectory
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                _currentDownloadDirectory = dlg.FolderName;
                DownloaderFolderTextBox.Text = _currentDownloadDirectory;
            }
        }
        catch
        {
            // Fallback si no está disponible
        }
    }

    private void DownloaderDismissErrorBtn_Click(object sender, RoutedEventArgs e)
    {
        DownloaderErrorBorder.Visibility = Visibility.Collapsed;
    }

    private void DownloaderStartButton_Click(object sender, RoutedEventArgs e)
    {
        StartDedicatedDownload();
    }

    private async void StartDedicatedDownload()
    {
        var rawUrl = (DownloaderUrlTextBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawUrl) || (!rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            ShowDownloaderError("Por favor, ingresa o pega un enlace válido que comience con http:// o https://");
            return;
        }

        DownloaderErrorBorder.Visibility = Visibility.Collapsed;
        DownloaderSuccessBorder.Visibility = Visibility.Collapsed;

        bool isAudioOnly = DownloaderFormatAudioRadio.IsChecked == true;
        var selectedQuality = DownloaderQualityComboBox.SelectedItem as QualityOption ?? QualityOption.DefaultOptions[0];

        _downloaderCts?.Cancel();
        _downloaderCts = new CancellationTokenSource();
        var ct = _downloaderCts.Token;

        DownloaderStartButton.IsEnabled = false;
        DownloaderProgressBorder.Visibility = Visibility.Visible;
        DownloaderProgressBar.Value = 0;
        DownloaderProgressStatusText.Text = "Iniciando descarga...";

        var progress = new Progress<(double Percent, string Status)>(report =>
        {
            Dispatcher.Invoke(() =>
            {
                DownloaderProgressBar.Value = Math.Clamp(report.Percent, 0, 100);
                DownloaderProgressStatusText.Text = report.Status;
            });
        });

        try
        {
            bool isPlaylist = DownloaderPlaylistDetectedBorder.Visibility == Visibility.Visible &&
                              DownloaderDownloadFullPlaylistCheckBox.IsChecked == true;

            var savedPath = await _qualityAndDownloadService.DownloadDirectUrlAsync(
                rawUrl,
                isAudioOnly,
                selectedQuality,
                _currentDownloadDirectory,
                progress,
                ct,
                isPlaylist);

            _lastDownloadedFilePath = savedPath;
            DownloaderProgressBorder.Visibility = Visibility.Collapsed;
            var displayName = isPlaylist ? $"Playlist: {Path.GetFileName(savedPath.TrimEnd('\\', '/'))}" : Path.GetFileName(savedPath);
            DownloaderSuccessFileNameText.Text = displayName + $" • Guardado en {_currentDownloadDirectory}";
            DownloaderSuccessBorder.Visibility = Visibility.Visible;

            _recentDownloads.Insert(0, new RecentDownloadItem
            {
                FileName = Path.GetFileName(savedPath),
                FilePath = savedPath,
                FormatType = isAudioOnly ? "MP3" : "MP4",
                SourceUrl = rawUrl,
                Timestamp = DateTime.Now.ToString("HH:mm:ss")
            });

            DownloaderRecentSectionBorder.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            DownloaderProgressBorder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            DownloaderProgressBorder.Visibility = Visibility.Collapsed;
            ShowDownloaderError(VideoQualityAndDownloadService.HumanizeDownloadErrorMessage(ex.Message, selectedQuality));
        }
        finally
        {
            DownloaderStartButton.IsEnabled = true;
        }
    }

    private void DownloaderCancelButton_Click(object sender, RoutedEventArgs e)
    {
        try { _downloaderCts?.Cancel(); } catch { }
        DownloaderProgressBorder.Visibility = Visibility.Collapsed;
        DownloaderStartButton.IsEnabled = true;
    }

    private void ShowDownloaderError(string message)
    {
        DownloaderErrorText.Text = message;
        DownloaderErrorBorder.Visibility = Visibility.Visible;
    }

    private void DownloaderOpenCompletedFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastDownloadedFilePath) && File.Exists(_lastDownloadedFilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _lastDownloadedFilePath,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private void DownloaderOpenCompletedFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderOrSelectFile(_lastDownloadedFilePath ?? _currentDownloadDirectory);
    }

    private void RecentDownloadOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch { }
        }
    }

    private void RecentDownloadOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            OpenFolderOrSelectFile(path);
        }
    }

    private static void OpenFolderOrSelectFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    // ==================== REPRODUCTOR INTEGRADO, REGULADOR DE CALIDAD, DESCARGA Y CARTEL NO COMPATIBLE ====================

    private void InitializeQualitySelector()
    {
        _suppressQualitySelectionHandler = true;
        PlayerQualityComboBox.ItemsSource = QualityOption.DefaultOptions;
        var savedOpt = QualityOption.DefaultOptions.FirstOrDefault(o => o.TargetHeight == _appSettings.PreferredQualityHeight)
                       ?? QualityOption.DefaultOptions[0];
        PlayerQualityComboBox.SelectedItem = savedOpt;
        _suppressQualitySelectionHandler = false;
    }

    private QualityOption GetSelectedQualityOption()
        => PlayerQualityComboBox.SelectedItem as QualityOption ?? QualityOption.DefaultOptions[0];

    private void VideoCardControl_VideoSelected(object? sender, VideoResult video)
    {
        _activePlayerVideo = video;
        if (!_appSettings.IsIncognitoMode)
        {
            _playbackHistoryService.AddEntry(video);
        }
        _activeVideoStreams = [];
        while (_interceptedPlayerMediaUrls.TryDequeue(out _)) { }

        PlayerSiteText.Text = video.SourceSiteName;
        PlayerTitleText.Text = video.Title;
        PlayerTitleText.ToolTip = video.Title;
        CopyVideoLinkButton.Content = "📋 Copiar";
        PlayerIncompatibleOverlay.Visibility = Visibility.Collapsed;

        if (_playerWebViewReady && PlayerWebView.CoreWebView2 != null)
        {
            var targetUrl = !string.IsNullOrWhiteSpace(video.PlayerUrl) ? video.PlayerUrl : video.VideoPageUrl;

            // Si es Dailymotion y el usuario eligió una calidad fija, pasarla ya en el parámetro del player oficial
            var qOpt = GetSelectedQualityOption();
            if (qOpt.TargetHeight > 0 && targetUrl.Contains("geo.dailymotion.com/player.html", StringComparison.OrdinalIgnoreCase))
            {
                int dmQ = qOpt.TargetHeight == 360 ? 380 : qOpt.TargetHeight;
                targetUrl += $"&quality={dmQ}";
            }

            PlayerLoadingMask.Visibility = Visibility.Visible;
            PlayerWebView.Margin = new Thickness(-5000, -5000, 0, 0);
            PlayerModalOverlay.Visibility = Visibility.Visible;

            if (targetUrl.Contains("geo.dailymotion.com", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var req = _webViewEnv?.CreateWebResourceRequest(targetUrl, "GET", Stream.Null, "Referer: https://www.dailymotion.com/\r\n")
                              ?? PlayerWebView.CoreWebView2.Environment.CreateWebResourceRequest(targetUrl, "GET", Stream.Null, "Referer: https://www.dailymotion.com/\r\n");
                    PlayerWebView.CoreWebView2.NavigateWithWebResourceRequest(req);
                }
                catch
                {
                    PlayerWebView.CoreWebView2.Navigate(targetUrl);
                }
            }
            else
            {
                PlayerWebView.CoreWebView2.Navigate(targetUrl);
            }
        }
        else
        {
            // Mostrar cartel de no compatible en el reproductor integrado si WebView2 no pudo iniciar
            PlayerLoadingMask.Visibility = Visibility.Collapsed;
            PlayerIncompatibleDetailText.Text =
                "El motor Microsoft Edge WebView2 no está disponible en este sistema para reproducir el video integrado. Puedes abrirlo directamente en tu navegador.";
            PlayerIncompatibleOverlay.Visibility = Visibility.Visible;
            PlayerModalOverlay.Visibility = Visibility.Visible;
        }
    }

    private async void PlayerWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (PlayerWebView.CoreWebView2 == null || _activePlayerVideo == null) return;
        var currentSource = PlayerWebView.Source?.ToString() ?? string.Empty;
        if (currentSource == "about:blank") return;

        var currentVideo = _activePlayerVideo;

        // Si hubo fallo duro de red / SSL / conexión (excluyendo redirecciones abortadas normales)
        if (!e.IsSuccess &&
            e.WebErrorStatus is not (CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted))
        {
            PlayerLoadingMask.Visibility = Visibility.Collapsed;
            PlayerIncompatibleDetailText.Text =
                $"No se pudo conectar con el servidor de video ({e.WebErrorStatus}). Es posible que el enlace no sea compatible o esté bloqueado en tu región.";
            PlayerIncompatibleOverlay.Visibility = Visibility.Visible;
            return;
        }

        string isolationStatus = string.Empty;
        try
        {
            var wv = PlayerWebView.CoreWebView2;
            await wv.ExecuteScriptAsync(DismissAgeGateScript);
            if (_adBlocker.IsEnabled)
                await wv.ExecuteScriptAsync(AdBlockerService.CleanDomAdOverlaysScript);

            // Esperar hasta ~2.5s a que la página monte su etiqueta <video> o reproductor embebido
            for (int i = 0; i < 10; i++)
            {
                if (_activePlayerVideo != currentVideo) return;
                var found = await wv.ExecuteScriptAsync(
                    "Boolean(location.pathname.includes('/embed/video/') || location.pathname.includes('/player/v1/') || location.hostname.includes('player.twitch.tv') || location.hostname.includes('clips.twitch.tv') || location.hostname.includes('redditmedia.com') || document.querySelector('video, #movie_player, .html5-video-player, .jwplayer, .video-js, .fluid_video_wrapper, .plyr, shreddit-player, shreddit-async-loader, .video-player, [data-a-target=video-player], iframe[src*=\"embed\"], iframe[src*=\"player\"], iframe[src*=\"tiktok.com/player\"], iframe[src*=\"bilibili.com/player\"]'))");
                if (found == "true") break;
                await Task.Delay(250);
            }

            if (_activePlayerVideo != currentVideo) return;

            // Extraer todos los flujos y calidades disponibles ANTES de limpiar el DOM
            var rawStreamsJson = await wv.ExecuteScriptAsync(VideoQualityAndDownloadService.ExtractPageVideoStreamsScript);
            var domStreamsJson = TryUnwrapScriptString(rawStreamsJson);
            var pageCookies = await GetCookiesHeaderForUrlAsync(wv, currentSource);

            _activeVideoStreams = await _qualityAndDownloadService.DiscoverStreamsAsync(
                currentVideo,
                domStreamsJson,
                _interceptedPlayerMediaUrls.ToArray(),
                pageCookies,
                currentSource);

            if (_activePlayerVideo != currentVideo) return;

            // Aislar el reproductor limpio o detectar si la página indica error / no compatible
            var rawIso = await wv.ExecuteScriptAsync(IsolateCleanPlayerScript);
            isolationStatus = TryUnwrapScriptString(rawIso) ?? string.Empty;

            // Aplicar la calidad seleccionada en el regulador
            await ApplySelectedQualityToActivePlayerAsync();
        }
        catch
        {
            // Continuar para mostrar reproductor o cartel según estado
        }
        finally
        {
            if (_activePlayerVideo == currentVideo)
            {
                PlayerLoadingMask.Visibility = Visibility.Collapsed;

                if (isolationStatus == "INCOMPATIBLE")
                {
                    PlayerIncompatibleDetailText.Text =
                        "No se pudo obtener ningún reproductor de video compatible en esta página (el video fue retirado, requiere cuenta externa o su formato no es compatible).";
                    PlayerIncompatibleOverlay.Visibility = Visibility.Visible;
                }
                else
                {
                    PlayerIncompatibleOverlay.Visibility = Visibility.Collapsed;
                    PlayerWebView.Margin = new Thickness(0);
                }
            }
        }
    }

    private async void PlayerQualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressQualitySelectionHandler)
            return;

        var selected = GetSelectedQualityOption();
        _appSettings.PreferredQualityHeight = selected.TargetHeight;
        _settingsService.SaveSettings(_appSettings);

        if (_activePlayerVideo != null && PlayerModalOverlay.Visibility == Visibility.Visible)
        {
            await ApplySelectedQualityToActivePlayerAsync();
        }
    }

    private async Task ApplySelectedQualityToActivePlayerAsync()
    {
        if (!_playerWebViewReady || PlayerWebView.CoreWebView2 == null || _activePlayerVideo == null)
            return;

        try
        {
            var qOpt = GetSelectedQualityOption();
            var currentSrc = PlayerWebView.Source?.ToString() ?? string.Empty;

            // En el player embebido de Dailymotion, recargar con el parámetro oficial quality=XXX si cambió
            if (currentSrc.Contains("geo.dailymotion.com/player.html", StringComparison.OrdinalIgnoreCase) && qOpt.TargetHeight > 0)
            {
                int dmQ = qOpt.TargetHeight == 360 ? 380 : qOpt.TargetHeight;
                var baseDmUrl = System.Text.RegularExpressions.Regex.Replace(currentSrc, @"&quality=\d+", "");
                var nextDmUrl = $"{baseDmUrl}&quality={dmQ}";
                if (!string.Equals(currentSrc, nextDmUrl, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var req = _webViewEnv?.CreateWebResourceRequest(nextDmUrl, "GET", Stream.Null, "Referer: https://www.dailymotion.com/\r\n")
                                  ?? PlayerWebView.CoreWebView2.Environment.CreateWebResourceRequest(nextDmUrl, "GET", Stream.Null, "Referer: https://www.dailymotion.com/\r\n");
                        PlayerWebView.CoreWebView2.NavigateWithWebResourceRequest(req);
                    }
                    catch
                    {
                        PlayerWebView.CoreWebView2.Navigate(nextDmUrl);
                    }
                    return;
                }
            }

            var matched = VideoQualityAndDownloadService.SelectBestStreamForQuality(_activeVideoStreams, qOpt.TargetHeight);
            var js = VideoQualityAndDownloadService.BuildApplyPlaybackQualityScript(qOpt, matched);
            await PlayerWebView.CoreWebView2.ExecuteScriptAsync(js);
        }
        catch
        {
            // Ignorar si el iframe externo es cross-origin cerrado
        }
    }

    private async void DownloadVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlayerVideo == null)
            return;

        var video = _activePlayerVideo;
        var selectedQuality = GetSelectedQualityOption();
        var qualityTag = selectedQuality.TargetHeight > 0 ? $"{selectedQuality.TargetHeight}p" : "Mejor";
        var safeTitle = VideoQualityAndDownloadService.SanitizeFileName(video.Title);

        var defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(defaultDir) || !Directory.Exists(defaultDir))
            defaultDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var sfd = new SaveFileDialog
        {
            Title = $"Guardar video en calidad {selectedQuality.Label}",
            InitialDirectory = defaultDir,
            FileName = $"{safeTitle}_{qualityTag}.mp4",
            DefaultExt = ".mp4",
            Filter = "Archivo de Video MP4 (*.mp4)|*.mp4|Todos los archivos (*.*)|*.*"
        };

        if (sfd.ShowDialog(this) != true)
            return;

        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;

        DownloadVideoButton.IsEnabled = false;
        CancelDownloadButton.Visibility = Visibility.Visible;
        PlayerDownloadProgressBar.Value = 0;
        PlayerDownloadStatusText.Text = $"Preparando descarga ({selectedQuality.Label})...";
        PlayerDownloadBarBorder.Visibility = Visibility.Visible;

        string? cookieFile = null;
        try
        {
            string? pageCookies = null;
            if (_playerWebViewReady && PlayerWebView.CoreWebView2 != null)
            {
                var currentSrc = PlayerWebView.Source?.ToString() ?? video.VideoPageUrl;
                pageCookies = await GetCookiesHeaderForUrlAsync(PlayerWebView.CoreWebView2, currentSrc);
                cookieFile = await ExportNetscapeCookieFileAsync(PlayerWebView.CoreWebView2, currentSrc);

                // Refrescar detección de streams por si el reproductor cargó HLS tras iniciar reproducción
                if (_activeVideoStreams.Count == 0)
                {
                    var rawStreams = await PlayerWebView.CoreWebView2.ExecuteScriptAsync(
                        VideoQualityAndDownloadService.ExtractPageVideoStreamsScript);
                    _activeVideoStreams = await _qualityAndDownloadService.DiscoverStreamsAsync(
                        video,
                        TryUnwrapScriptString(rawStreams),
                        _interceptedPlayerMediaUrls.ToArray(),
                        pageCookies,
                        currentSrc,
                        cts.Token);
                }
            }

            var progress = new Progress<(double Percent, string Status)>(report =>
            {
                PlayerDownloadProgressBar.Value = Math.Clamp(report.Percent, 0, 100);
                PlayerDownloadStatusText.Text = report.Status;
            });

            await _qualityAndDownloadService.DownloadVideoAsync(
                video,
                selectedQuality,
                _activeVideoStreams,
                sfd.FileName,
                pageCookies,
                progress,
                cts.Token,
                cookieFilePath: cookieFile);

            PlayerDownloadProgressBar.Value = 100;
            PlayerDownloadStatusText.Text = $"✓ Guardado en {Path.GetFileName(sfd.FileName)}";
            CancelDownloadButton.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            PlayerDownloadStatusText.Text = "Descarga cancelada.";
            CancelDownloadButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            PlayerDownloadBarBorder.Visibility = Visibility.Collapsed;
            MessageBox.Show(
                this,
                ex.Message,
                "Aviso de descarga",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        finally
        {
            if (cookieFile != null && File.Exists(cookieFile))
            {
                try { File.Delete(cookieFile); } catch { }
            }
            if (_downloadCts == cts)
                DownloadVideoButton.IsEnabled = true;
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
        => _downloadCts?.Cancel();

    private static async Task<string?> GetCookiesHeaderForUrlAsync(CoreWebView2 wv, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return null;

        try
        {
            var cookies = await wv.CookieManager.GetCookiesAsync(url);
            if (cookies == null || cookies.Count == 0)
                return null;

            return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ExportNetscapeCookieFileAsync(CoreWebView2? wv, string url)
    {
        if (wv == null || string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        try
        {
            var cookies = await wv.CookieManager.GetCookiesAsync(url);
            if (cookies == null || cookies.Count == 0)
                return null;

            var tempPath = Path.Combine(Path.GetTempPath(), $"buscapp_wv_c_{Guid.NewGuid():N}.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            sb.AppendLine("# Generated by BUSCAPP");

            foreach (var c in cookies)
            {
                var dom = c.Domain ?? "";
                if (string.IsNullOrWhiteSpace(dom))
                    dom = "." + uri.Host;
                var sub = dom.StartsWith(".") ? "TRUE" : "FALSE";
                var p = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
                var sec = c.IsSecure ? "TRUE" : "FALSE";
                long exp = 2147483647;
                try
                {
                    if (c.Expires != DateTime.MinValue)
                        exp = new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds();
                }
                catch { }
                if (exp <= 0) exp = 2147483647;

                sb.AppendLine($"{dom}\t{sub}\t{p}\t{sec}\t{exp}\t{c.Name}\t{c.Value}");
            }

            await File.WriteAllTextAsync(tempPath, sb.ToString(), System.Text.Encoding.ASCII);
            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryUnwrapScriptString(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || rawJson == "null")
            return null;

        try
        {
            return JsonSerializer.Deserialize<string>(rawJson);
        }
        catch
        {
            return rawJson.Trim('"');
        }
    }

    private const string IsolateCleanPlayerScript = @"
(() => {
    try {
        const host = (location.hostname || '').toLowerCase();

        // 0) Si ya estamos en un player dedicado limpio (Dailymotion, Vimeo, TikTok player/v1, Bilibili player, Twitch embed, Instagram embed), respetar su UI nativa
        if (host.includes('geo.dailymotion.com') ||
            (host.includes('dailymotion.com') && (location.pathname.includes('/embed/video/') || location.pathname.includes('/player/'))) ||
            host.includes('player.vimeo.com') ||
            host.includes('player.bilibili.com') ||
            host.includes('player.twitch.tv') ||
            host.includes('clips.twitch.tv') ||
            (host.includes('instagram.com') && location.pathname.includes('/embed')) ||
            location.pathname.includes('/player/v1/')) {
            document.documentElement.style.background = '#000';
            document.body.style.background = '#000';
            document.body.style.margin = '0';
            const vid = document.querySelector('video');
            if (vid) {
                vid.style.width = '100vw';
                vid.style.height = '100vh';
                vid.style.objectFit = 'contain';
                vid.play().catch(() => {});
            }
            return 'DEDICATED_EMBED';
        }

        // 1) Aislamiento CSS resistente a re-renderizado SPA para YouTube (mantiene regulador de calidad #movie_player intacto)
        if (host.includes('youtube.com')) {
            const unavailable = document.querySelector('yt-player-error-message-renderer');
            if (unavailable && (unavailable.innerText || '').trim().length > 5 && !document.querySelector('video[src]')) {
                return 'INCOMPATIBLE';
            }
            let st = document.getElementById('buscapp-yt-clean');
            if (!st) {
                st = document.createElement('style');
                st.id = 'buscapp-yt-clean';
                st.textContent = `
                    #masthead-container, ytd-masthead, #secondary, #comments, #below,
                    ytd-watch-metadata, ytd-merch-shelf-renderer, tp-yt-paper-dialog,
                    ytd-consent-bump-v2-lightbox { display: none !important; }
                    html, body, ytd-app, #content, #page-manager, ytd-watch-flexy {
                        margin: 0 !important; padding: 0 !important; background: #000 !important; overflow: hidden !important;
                    }
                    #movie_player, #player-container-outer, #player-container-inner, #player-container, #ytd-player {
                        position: fixed !important; inset: 0 !important;
                        width: 100vw !important; height: 100vh !important;
                        max-width: 100vw !important; max-height: 100vh !important;
                        z-index: 2147483646 !important; background: #000 !important;
                    }
                    #movie_player video {
                        width: 100vw !important; height: 100vh !important; object-fit: contain !important;
                    }
                `;
                document.head.appendChild(st);
            }
            window.dispatchEvent(new Event('resize'));
            const v = document.querySelector('video');
            if (v) v.play().catch(() => {});
            return document.querySelector('#movie_player, video') ? 'YOUTUBE_ISOLATED' : 'INCOMPATIBLE';
        }

        // 1.4) Aislamiento nativo para Twitch (si se abre en página completa de twitch.tv)
        if (host.includes('twitch.tv')) {
            let st = document.getElementById('buscapp-twitch-clean');
            if (!st) {
                st = document.createElement('style');
                st.id = 'buscapp-twitch-clean';
                st.textContent = `
                    nav, .side-nav, .right-column, .chat-shell, .channel-root__right-column,
                    [data-a-target=top-nav-container], [data-a-target=chat-container],
                    [data-a-target=side-nav-bar], .whispers, .tw-cookie-banner,
                    .consent-banner, [data-a-target=consent-banner] {
                        display: none !important;
                    }
                    html, body, #root, .tw-root {
                        margin: 0 !important; padding: 0 !important;
                        background: #000 !important; overflow: hidden !important;
                        width: 100vw !important; height: 100vh !important;
                    }
                    .video-player, .video-player__container, .persistent-player, [data-a-target=video-player], .highwind-video-player {
                        position: fixed !important; inset: 0 !important;
                        width: 100vw !important; height: 100vh !important;
                        max-width: 100vw !important; max-height: 100vh !important;
                        z-index: 2147483646 !important;
                    }
                    .player-overlay-background, .video-player__overlay--fade {
                        background: transparent !important;
                        display: none !important;
                    }
                    .video-player video, [data-a-target=video-player] video, video {
                        width: 100vw !important; height: 100vh !important; object-fit: contain !important;
                    }
                `;
                document.head.appendChild(st);
            }
            window.dispatchEvent(new Event('resize'));
            const v = document.querySelector('video');
            if (v) {
                v.muted = false;
                v.play().catch(() => {});
            }
            return 'TWITCH_ISOLATED';
        }

        // 1.45) Aislamiento nativo para Instagram (publicaciones / reels / embeds)
        if (host.includes('instagram.com')) {
            document.documentElement.style.background = '#000';
            document.body.style.background = '#000';
            document.body.style.margin = '0';
            const playBtn = document.querySelector('[aria-label*=""Play""], [aria-label*=""Reproducir""], .playButton, [role=button][tabindex=""0""]');
            if (playBtn) { try { playBtn.click(); } catch(e) {} }
            const v = document.querySelector('video');
            if (v) {
                v.muted = false;
                v.play().catch(() => {});
                return 'INSTAGRAM_ISOLATED';
            }
            return 'DEDICATED_EMBED';
        }

        // 1.5) Aislamiento nativo para Reddit (web component shreddit-player)
        if (host.includes('reddit.com')) {
            document.querySelectorAll('reddit-header-large, #bottom-sheet, reddit-bottom-sheet, reddit-cookie-banner, #overlay-container, reddit-overlay-container, shreddit-comment-tree, aside, footer, xpromo-nsfw-blocking-container, [data-testid=nsfw-blocking-container], [slot=interstitial]').forEach(e => {
                try { e.remove(); } catch(err) {}
            });
            document.querySelectorAll('button[slot*=warning], button[aria-label*=18], button[id*=age], button[id*=agree]').forEach(b => { try { b.click(); } catch(err) {} });
            const sp = document.querySelector('shreddit-player');
            if (sp) {
                let st = document.getElementById('buscapp-reddit-clean');
                if (!st) {
                    st = document.createElement('style');
                    st.id = 'buscapp-reddit-clean';
                    st.textContent = `
                        html, body { margin:0 !important; padding:0 !important; background:#000 !important; overflow:hidden !important; width:100vw !important; height:100vh !important; }
                        reddit-header-large, #bottom-sheet, reddit-bottom-sheet, reddit-cookie-banner, #overlay-container, reddit-overlay-container, shreddit-comment-tree, aside, footer, xpromo-nsfw-blocking-container, [data-testid=nsfw-blocking-container] { display:none !important; }
                        shreddit-async-loader { display:block !important; width:100vw !important; height:100vh !important; }
                        shreddit-player, [data-testid=shreddit-player] { position:fixed !important; inset:0 !important; width:100vw !important; height:100vh !important; max-width:100vw !important; max-height:100vh !important; z-index:2147483646 !important; filter:none !important; }
                        shreddit-player video, video { width:100vw !important; height:100vh !important; object-fit:contain !important; }
                    `;
                    document.head.appendChild(st);
                }
                sp.style.setProperty('filter', 'none', 'important');
                let anc = sp.parentElement;
                while (anc && anc !== document.body && anc !== document.documentElement) {
                    anc.style.setProperty('overflow', 'visible', 'important');
                    anc.style.setProperty('transform', 'none', 'important');
                    anc.style.setProperty('filter', 'none', 'important');
                    anc.style.setProperty('contain', 'none', 'important');
                    anc = anc.parentElement;
                }
                const vid = sp.querySelector('video') || (sp.shadowRoot ? sp.shadowRoot.querySelector('video') : null) || document.querySelector('video');
                if (vid) {
                    vid.style.setProperty('width', '100vw', 'important');
                    vid.style.setProperty('height', '100vh', 'important');
                    vid.style.setProperty('object-fit', 'contain', 'important');
                    vid.muted = false;
                    vid.play().catch(() => {});
                }
                window.dispatchEvent(new Event('resize'));
                if (sp.shadowRoot) {
                    sp.shadowRoot.querySelectorAll('button').forEach(b => {
                        const txt = (b.innerText || b.getAttribute('aria-label') || '').toLowerCase();
                        if (txt.includes('play') || txt.includes('view') || txt.includes('nsfw') || txt.includes('yes')) {
                            try { b.click(); } catch(e) {}
                        }
                    });
                }
                return 'REDDIT_ISOLATED';
            }
        }

        // 2) Si existe un <video> con archivo directo MP4/WebM (no blob:), montar reproductor HTML5 limpio conservando origen y cookies
        const vids = Array.from(document.querySelectorAll('video'));
        let directSrc = '';
        for (const v of vids) {
            const candidates = [
                v.currentSrc,
                v.src,
                v.getAttribute('src'),
                ...Array.from(v.querySelectorAll('source')).map(s => s.src || s.getAttribute('src'))
            ].filter(Boolean);

            for (const c of candidates) {
                if ((c.startsWith('http://') || c.startsWith('https://')) && !c.startsWith('blob:')) {
                    directSrc = c;
                    break;
                }
            }
            if (directSrc) break;
        }

        if (!directSrc && vids.length === 0) {
            const html = document.documentElement.innerHTML;
            const mp4Match = html.match(/https?:\\?\/\\?\/[^""'\s<>]+?\.mp4(?:\?[^""'\s<>]*)?/i);
            if (mp4Match) {
                directSrc = mp4Match[0].replace(/\\\//g, '/').replace(/&amp;/g, '&');
            }
        }

        // Si la propia URL cargada en el navegador es un archivo directo MP4/WebM
        if (!directSrc) {
            const loc = location.href || '';
            if (loc.includes('.mp4') || loc.includes('.webm') || loc.includes('DASH_') || loc.includes('fallback')) {
                directSrc = loc;
            }
        }

        if (directSrc) {
            const highestId = window.setTimeout(() => {}, 0);
            for (let i = 0; i <= highestId; i++) window.clearTimeout(i);

            document.head.innerHTML = '<meta charset=""utf-8""><style>html,body{margin:0;padding:0;width:100%;height:100%;background:#000;overflow:hidden;display:flex;align-items:center;justify-content:center;}video{width:100vw;height:100vh;max-width:100%;max-height:100%;background:#000;outline:none;}</style>';
            document.body.innerHTML = '';
            const cleanVideo = document.createElement('video');
            cleanVideo.src = directSrc;
            cleanVideo.controls = true;
            cleanVideo.autoplay = true;
            cleanVideo.playsInline = true;
            document.body.appendChild(cleanVideo);
            cleanVideo.play().catch(() => {});
            return 'DIRECT_PLAYER';
        }

        // 3) Para reproductores MSE/HLS (blob:, JWPlayer, Video.js, FluidPlayer, Plyr, shreddit-player o iframe player):
        const target =
            document.querySelector('#movie_player, .html5-video-player, .jwplayer, .video-js, .fluid_video_wrapper, .plyr, shreddit-player') ||
            document.querySelector('video') ||
            document.querySelector('iframe[src*=""embed""], iframe[src*=""player""], iframe[src*=""video""]');

        if (target) {
            const keepChain = new Set();
            let curr = target;
            while (curr && curr !== document.documentElement) {
                keepChain.add(curr);
                curr = curr.parentElement;
            }

            document.querySelectorAll('body *').forEach(el => {
                if (!keepChain.has(el) && !target.contains(el)) {
                    // Solo ocultar barras de navegación, encabezados, comentarios y barras laterales conocidas, nunca contenedores padre/hermanos genéricos
                    const tag = el.tagName.toLowerCase();
                    const cls = (el.className || '').toString().toLowerCase();
                    const id = (el.id || '').toString().toLowerCase();
                    if (tag === 'header' || tag === 'nav' || tag === 'footer' || tag === 'aside' ||
                        cls.includes('comment') || cls.includes('sidebar') || cls.includes('chat') ||
                        cls.includes('header') || cls.includes('nav') || cls.includes('footer') ||
                        id.includes('comment') || id.includes('sidebar') || id.includes('chat') ||
                        id.includes('header') || id.includes('nav') || id.includes('footer')) {
                        try { el.style.setProperty('display', 'none', 'important'); } catch(err) {}
                    }
                }
            });

            keepChain.forEach(anc => {
                if (anc !== target && anc.style) {
                    anc.style.setProperty('margin', '0', 'important');
                    anc.style.setProperty('padding', '0', 'important');
                    anc.style.setProperty('border', 'none', 'important');
                    anc.style.setProperty('width', '100vw', 'important');
                    anc.style.setProperty('height', '100vh', 'important');
                    anc.style.setProperty('max-width', 'none', 'important');
                    anc.style.setProperty('max-height', 'none', 'important');
                    anc.style.setProperty('background', '#000', 'important');
                    anc.style.setProperty('overflow', 'hidden', 'important');
                }
            });

            target.style.setProperty('position', 'fixed', 'important');
            target.style.setProperty('inset', '0', 'important');
            target.style.setProperty('width', '100vw', 'important');
            target.style.setProperty('height', '100vh', 'important');
            target.style.setProperty('max-width', '100vw', 'important');
            target.style.setProperty('max-height', '100vh', 'important');
            target.style.setProperty('z-index', '2147483647', 'important');
            target.style.setProperty('background', '#000', 'important');

            document.documentElement.style.setProperty('overflow', 'hidden', 'important');
            document.body.style.setProperty('background', '#000', 'important');
            document.body.style.setProperty('overflow', 'hidden', 'important');

            const innerVid = target.tagName === 'VIDEO' ? target : target.querySelector('video');
            if (innerVid) {
                if (target.tagName === 'VIDEO') innerVid.controls = true;
                innerVid.style.setProperty('width', '100%', 'important');
                innerVid.style.setProperty('height', '100%', 'important');
                innerVid.play().catch(() => {});
            }
            window.dispatchEvent(new Event('resize'));
            return 'ISOLATED_PLAYER';
        }
    } catch(e) {}
    return 'INCOMPATIBLE';
})()";

    private void ClosePlayerButton_Click(object sender, RoutedEventArgs e) => CloseIntegratedPlayer();

    private void CloseIntegratedPlayer()
    {
        _downloadCts?.Cancel();
        PlayerDownloadBarBorder.Visibility = Visibility.Collapsed;
        PlayerIncompatibleOverlay.Visibility = Visibility.Collapsed;
        PlayerModalOverlay.Visibility = Visibility.Collapsed;
        _activePlayerVideo = null;
        _activeVideoStreams = [];

        if (_playerWebViewReady && PlayerWebView.CoreWebView2 != null)
        {
            PlayerWebView.CoreWebView2.Navigate("about:blank");
        }
    }

    private void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlayerVideo != null)
            OpenUrlInExternalBrowser(_activePlayerVideo.VideoPageUrl);
    }

    private void CopyVideoLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activePlayerVideo != null && !string.IsNullOrWhiteSpace(_activePlayerVideo.VideoPageUrl))
        {
            Clipboard.SetText(_activePlayerVideo.VideoPageUrl);
            CopyVideoLinkButton.Content = "✓ Copiado";
        }
    }

    private static void OpenUrlInExternalBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignorar si no se pudo lanzar el navegador
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.B && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SetSidebarCollapsed(!_isSidebarCollapsed, animate: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (AdBlockWarningModalOverlay.Visibility == Visibility.Visible)
            {
                AdBlockWarningModalOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            else if (SearchHistoryPopup.IsOpen)
            {
                SearchHistoryPopup.IsOpen = false;
                e.Handled = true;
            }
            else if (PlayerModalOverlay.Visibility == Visibility.Visible)
            {
                CloseIntegratedPlayer();
                e.Handled = true;
            }
            else if (EditSiteModalOverlay.Visibility == Visibility.Visible)
            {
                EditSiteModalOverlay.Visibility = Visibility.Collapsed;
                _siteBeingEdited = null;
                e.Handled = true;
            }
            else if (IncompatibleSearchOverlay.Visibility == Visibility.Visible)
            {
                CloseIncompatibleSearchOverlay_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }

    // ==================== MOTOR CHROMIUM UNIVERSAL (INTERACCIÓN DE BUSCADOR + GEOMETRÍA DOM) ====================

    public async Task<List<VideoResult>> InteractiveSiteSearchAsync(SavedSite site, string query, CancellationToken ct = default)
    {
        if (!await _scraperLock.WaitAsync(TimeSpan.FromSeconds(16), ct))
            return [];
        try
        {
            return await Dispatcher.InvokeAsync(async () =>
            {
                var results = new List<VideoResult>();
                if (!_scraperWebViewReady || ScraperWebView.CoreWebView2 == null)
                    return results;

                var wv = ScraperWebView.CoreWebView2;
                try
                {
                    string startUrl = site.Url;
                    bool usedTemplate = false;
                    if (!string.IsNullOrWhiteSpace(query) && startUrl.Contains("{q}", StringComparison.OrdinalIgnoreCase))
                    {
                        startUrl = startUrl.Replace("{q}", Uri.EscapeDataString(query), StringComparison.OrdinalIgnoreCase);
                        usedTemplate = true;
                    }

                    await NavigateWebViewAndWaitAsync(wv, startUrl, 7500, ct);

                    await wv.ExecuteScriptAsync(DismissAgeGateScript);
                    await Task.Delay(250, ct);

                    if (!string.IsNullOrWhiteSpace(query) && !usedTemplate)
                    {
                        var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        void NavHandler(object? s, CoreWebView2NavigationCompletedEventArgs a) => navTcs.TrySetResult(a.IsSuccess);

                        wv.NavigationCompleted += NavHandler;
                        try
                        {
                            var submitScript = BuildUniversalSearchSubmitScript(query);
                            var rawStatus = await wv.ExecuteScriptAsync(submitScript);
                            var status = TryUnwrapScriptString(rawStatus) ?? string.Empty;

                            if (status == "SUBMITTED")
                            {
                                await Task.WhenAny(navTcs.Task, Task.Delay(5500, ct));
                                await Task.Delay(500, ct);
                            }
                            else
                            {
                                var enc = Uri.EscapeDataString(query);
                                await NavigateWebViewAndWaitAsync(wv, $"{site.BaseUrl.TrimEnd('/')}/search/videos/{enc}/", 5500, ct);
                            }
                        }
                        finally
                        {
                            wv.NavigationCompleted -= NavHandler;
                        }
                    }

                    await wv.ExecuteScriptAsync(TriggerLazyLoadAndHoverPreviewsScript);
                    await Task.Delay(450, ct);

                    var currentUrl = wv.Source;
                    var rawJson = await wv.ExecuteScriptAsync(ExtractGeometricVideoCardsScript);
                    var json = TryUnwrapScriptString(rawJson);

                    if (string.IsNullOrWhiteSpace(json) || !Uri.TryCreate(currentUrl, UriKind.Absolute, out var pageUri))
                        return results;

                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return results;

                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var rawHref = el.TryGetProperty("href", out var hp) ? hp.GetString() : null;
                        var resolvedUrl = DirectWebsiteVideoScraper.ResolveCandidateVideoUrl(rawHref, pageUri, site);
                        if (string.IsNullOrWhiteSpace(resolvedUrl))
                            continue;

                        var dedup = DirectWebsiteVideoScraper.NormalizeUrlKey(resolvedUrl);
                        if (!seen.Add(dedup))
                            continue;

                        var title = (el.TryGetProperty("title", out var tp) ? tp.GetString() : null)?.Trim() ?? string.Empty;
                        var rawThumb = el.TryGetProperty("thumb", out var thp) ? thp.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(rawThumb) && rawThumb.Contains(' ') && rawThumb.Contains(','))
                            rawThumb = DirectWebsiteVideoScraper.ExtractBestUrlFromSrcset(rawThumb);

                        var thumb = DirectWebsiteVideoScraper.ResolveMediaUrl(rawThumb, pageUri);
                        var previewRaw = el.TryGetProperty("preview", out var pvp) ? pvp.GetString() : null;
                        var dur = (el.TryGetProperty("duration", out var dp) ? dp.GetString() : null)?.Trim() ?? string.Empty;

                        if (string.IsNullOrWhiteSpace(thumb) || string.IsNullOrWhiteSpace(title) || title.Length < 2)
                            continue;

                        var (mp4, webp) = !string.IsNullOrWhiteSpace(previewRaw)
                            ? DirectWebsiteVideoScraper.ClassifyPreviewUrl(previewRaw, pageUri)
                            : (string.Empty, string.Empty);

                        results.Add(new VideoResult
                        {
                            Id = $"uni_{Guid.NewGuid():N}",
                            Title = WebUtility.HtmlDecode(title),
                            Duration = dur,
                            ThumbnailUrl = thumb,
                            PreviewMp4Url = mp4,
                            PreviewWebpUrl = webp,
                            VideoPageUrl = resolvedUrl,
                            PlayerUrl = UniversalVideoSearchProvider.BuildBestPlayerUrl(resolvedUrl, site.RootDomain),
                            SourceSiteId = site.Id,
                            SourceSiteName = site.DisplayName,
                            SourceDomain = site.Domain
                        });
                    }

                    return results;
                }
                finally
                {
                    // Liberar memoria y conexiones del navegador oculto al terminar la extracción
                    try { wv.Navigate("about:blank"); } catch { }
                }
            }).Task.Unwrap();
        }
        catch
        {
            return [];
        }
        finally
        {
            _scraperLock.Release();
        }
    }

    public async Task<string?> NavigateAndRunScriptAsync(
        string url,
        string waitSelectorJs,
        string extractScriptJs,
        int timeoutMs = 6000,
        CancellationToken ct = default)
    {
        await _scraperLock.WaitAsync(ct);
        try
        {
            var rawResult = await Dispatcher.InvokeAsync(async () =>
            {
                if (!_scraperWebViewReady || ScraperWebView.CoreWebView2 == null)
                    return null;

                var wv = ScraperWebView.CoreWebView2;
                try
                {
                    await NavigateWebViewAndWaitAsync(wv, url, timeoutMs, ct);

                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 2800 && !ct.IsCancellationRequested)
                    {
                        var check = await wv.ExecuteScriptAsync(waitSelectorJs);
                        if (check == "true")
                            break;
                        await Task.Delay(220, ct);
                    }

                    return await wv.ExecuteScriptAsync(extractScriptJs);
                }
                finally
                {
                    try { wv.Navigate("about:blank"); } catch { }
                }
            }).Task.Unwrap();

            return TryUnwrapScriptString(rawResult);
        }
        catch
        {
            return null;
        }
        finally
        {
            _scraperLock.Release();
        }
    }

    private static async Task NavigateWebViewAndWaitAsync(CoreWebView2 wv, string url, int timeoutMs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs args) => tcs.TrySetResult(args.IsSuccess);

        wv.NavigationCompleted += Handler;
        try
        {
            wv.Navigate(url);
            await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct));
        }
        finally
        {
            wv.NavigationCompleted -= Handler;
        }
    }

    private const string DismissAgeGateScript = @"
(() => {
    try {
        document.cookie = 'age_verified=1; path=/';
        document.cookie = 'over18=1; path=/';
        document.cookie = 'over18=1; path=/; domain=.reddit.com';
        document.cookie = 'edgebucket=1; path=/; domain=.reddit.com';
        document.cookie = 'accessAgeDisclaimerPH=1; path=/';
        document.cookie = 'age_gate=1; path=/';
        document.cookie = 'disclaimer=1; path=/';
        const agePatterns = /\b(18\+|i am 18|over 18|mayor de 18|tengo 18|soy mayor|enter|entrar|i agree|estoy de acuerdo|aceptar y entrar|confirm age)\b/i;
        document.querySelectorAll('button, a, input[type=""button""], input[type=""submit""], [role=""button""]').forEach(btn => {
            const txt = (btn.innerText || btn.value || btn.getAttribute('aria-label') || '').trim();
            const idCls = ((btn.id || '') + ' ' + (btn.className || '')).toLowerCase();
            if ((txt.length > 0 && txt.length < 42 && agePatterns.test(txt)) ||
                idCls.includes('age-gate') || idCls.includes('disclaimer-enter') || idCls.includes('over18')) {
                btn.click();
            }
        });
    } catch(e) {}
})()";

    private static string BuildUniversalSearchSubmitScript(string query)
    {
        var jsonQuery = JsonSerializer.Serialize(query);
        return $@"
(() => {{
    const q = {jsonQuery};
    const inputs = Array.from(document.querySelectorAll('input'));
    let bestInput = null;
    let bestScore = -1;

    for (const inp of inputs) {{
        const type = (inp.getAttribute('type') || 'text').toLowerCase();
        if (['hidden','password','checkbox','radio','submit','button','file','email','number','date'].includes(type)) continue;

        const r = inp.getBoundingClientRect();
        let score = 0;
        if (r.width >= 30 && r.height >= 12) score += 25;
        if (type === 'search') score += 50;
        const meta = [
            inp.name, inp.id, inp.placeholder, inp.className,
            inp.getAttribute('aria-label'),
            inp.form ? (inp.form.action + ' ' + inp.form.id + ' ' + inp.form.className) : ''
        ].join(' ').toLowerCase();

        if (/search|buscar|busqueda|query|keyword|find|video/.test(meta)) score += 40;
        if (/\b(q|s|k|p|wd|term|txt)\b/.test(inp.name || '')) score += 30;
        if (r.top < 320) score += 15;

        if (score > bestScore) {{
            bestScore = score;
            bestInput = inp;
        }}
    }}

    if (!bestInput) return 'NO_INPUT';

    bestInput.focus();
    const nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
    if (nativeSetter) nativeSetter.call(bestInput, q);
    else bestInput.value = q;

    bestInput.dispatchEvent(new Event('input', {{ bubbles: true }}));
    bestInput.dispatchEvent(new Event('change', {{ bubbles: true }}));

    const form = bestInput.closest('form');
    if (form) {{
        const submitBtn = form.querySelector('button[type=""submit""], input[type=""submit""], button:not([type=""button""])');
        if (submitBtn) {{
            submitBtn.click();
            return 'SUBMITTED';
        }}
        if (typeof form.requestSubmit === 'function') {{
            form.requestSubmit();
            return 'SUBMITTED';
        }}
        form.submit();
        return 'SUBMITTED';
    }}

    bestInput.dispatchEvent(new KeyboardEvent('keydown', {{ key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }}));
    bestInput.dispatchEvent(new KeyboardEvent('keypress', {{ key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }}));
    bestInput.dispatchEvent(new KeyboardEvent('keyup', {{ key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }}));
    return 'SUBMITTED';
}})()";
    }

    private const string TriggerLazyLoadAndHoverPreviewsScript = @"
(() => {
    try {
        window.scrollBy(0, 650);
        setTimeout(() => window.scrollTo(0, 0), 180);
        const links = Array.from(document.querySelectorAll('a[href] img')).slice(0, 24);
        for (const img of links) {
            const a = img.closest('a');
            if (a) {
                a.dispatchEvent(new MouseEvent('mouseenter', { bubbles: true }));
                a.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
            }
        }
    } catch(e) {}
})()";

    private const string ExtractGeometricVideoCardsScript = @"
(() => {
    const items = [];
    const imgAttrs = ['data-src','data-original','data-thumb_url','data-mediumthumb','data-poster','data-lazy-src','data-lazy','data-webp','src'];
    const previewAttrs = ['data-mediabook','data-preview','data-video-preview','data-preview-url','data-previewvideo','data-trailer','data-mp4','data-webm','data-vurl','data-video','data-pvv','data-gif'];

    document.querySelectorAll('a[href]').forEach(a => {
        try {
            if (a.closest('nav, header, footer')) return;

            const img = a.querySelector('img');
            let thumb = '';
            let imgRect = null;

            if (img) {
                imgRect = img.getBoundingClientRect();
                if (img.currentSrc && !img.currentSrc.startsWith('data:image') && !img.currentSrc.includes('.svg') && !img.currentSrc.includes('spacer') && !img.currentSrc.includes('blank')) {
                    thumb = img.currentSrc;
                }
                if (!thumb) {
                    for (const attr of imgAttrs) {
                        const v = (img.getAttribute(attr) || '').trim();
                        if (v && !v.startsWith('data:image') && !v.includes('.svg') && !v.includes('spacer') && !v.includes('blank') && !v.includes('/logo') && !v.includes('/flags/')) {
                            thumb = v;
                            break;
                        }
                    }
                }
            } else {
                const bgEl = a.querySelector('[style*=""url(""]') || (a.getAttribute('style')?.includes('url(') ? a : null);
                if (bgEl) {
                    imgRect = bgEl.getBoundingClientRect();
                    const m = (bgEl.getAttribute('style') || '').match(/url\(['""]?([^'""\)]+)['""]?\)/i);
                    if (m && m[1] && !m[1].startsWith('data:image')) thumb = m[1];
                }
            }

            if (!thumb) return;
            if (imgRect && imgRect.width > 0 && imgRect.height > 0 && (imgRect.width < 70 || imgRect.height < 40)) return;

            let card = a;
            while (card.parentElement && card.parentElement !== document.body) {
                const pr = card.parentElement.getBoundingClientRect();
                if (pr.width > 0 && pr.width <= 540 && pr.height <= 560) {
                    card = card.parentElement;
                } else {
                    break;
                }
            }

            let preview = '';
            const cardNodes = [a, img, card, ...Array.from(card.querySelectorAll('*'))].filter(Boolean);
            for (const node of cardNodes) {
                if (node.tagName === 'VIDEO' || node.tagName === 'SOURCE') {
                    const s = node.src || node.getAttribute('src') || '';
                    if (s && s.length > 4) { preview = s; break; }
                }
                for (const pa of previewAttrs) {
                    const pv = node.getAttribute ? node.getAttribute(pa) : null;
                    if (pv && pv.length > 4) { preview = pv; break; }
                }
                if (preview) break;
            }

            let title = '';
            const titleCandidates = [
                a.getAttribute('title'),
                ...Array.from(card.querySelectorAll('a[title], [title]')).map(el => el.getAttribute('title')),
                ...Array.from(card.querySelectorAll('[class*=""title""], [class*=""name""], h2, h3, h4, p')).map(el => el.textContent),
                img ? img.getAttribute('alt') : ''
            ];

            for (const cand of titleCandidates) {
                const clean = (cand || '').replace(/\s+/g, ' ').trim();
                if (clean.length >= 3 && !/^\d{1,3}:\d{2}(:\d{2})?$/.test(clean) && !/^\d+\s*(min|sec|views|vistas|%)/i.test(clean)) {
                    title = clean;
                    break;
                }
            }

            if (!title) {
                const lines = (card.innerText || '').split('\n').map(l => l.trim()).filter(l =>
                    l.length >= 4 &&
                    !/^\d{1,3}:\d{2}(:\d{2})?$/.test(l) &&
                    !/^\d+(\.\d+)?[kKmM]?\s*(views|vistas|%)$/i.test(l) &&
                    !/^(HD|4K|1080p|720p|NEW)$/i.test(l)
                );
                if (lines.length > 0) title = lines[0];
            }

            let duration = '';
            const cardText = card.innerText || '';
            const dm = cardText.match(/\b(\d{1,3}:\d{2}(?::\d{2})?|\d{1,3}\s*min(?:\s*\d{1,2}\s*sec)?)\b/i);
            if (dm) duration = dm[1];

            items.push({
                href: a.href,
                title: title,
                thumb: thumb,
                preview: preview,
                duration: duration
            });
        } catch(e) {}
    });
    return JSON.stringify(items);
})()";

    // ==================== FAVORITOS, HISTORIAL Y AJUSTES ====================

    private void OpenFavoritesView_Click(object sender, RoutedEventArgs e)
    {
        NavToSearchArea_Click(sender, e);
        _searchCts?.Cancel();
        _previousSearchesStack.Clear();

        var favs = _favoritesService.LoadFavorites();
        _allSearchResults = favs.Select(f => f.ToVideoResult()).ToList();
        _currentSearchQuery = "⭐ Mis Favoritos";
        SearchQueryTextBox.Text = string.Empty;
        _selectedFilterSiteId = null;
        _currentPage = 1;

        FilterBarBorder.Visibility = Visibility.Collapsed;
        IncompatibleSearchOverlay.Visibility = Visibility.Collapsed;
        SearchWarningBanner.Visibility = Visibility.Collapsed;

        ApplyFilterAndRenderPage();
        UpdateSearchSessionButtonsState();

        if (_allSearchResults.Count == 0)
        {
            EmptyStateTitle.Text = "No tienes videos favoritos aún";
            EmptyStateSubtitle.Text = "Toca la estrella ☆ en la esquina superior derecha de cualquier video para guardarlo aquí.";
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
    }

    private void OpenHistoryView_Click(object sender, RoutedEventArgs e)
    {
        NavToSearchArea_Click(sender, e);
        _searchCts?.Cancel();
        _previousSearchesStack.Clear();

        var history = _playbackHistoryService.LoadHistory();
        _allSearchResults = history.Select(h => h.ToVideoResult()).ToList();
        _currentSearchQuery = "🕒 Historial de Reproducción";
        SearchQueryTextBox.Text = string.Empty;
        _selectedFilterSiteId = null;
        _currentPage = 1;

        FilterBarBorder.Visibility = Visibility.Collapsed;
        IncompatibleSearchOverlay.Visibility = Visibility.Collapsed;
        SearchWarningBanner.Visibility = Visibility.Collapsed;

        ApplyFilterAndRenderPage();
        UpdateSearchSessionButtonsState();

        if (_allSearchResults.Count == 0)
        {
            EmptyStateTitle.Text = "Historial de reproducción vacío";
            EmptyStateSubtitle.Text = "Los videos que reproduzcas dentro de la aplicación se guardarán automáticamente aquí para que puedas retomarlos cuando quieras.";
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
    }

    private void OpenSettingsModalButton_Click(object sender, RoutedEventArgs e)
    {
        ThemeDarkRadio.IsChecked = _appSettings.Theme == "Dark";
        ThemeLightRadio.IsChecked = _appSettings.Theme == "Light";
        ThemeCustomRadio.IsChecked = _appSettings.Theme == "Custom";
        CustomColorPalettePanel.Visibility = _appSettings.Theme == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        SettingsIncognitoCheckBox.IsChecked = _appSettings.IsIncognitoMode;
        SettingsAdultCheckBox.IsChecked = _appSettings.IsAdultContentEnabled;
        LangSpanishRadio.IsChecked = _appSettings.Language != "en";
        LangEnglishRadio.IsChecked = _appSettings.Language == "en";
        SettingsDownloadDirTextBox.Text = string.IsNullOrWhiteSpace(_appSettings.DefaultDownloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : _appSettings.DefaultDownloadDirectory;
        SettingsPlaylistAutoCheckBox.IsChecked = _appSettings.DownloadPlaylistsComplete;

        SettingsModalOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettingsModal_Click(object sender, RoutedEventArgs e)
    {
        _appSettings.DownloadPlaylistsComplete = SettingsPlaylistAutoCheckBox.IsChecked == true;
        _settingsService.SaveSettings(_appSettings);
        SettingsModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_appSettings == null || _settingsService == null) return;

        if (ThemeLightRadio?.IsChecked == true)
        {
            _appSettings.Theme = "Light";
            if (CustomColorPalettePanel != null) CustomColorPalettePanel.Visibility = Visibility.Collapsed;
            AppThemeService.ApplyTheme(AppTheme.Light);
        }
        else if (ThemeCustomRadio?.IsChecked == true)
        {
            _appSettings.Theme = "Custom";
            if (CustomColorPalettePanel != null) CustomColorPalettePanel.Visibility = Visibility.Visible;
            AppThemeService.ApplyTheme(AppTheme.Custom, _appSettings.CustomAccentColor);
        }
        else
        {
            _appSettings.Theme = "Dark";
            if (CustomColorPalettePanel != null) CustomColorPalettePanel.Visibility = Visibility.Collapsed;
            AppThemeService.ApplyTheme(AppTheme.Dark);
        }

        _settingsService.SaveSettings(_appSettings);
    }

    private void AccentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex)
        {
            _appSettings.CustomAccentColor = hex;
            _appSettings.Theme = "Custom";
            if (ThemeCustomRadio != null) ThemeCustomRadio.IsChecked = true;
            AppThemeService.ApplyTheme(AppTheme.Custom, hex);
            _settingsService.SaveSettings(_appSettings);
        }
    }

    private void SettingsIncognitoCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_appSettings == null || _settingsService == null) return;
        _appSettings.IsIncognitoMode = SettingsIncognitoCheckBox.IsChecked == true;
        _settingsService.SaveSettings(_appSettings);
        UpdateIncognitoBadgeVisual();
    }

    private void ToggleIncognitoFromBadge_Click(object sender, MouseButtonEventArgs e)
    {
        _appSettings.IsIncognitoMode = !_appSettings.IsIncognitoMode;
        _settingsService.SaveSettings(_appSettings);
        UpdateIncognitoBadgeVisual();
        if (SettingsIncognitoCheckBox != null)
        {
            SettingsIncognitoCheckBox.IsChecked = _appSettings.IsIncognitoMode;
        }
    }

    private void UpdateIncognitoBadgeVisual()
    {
        if (IncognitoIndicatorBadge != null && _appSettings != null)
        {
            IncognitoIndicatorBadge.Visibility = _appSettings.IsIncognitoMode ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SettingsChangeDownloadDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Seleccionar carpeta de descargas predeterminada",
            InitialDirectory = SettingsDownloadDirTextBox.Text
        };
        if (dlg.ShowDialog() == true)
        {
            _appSettings.DefaultDownloadDirectory = dlg.FolderName;
            SettingsDownloadDirTextBox.Text = dlg.FolderName;
            _currentDownloadDirectory = dlg.FolderName;
            DownloaderFolderTextBox.Text = dlg.FolderName;
            _settingsService.SaveSettings(_appSettings);
        }
    }

    private void SettingsClearSearchHistory_Click(object sender, RoutedEventArgs e)
    {
        _searchHistory.Clear();
        _settingsService.ClearSearchHistory();
        RefreshHistoryPopupState();
        System.Windows.MessageBox.Show("Historial de búsquedas eliminado correctamente.", "Ajustes", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SettingsClearPlaybackHistory_Click(object sender, RoutedEventArgs e)
    {
        _playbackHistoryService.ClearHistory();
        System.Windows.MessageBox.Show("Historial de reproducción eliminado correctamente.", "Ajustes", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SettingsClearFavorites_Click(object sender, RoutedEventArgs e)
    {
        _favoritesService.ClearFavorites();
        System.Windows.MessageBox.Show("Lista de favoritos vaciada correctamente.", "Ajustes", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SettingsAdultCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_appSettings == null || _settingsService == null) return;
        _appSettings.IsAdultContentEnabled = SettingsAdultCheckBox.IsChecked == true;
        ToggleNsfwPanelButton.Visibility = _appSettings.IsAdultContentEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!_appSettings.IsAdultContentEnabled && NsfwPanelGrid.Visibility == Visibility.Visible)
        {
            ShowCustomSitesButton_Click(ShowCustomSitesButton, new RoutedEventArgs());
        }
        _settingsService.SaveSettings(_appSettings);
    }

    private void LangRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_appSettings == null || _settingsService == null) return;
        _appSettings.Language = LangEnglishRadio?.IsChecked == true ? "en" : "es";
        LocalizationService.CurrentLanguage = _appSettings.Language;
        _settingsService.SaveSettings(_appSettings);
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        // Navegación Barra Superior
        if (SearchButton != null) SearchButton.Content = LocalizationService.T("SearchBtn");
        if (CancelSearchButton != null) CancelSearchButton.Content = LocalizationService.T("CancelBtn");
        if (RevertSearchButton != null) RevertSearchButton.Content = LocalizationService.T("RevertBtn");
        if (ClearSearchSessionButton != null) ClearSearchSessionButton.Content = LocalizationService.T("ClearBtn");
        if (OpenSettingsButton != null) OpenSettingsButton.Content = LocalizationService.T("Settings");

        // Barra Lateral
        if (ShowCustomSitesButton != null) ShowCustomSitesButton.Content = LocalizationService.T("MySites");
        if (ToggleNsfwPanelButton != null) ToggleNsfwPanelButton.Content = LocalizationService.T("NsfwSites");
        if (SidebarFavoritesButton != null) SidebarFavoritesButton.Content = LocalizationService.T("Favorites");
        if (SidebarHistoryButton != null) SidebarHistoryButton.Content = LocalizationService.T("History");

        // Ajustes Modal Textos
        if (SettingsIncognitoLabel != null) SettingsIncognitoLabel.Text = LocalizationService.T("SettingsIncognito");
        if (SettingsIncognitoSubLabel != null) SettingsIncognitoSubLabel.Text = LocalizationService.T("SettingsIncognitoSub");
        if (SettingsAdultLabel != null) SettingsAdultLabel.Text = LocalizationService.T("SettingsAdult");
        if (SettingsAdultSubLabel != null) SettingsAdultSubLabel.Text = LocalizationService.T("SettingsAdultSub");
    }

}
