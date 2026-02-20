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

    public DashboardSnapshot Snapshot => _snapshot;

    public event Action? StateChanged;

    public void SetLoading()
    {
        _snapshot = _snapshot with { IsLoading = true, Error = null };
        NotifyStateChanged();
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
        NotifyStateChanged();
    }

    public void SetUserInfo(string? gitHubLogin, string? jiraDisplayName, SprintInfo? activeSprint = null)
    {
        _snapshot = _snapshot with { GitHubLogin = gitHubLogin, JiraDisplayName = jiraDisplayName, ActiveSprint = activeSprint };
        NotifyStateChanged();
    }

    public void SetError(string error)
    {
        _snapshot = _snapshot with { IsLoading = false, Error = error };
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();
}
