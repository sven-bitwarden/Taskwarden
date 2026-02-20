namespace Taskwarden.Models;

public record GitHubFetchResult
{
    public required Dictionary<string, List<GitHubPullRequest>> AuthoredPrsByTicket { get; init; }
    public required List<(string? TicketKey, GitHubPullRequest Pr)> ReviewRequests { get; init; }
    public required List<(string? TicketKey, GitHubPullRequest Pr)> ReviewedPrs { get; init; }
}
