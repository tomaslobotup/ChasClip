using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BuscadorVideos.Models;

/// <summary>
/// Categoría desplegable de páginas NSFW (extraída de theporndude.com/es) con selección grupal o individual.
/// </summary>
public class NsfwCategory : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private bool _isExpanded;
    private ObservableCollection<SavedSite> _sites = [];

    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeaderSummary));
            }
        }
    }

    [JsonIgnore]
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<SavedSite> Sites
    {
        get => _sites;
        set
        {
            _sites = value ?? [];
            AttachSiteListeners();
            OnPropertyChanged();
            RefreshSelectionState();
        }
    }

    /// <summary>
    /// Casilla maestra de la categoría: permite marcar o desmarcar todas las páginas de la categoría de un toque.
    /// </summary>
    [JsonIgnore]
    public bool IsCategoryChecked
    {
        get => Sites.Count > 0 && Sites.All(s => s.IsEnabled);
        set
        {
            foreach (var s in Sites)
                s.IsEnabled = value;
            RefreshSelectionState();
        }
    }

    [JsonIgnore]
    public int EnabledCount => Sites.Count(s => s.IsEnabled);

    [JsonIgnore]
    public string HeaderSummary => EnabledCount > 0
        ? $"{Name} ({EnabledCount}/{Sites.Count})"
        : $"{Name} ({Sites.Count})";

    public void AttachSiteListeners()
    {
        foreach (var site in _sites)
        {
            site.PropertyChanged -= Site_PropertyChanged;
            site.PropertyChanged += Site_PropertyChanged;
        }
    }

    private void Site_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SavedSite.IsEnabled))
            RefreshSelectionState();
    }

    public void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(IsCategoryChecked));
        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(HeaderSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
