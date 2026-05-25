using System.ComponentModel;

namespace Noticker.Models;

public class AppSettings : INotifyPropertyChanged
{
    public static AppSettings Instance { get; } = new();

    private AppSettings() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _colorSwapped;
    public bool ColorSwapped
    {
        get => _colorSwapped;
        set { _colorSwapped = value; Notify(nameof(ColorSwapped)); }
    }

    private bool _isSyncPaused;
    public bool IsSyncPaused
    {
        get => _isSyncPaused;
        set { _isSyncPaused = value; Notify(nameof(IsSyncPaused)); }
    }

    public string? NotionToken { get; set; }
    public string? TargetDbId { get; set; }
    public string CategoryPropertyName { get; set; } = "Category";
    public bool AutostartEnabled { get; set; }
    public List<string> CategoryOptions { get; set; } = [];
    public Dictionary<string, string> CategoryColors { get; set; } = [];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(NotionToken) &&
        !string.IsNullOrWhiteSpace(TargetDbId);
}
