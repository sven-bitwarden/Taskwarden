namespace Taskwarden.Models;

public record WorkItem
{
    public required string TicketKey { get; init; }
    public required JiraTicket Ticket { get; init; }
    public IReadOnlyList<GitHubPullRequest> PullRequests { get; init; } = [];
    public GitHubPullRequest? PrimaryPullRequest { get; init; }
    public WorkflowStage Stage { get; init; }
    public AttentionStatus Attention { get; init; }
    public string? AttentionReason { get; init; }
    public DateTimeOffset LastRefreshed { get; init; }
}
