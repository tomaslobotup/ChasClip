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
    private const int VideosPerPage = 12; // Cuadrícula de 3 x 4 (12 videos por página)
    private const double ExpandedSidebarWidth = 340.0;
    private const string CafecitoDonationUrl = "https://cafecito.app/atomichead";

    private readonly AppSettingsService _settingsService = new();
    private readonly SiteStorageService _storageService = new();
    private readonly PornDudeCatalogService _nsfwService = new();
    private readonly AdBlockerService _adBlocker = new();
    private readonly VideoQualityAndDownloadService _qualityAndDownloadService = new();
    private readonly VideoSearchCoordinator _searchCoordinator;

    private readonly ObservableCollection<SavedSite> _savedSites = [];
    private readonly ObservableCollection<NsfwCategory> _nsfwCategories = [];
    private readonly ObservableCollection<SearchHistoryEntry> _searchHistory = [];
    private readonly Stack<(SavedSite Site, int OriginalIndex)> _deletedSitesUndoStack = new();
    private readonly Stack<SearchSessionSnapshot> _previousSearchesStack = new();
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
        InitializeComponent();

        _searchCoordinator = new VideoSearchCoordinator(this);
        _appSettings = _settingsService.LoadSettings();
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
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var animTask = PlayStartupAnimationAsync();
        var webViewTask = InitializeWebViewsAsync();
        await animTask;
        SearchQueryTextBox.Focus();
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
            PlayerWebView.CoreWebView2.Navigate(targetUrl);
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
                    "Boolean(document.querySelector('video, #movie_player, .html5-video-player, .jwplayer, .video-js, .fluid_video_wrapper, .plyr, iframe[src*=\"embed\"], iframe[src*=\"player\"], iframe[src*=\"tiktok.com/player\"], iframe[src*=\"bilibili.com/player\"]'))");
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
                    PlayerWebView.CoreWebView2.Navigate(nextDmUrl);
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

        try
        {
            string? pageCookies = null;
            if (_playerWebViewReady && PlayerWebView.CoreWebView2 != null)
            {
                var currentSrc = PlayerWebView.Source?.ToString() ?? video.VideoPageUrl;
                pageCookies = await GetCookiesHeaderForUrlAsync(PlayerWebView.CoreWebView2, currentSrc);

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
                cts.Token);

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
                "Video no compatible para descarga directa",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
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

        // 0) Si ya estamos en un player dedicado limpio (Dailymotion, Vimeo, TikTok player/v1, Bilibili player), respetar su UI nativa
        if (host.includes('geo.dailymotion.com') ||
            host.includes('player.vimeo.com') ||
            host.includes('player.bilibili.com') ||
            location.pathname.includes('/player/v1/')) {
            document.documentElement.style.background = '#000';
            document.body.style.background = '#000';
            document.body.style.margin = '0';
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

        // 3) Para reproductores MSE/HLS (blob:, JWPlayer, Video.js, FluidPlayer, Plyr o iframe player):
        const target =
            document.querySelector('#movie_player, .html5-video-player, .jwplayer, .video-js, .fluid_video_wrapper, .plyr') ||
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
                    el.style.setProperty('display', 'none', 'important');
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
}
