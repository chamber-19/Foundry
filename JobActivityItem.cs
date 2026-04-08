using DailyDesk.ViewModels;

namespace DailyDesk.Models;

public sealed class JobActivityItem : ObservableObject
{
    private string _title = string.Empty;
    private string _agent = string.Empty;
    private string _model = string.Empty;
    private string _status = "queued";
    private string _summary = string.Empty;
    private DateTimeOffset _startedAt = DateTimeOffset.Now;
    private DateTimeOffset? _completedAt;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Agent
    {
        get => _agent;
        set => SetProperty(ref _agent, value);
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    public string Status
    {
        get => _status;
        set
        {
            if (!SetProperty(ref _status, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(DisplaySummary));
            OnPropertyChanged(nameof(DisplayMeta));
        }
    }

    public string Summary
    {
        get => _summary;
        set
        {
            if (!SetProperty(ref _summary, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DisplaySummary));
        }
    }

    public DateTimeOffset StartedAt
    {
        get => _startedAt;
        set
        {
            if (!SetProperty(ref _startedAt, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DisplayMeta));
        }
    }

    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set
        {
            if (!SetProperty(ref _completedAt, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DisplayMeta));
        }
    }

    public bool IsActive =>
        Status.Equals("queued", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("running", StringComparison.OrdinalIgnoreCase);

    public string DisplayMeta =>
        string.IsNullOrWhiteSpace(Model)
            ? $"{Agent} | {Status}"
            : $"{Agent} | {Model} | {Status}";

    public string DisplaySummary =>
        string.IsNullOrWhiteSpace(Summary) ? Title : $"{Title} | {Summary}";
}
