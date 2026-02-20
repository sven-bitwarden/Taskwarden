using Taskwarden.Models;

namespace Taskwarden.State;

public record DashboardSnapshot
{
    public IReadOnlyList<WorkItem> WorkItems { get; init; } = [];
    public DateTimeOffset? LastRefreshed { get; init; }
    public bool IsLoading { get; init; }
    public string? Error { get; init; }
    public string? GitHubLogin { get; init; }
    public string? JiraDisplayName { get; init; }
    public SprintInfo? ActiveSprint { get; init; }
}

public class DashboardStateContainer
{
    private volatile DashboardSnapshot _snapshot = new();
    private readonly List<string> _progressMessages = [];
    private readonly object _progressLock = new();

    public DashboardSnapshot Snapshot => _snapshot;

    public IReadOnlyList<string> ProgressMessages
    {
        get { lock (_progressLock) return [.. _progressMessages]; }
    }

    public bool IsRefreshing { get; private set; }

    public event Action? StateChanged;
    public event Action? ProgressChanged;

    public void SetLoading()
    {
        IsRefreshing = true;
        lock (_progressLock) _progressMessages.Clear();
        _snapshot = _snapshot with { IsLoading = true, Error = null };
        NotifyStateChanged();
        NotifyProgressChanged();
    }

    public void AddProgress(string message)
    {
        lock (_progressLock) _progressMessages.Add(message);
        NotifyProgressChanged();
    }

    public void SetData(IReadOnlyList<WorkItem> workItems)
    {
        _snapshot = new DashboardSnapshot
        {
            WorkItems = workItems,
            LastRefreshed = DateTimeOffset.UtcNow,
            IsLoading = false,
            Error = null,
            GitHubLogin = _snapshot.GitHubLogin,
            JiraDisplayName = _snapshot.JiraDisplayName,
            ActiveSprint = _snapshot.ActiveSprint
        };
        IsRefreshing = false;
        NotifyStateChanged();
        NotifyProgressChanged();
    }

    public void SetUserInfo(string? gitHubLogin, string? jiraDisplayName, SprintInfo? activeSprint = null)
    {
        _snapshot = _snapshot with { GitHubLogin = gitHubLogin, JiraDisplayName = jiraDisplayName, ActiveSprint = activeSprint };
        NotifyStateChanged();
    }

    public void SetError(string error)
    {
        _snapshot = _snapshot with { IsLoading = false, Error = error };
        IsRefreshing = false;
        NotifyStateChanged();
        NotifyProgressChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();
    private void NotifyProgressChanged() => ProgressChanged?.Invoke();
}
