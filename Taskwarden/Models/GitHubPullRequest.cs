namespace Taskwarden.Models;

public record GitHubPullRequest
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string RepositoryFullName { get; init; }
    public required string HeadBranch { get; init; }
    public string? State { get; init; }
    public bool IsDraft { get; init; }
    public bool IsMerged { get; init; }
    public string? ReviewState { get; init; }
    public IReadOnlyList<string> PendingReviewers { get; init; } = [];
    public IReadOnlyList<string> Labels { get; init; } = [];
    public DateTimeOffset? UpdatedAt { get; init; }
}
