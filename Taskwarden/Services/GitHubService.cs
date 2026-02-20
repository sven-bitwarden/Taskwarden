using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Octokit;
using Taskwarden.Configuration;
using Taskwarden.Models;
using GitHubPullRequest = Taskwarden.Models.GitHubPullRequest;

namespace Taskwarden.Services;

public partial class GitHubService : IGitHubService
{
    private readonly GitHubClient _client;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubService> _logger;
    private readonly SemaphoreSlim _rateLimiter = new(5, 5);

    public GitHubService(IOptions<GitHubOptions> options, ILogger<GitHubService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new GitHubClient(new ProductHeaderValue("TaskWarden"))
        {
            Credentials = new Credentials(_options.PersonalAccessToken)
        };
    }

    public async Task<string> GetCurrentUserLoginAsync()
    {
        var user = await _client.User.Current();
        return user.Login;
    }

    public async Task<Dictionary<string, List<GitHubPullRequest>>> FindPullRequestsForUserAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.PersonalAccessToken) ||
            string.IsNullOrWhiteSpace(_options.Organization))
        {
            throw new InvalidOperationException(
                "GitHub configuration is incomplete. Set PersonalAccessToken and Organization.");
        }

        var user = await _client.User.Current();
        var login = user.Login;
        _logger.LogDebug("GitHub authenticated as {Login}", login);

        var openPrs = await SearchPrsAsync($"org:{_options.Organization} author:{login} is:pr is:open");

        var mergedSince = DateTimeOffset.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var mergedPrs = await SearchPrsAsync($"org:{_options.Organization} author:{login} is:pr is:merged merged:>{mergedSince}");

        var allPrs = openPrs.Concat(mergedPrs)
            .DistinctBy(pr => (ExtractRepoFullName(pr), pr.Number))
            .ToList();

        _logger.LogInformation("Found {Count} PRs for {Login}", allPrs.Count, login);

        var result = new Dictionary<string, List<GitHubPullRequest>>(StringComparer.OrdinalIgnoreCase);

        var detailTasks = allPrs.Select(async pr =>
        {
            await _rateLimiter.WaitAsync(cancellationToken);
            try
            {
                return await GetPrDetailsAsync(pr);
            }
            finally
            {
                _rateLimiter.Release();
            }
        });

        var details = await Task.WhenAll(detailTasks);

        foreach (var (ticketKey, ghPr) in details)
        {
            if (ticketKey is null || ghPr is null)
                continue;

            if (!result.TryGetValue(ticketKey, out var list))
            {
                list = [];
                result[ticketKey] = list;
            }

            list.Add(ghPr);
        }

        return result;
    }

    public async Task<List<(string? TicketKey, GitHubPullRequest Pr)>> FindReviewRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.PersonalAccessToken) ||
            string.IsNullOrWhiteSpace(_options.Organization))
        {
            return [];
        }

        var user = await _client.User.Current();
        var login = user.Login;

        var prs = await SearchPrsAsync($"org:{_options.Organization} review-requested:{login} is:pr is:open");
        _logger.LogInformation("Found {Count} PRs requesting review from {Login}", prs.Count, login);

        var detailTasks = prs.Select(async pr =>
        {
            await _rateLimiter.WaitAsync(cancellationToken);
            try
            {
                return await GetPrDetailsAsync(pr, requireReviewerLogin: login);
            }
            finally
            {
                _rateLimiter.Release();
            }
        });

        var details = await Task.WhenAll(detailTasks);
        return details
            .Where(d => d.Pr is not null)
            .Select(d => (d.TicketKey, d.Pr!))
            .ToList();
    }

    private async Task<IReadOnlyList<Octokit.Issue>> SearchPrsAsync(string query)
    {
        _logger.LogDebug("Searching GitHub PRs: {Query}", query);

        var request = new SearchIssuesRequest(query);
        var results = await _client.Search.SearchIssues(request);
        return results.Items;
    }

    private async Task<(string? TicketKey, GitHubPullRequest? Pr)> GetPrDetailsAsync(
        Octokit.Issue searchResult, string? requireReviewerLogin = null)
    {
        try
        {
            var repoFullName = ExtractRepoFullName(searchResult);
            if (repoFullName is null)
            {
                _logger.LogWarning("Could not determine repo for issue #{Number}", searchResult.Number);
                return (null, null);
            }
            var parts = repoFullName.Split('/');
            var owner = parts[0];
            var repo = parts[1];

            var pr = await _client.PullRequest.Get(owner, repo, searchResult.Number);

            // Only include PRs where the user is directly listed as a reviewer
            if (requireReviewerLogin is not null &&
                pr.RequestedReviewers?.Any(r =>
                    string.Equals(r.Login, requireReviewerLogin, StringComparison.OrdinalIgnoreCase)) != true)
            {
                _logger.LogDebug("Skipping PR #{Number} — {Login} not directly requested as reviewer",
                    pr.Number, requireReviewerLogin);
                return (null, null);
            }

            var ticketKey = ExtractTicketKey(pr.Head.Ref) ?? ExtractTicketKeyFromTitle(pr.Title);

            var reviews = await _client.PullRequest.Review.GetAll(owner, repo, pr.Number);
            var reviewState = DetermineReviewState(reviews);
            var pendingReviewers = pr.RequestedReviewers?.Select(r => r.Login).ToList() ?? [];

            var ghPr = new GitHubPullRequest
            {
                Number = pr.Number,
                Title = pr.Title,
                Url = pr.HtmlUrl,
                RepositoryFullName = repoFullName,
                HeadBranch = pr.Head.Ref,
                State = pr.Merged ? "merged" : pr.State.StringValue,
                IsDraft = pr.Draft,
                IsMerged = pr.Merged,
                ReviewState = reviewState,
                PendingReviewers = pendingReviewers,
                Labels = pr.Labels?.Select(l => l.Name).ToList() ?? [],
                UpdatedAt = pr.UpdatedAt
            };

            return (ticketKey, ghPr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch details for PR #{Number}", searchResult.Number);
            return (null, null);
        }
    }

    /// <summary>
    /// Extracts "owner/repo" from the issue. Prefers Repository.FullName,
    /// falls back to parsing the HtmlUrl (e.g. https://github.com/owner/repo/pull/123).
    /// </summary>
    private static string? ExtractRepoFullName(Octokit.Issue issue)
    {
        if (issue.Repository?.FullName is { } fullName)
            return fullName;

        if (issue.HtmlUrl is { } htmlUrl && Uri.TryCreate(htmlUrl, UriKind.Absolute, out var uri))
        {
            // /owner/repo/pull/123
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
                return $"{segments[0]}/{segments[1]}";
        }

        return null;
    }

    private static string? ExtractTicketKeyFromTitle(string? title)
    {
        if (string.IsNullOrEmpty(title))
            return null;

        var match = TitleTicketKeyRegex().Match(title);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string? ExtractTicketKey(string branchName)
    {
        // Match ac/<ticket-key>/ pattern (e.g., ac/PM-1234/my-feature)
        var match = TicketKeyRegex().Match(branchName);
        if (match.Success)
            return match.Groups[1].Value.ToUpperInvariant();

        // Fallback: match common patterns like PM-1234 anywhere in the branch
        var fallback = FallbackTicketKeyRegex().Match(branchName);
        return fallback.Success ? fallback.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string? DetermineReviewState(IReadOnlyList<PullRequestReview> reviews)
    {
        if (reviews.Count == 0)
            return null;

        // Get the latest review per reviewer
        var latestByReviewer = reviews
            .Where(r => r.State.Value is PullRequestReviewState.Approved
                or PullRequestReviewState.ChangesRequested)
            .GroupBy(r => r.User.Login)
            .Select(g => g.OrderByDescending(r => r.SubmittedAt).First())
            .ToList();

        if (latestByReviewer.Any(r => r.State.Value == PullRequestReviewState.ChangesRequested))
            return "changes_requested";

        if (latestByReviewer.Any(r => r.State.Value == PullRequestReviewState.Approved))
            return "approved";

        return "pending";
    }

    [GeneratedRegex(@"^ac/([A-Za-z]+-\d+)/", RegexOptions.IgnoreCase)]
    private static partial Regex TicketKeyRegex();

    [GeneratedRegex(@"([A-Z]+-\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FallbackTicketKeyRegex();

    [GeneratedRegex(@"^\[([A-Za-z]+-\d+)\]")]
    private static partial Regex TitleTicketKeyRegex();
}
