using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BuscadorVideos.Models;

/// <summary>
/// Representa una entrada en el historial de navegación y búsqueda del buscador web.
/// </summary>
public class BrowserHistoryItem : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _url = string.Empty;
    private DateTime _visitedAt = DateTime.Now;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
            }
        }
    }

    public string Url
    {
        get => _url;
        set
        {
            if (_url != value)
            {
                _url = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime VisitedAt
    {
        get => _visitedAt;
        set
        {
            if (_visitedAt != value)
            {
                _visitedAt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedTime));
            }
        }
    }

    public string FormattedTime => VisitedAt.ToString("HH:mm - dd/MM/yyyy");

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
