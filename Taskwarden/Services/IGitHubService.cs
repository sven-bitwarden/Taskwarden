using Taskwarden.Models;

namespace Taskwarden.Services;

public interface IGitHubService
{
    /// <summary>
    /// Finds all open and recently merged PRs authored by the authenticated user across the org.
    /// Returns a dictionary keyed by Jira ticket key (extracted from branch name).
    /// </summary>
    Task<Dictionary<string, List<GitHubPullRequest>>> FindPullRequestsForUserAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds open PRs where the authenticated user is a requested reviewer.
    /// Returns a list of (TicketKey, PR) tuples. TicketKey may be null if not extractable.
    /// </summary>
    Task<List<(string? TicketKey, GitHubPullRequest Pr)>> FindReviewRequestsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds open PRs that the authenticated user has already reviewed (approved or commented).
    /// Returns a list of (TicketKey, PR) tuples. TicketKey may be null if not extractable.
    /// </summary>
    Task<List<(string? TicketKey, GitHubPullRequest Pr)>> FindReviewedPullRequestsAsync(CancellationToken cancellationToken = default);

    Task<string> GetCurrentUserLoginAsync();
}
