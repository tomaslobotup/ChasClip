using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BuscadorVideos.Models;

/// <summary>
/// Representa una página guardada por el usuario con su Nombre y su Enlace (URL).
/// </summary>
public class SavedSite : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _displayName = string.Empty;
    private string _url = string.Empty;
    private string _domain = string.Empty;
    private bool _isEnabled = true;

    public string Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Enlace completo ingresado por el usuario (ej. "https://en.luxuretv.com" o "https://www.youtube.com").
    /// </summary>
    public string Url
    {
        get => string.IsNullOrWhiteSpace(_url) ? $"https://{Domain}" : _url;
        set
        {
            var normalizedUrl = NormalizeUrlInput(value);
            if (_url != normalizedUrl)
            {
                _url = normalizedUrl;
                _domain = ExtractHostFromUrl(_url);
                OnPropertyChanged();
                OnPropertyChanged(nameof(Domain));
                OnPropertyChanged(nameof(BaseUrl));
                OnPropertyChanged(nameof(RootDomain));
                OnPropertyChanged(nameof(AccentHex));
            }
        }
    }

    public string Domain
    {
        get => string.IsNullOrWhiteSpace(_domain) ? ExtractHostFromUrl(_url) : _domain;
        set
        {
            var host = ExtractHostFromUrl(value);
            if (_domain != host)
            {
                _domain = host;
                if (string.IsNullOrWhiteSpace(_url))
                    _url = $"https://{_domain}";
                OnPropertyChanged();
                OnPropertyChanged(nameof(Url));
                OnPropertyChanged(nameof(BaseUrl));
                OnPropertyChanged(nameof(RootDomain));
                OnPropertyChanged(nameof(AccentHex));
            }
        }
    }

    [JsonIgnore]
    public string BaseUrl
    {
        get
        {
            if (Uri.TryCreate(Url, UriKind.Absolute, out var u))
                return $"{u.Scheme}://{u.Host.ToLowerInvariant()}";
            return $"https://{Domain}";
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public string RootDomain => ExtractRootDomain(Domain);

    [JsonIgnore]
    public string AccentHex => GetAccentColorForDomain(RootDomain);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static readonly HashSet<string> TwoPartTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "co.jp", "ne.jp", "com.ar", "com.mx", "com.br",
        "com.co", "com.cl", "com.pe", "com.es", "com.au", "co.kr", "com.cn", "com.tw"
    };

    public static SavedSite Create(string name, string urlOrDomain)
    {
        var cleanUrl = NormalizeUrlInput(urlOrDomain);
        if (string.IsNullOrWhiteSpace(cleanUrl))
            throw new ArgumentException("Debes ingresar un enlace o dominio válido.");

        var host = ExtractHostFromUrl(cleanUrl);
        var displayName = string.IsNullOrWhiteSpace(name) ? host : name.Trim();

        return new SavedSite
        {
            DisplayName = displayName,
            Url = cleanUrl,
            Domain = host,
            IsEnabled = true
        };
    }

    public static SavedSite FromUserInput(string rawUrlOrDomain)
        => Create(string.Empty, rawUrlOrDomain);

    public static string NormalizeUrlInput(string input)
    {
        var trimmed = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!trimmed.Contains('.'))
                trimmed += ".com";
            trimmed = "https://" + trimmed;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? uri.ToString()
            : trimmed;
    }

    public static string ExtractHostFromUrl(string input)
    {
        var normalized = NormalizeUrlInput(input);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            var host = uri.Host.ToLowerInvariant();
            return host.StartsWith("www.") ? host[4..] : host;
        }
        return normalized.ToLowerInvariant();
    }

    public static string ExtractRootDomain(string domainOrHost)
    {
        var host = ExtractHostFromUrl(domainOrHost);
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2)
            return host;

        var lastTwo = $"{parts[^2]}.{parts[^1]}";
        if (TwoPartTlds.Contains(lastTwo) && parts.Length >= 3)
            return $"{parts[^3]}.{lastTwo}";

        return lastTwo;
    }

    public bool MatchesUrl(string? candidateUrl)
    {
        if (string.IsNullOrWhiteSpace(candidateUrl) || !Uri.TryCreate(candidateUrl, UriKind.Absolute, out var uri))
            return false;

        var urlRoot = ExtractRootDomain(uri.Host);
        var siteRoot = RootDomain;

        if (string.Equals(urlRoot, siteRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        if (siteRoot == "youtube.com" && urlRoot == "youtu.be")
            return true;

        return false;
    }

    public SavedSite Clone() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Url = Url,
        Domain = Domain,
        IsEnabled = IsEnabled
    };

    public static string GetAccentColorForDomain(string domain)
    {
        var d = (domain ?? string.Empty).ToLowerInvariant();
        if (d.Contains("youtube") || d.Contains("youtu.be")) return "#FF2E4C";
        if (d.Contains("tiktok")) return "#00E5D4";
        if (d.Contains("dailymotion")) return "#2F80ED";
        if (d.Contains("vimeo")) return "#1AB7EA";
        if (d.Contains("twitch")) return "#9146FF";
        if (d.Contains("bilibili")) return "#00A1D6";
        if (d.Contains("rumble")) return "#85C742";
        if (d.Contains("reddit")) return "#FF5722";
        if (d.Contains("pornhub")) return "#FF9900";
        if (d.Contains("xvideos")) return "#E03131";
        if (d.Contains("xnxx")) return "#3B82F6";
        if (d.Contains("xhamster")) return "#F43F5E";
        return "#7C5CFF";
    }
}
