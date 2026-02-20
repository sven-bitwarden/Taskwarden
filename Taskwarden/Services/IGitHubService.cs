using Taskwarden.Models;

namespace Taskwarden.Services;

public interface IGitHubService
{
    /// <summary>
    /// Fetches all GitHub PR data in a single consolidated operation: authored PRs,
    /// review requests, and already-reviewed PRs. Deduplicates detail fetches for
    /// PRs appearing in multiple search results.
    /// </summary>
    Task<GitHubFetchResult> FetchAllPullRequestDataAsync(CancellationToken cancellationToken = default);

    Task<string> GetCurrentUserLoginAsync();
}
