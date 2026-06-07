using System.ComponentModel;

namespace SpoofGUI.Models;

public sealed class Subscription : INotifyPropertyChanged
{
    public long Id { get; set; }
    public string Name { get; set; } = "subscription";
    public string Url { get; set; } = "";

    private bool _autoUpdate = true;
    public bool AutoUpdate
    {
        get => _autoUpdate;
        set
        {
            if (_autoUpdate == value) return;
            _autoUpdate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoUpdate)));
        }
    }

    private string _lastUpdated = "";
    public string LastUpdated
    {
        get => _lastUpdated;
        set
        {
            if (_lastUpdated == value) return;
            _lastUpdated = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastUpdated)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLine)));
        }
    }

    private int _lastCount;
    public int LastCount
    {
        get => _lastCount;
        set
        {
            if (_lastCount == value) return;
            _lastCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLine)));
        }
    }

    public string StatusLine =>
        string.IsNullOrEmpty(LastUpdated) ? "never updated" : $"{LastCount} configs · {LastUpdated}";

    public event PropertyChangedEventHandler? PropertyChanged;
}
