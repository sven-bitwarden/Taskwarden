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
    private string? _cachedLogin;

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
        if (_cachedLogin is not null)
            return _cachedLogin;

        var user = await _client.User.Current();
        _cachedLogin = user.Login;
        return _cachedLogin;
    }

    public async Task<GitHubFetchResult> FetchAllPullRequestDataAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.PersonalAccessToken) ||
            string.IsNullOrWhiteSpace(_options.Organization))
        {
            throw new InvalidOperationException(
                "GitHub configuration is incomplete. Set PersonalAccessToken and Organization.");
        }

        var login = await GetCurrentUserLoginAsync();
        _logger.LogDebug("GitHub authenticated as {Login}", login);

        var mergedSince = DateTimeOffset.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");

        // Run all 4 search queries in parallel
        var authoredOpenTask = SearchPrsAsync($"org:{_options.Organization} author:{login} is:pr is:open");
        var authoredMergedTask = SearchPrsAsync($"org:{_options.Organization} author:{login} is:pr is:merged merged:>{mergedSince}");
        var reviewRequestedTask = SearchPrsAsync($"org:{_options.Organization} review-requested:{login} is:pr is:open");
        var reviewedByTask = SearchPrsAsync($"org:{_options.Organization} reviewed-by:{login} is:pr is:open -author:{login}");

        await Task.WhenAll(authoredOpenTask, authoredMergedTask, reviewRequestedTask, reviewedByTask);

        var authoredOpen = authoredOpenTask.Result;
        var authoredMerged = authoredMergedTask.Result;
        var reviewRequested = reviewRequestedTask.Result;
        var reviewedBy = reviewedByTask.Result;

        _logger.LogInformation(
            "GitHub search results — authored open: {Open}, authored merged: {Merged}, review requested: {ReviewReq}, reviewed by: {Reviewed}",
            authoredOpen.Count, authoredMerged.Count, reviewRequested.Count, reviewedBy.Count);

        // Deduplicate by (repoFullName, number), tracking which searches each PR came from
        var prMap = new Dictionary<(string Repo, int Number), (Issue Issue, PrSource Sources)>();

        void AddResults(IReadOnlyList<Issue> issues, PrSource source)
        {
            foreach (var issue in issues)
            {
                var repo = ExtractRepoFullName(issue);
                if (repo is null) continue;
                var key = (repo, issue.Number);
                if (prMap.TryGetValue(key, out var existing))
                    prMap[key] = (existing.Issue, existing.Sources | source);
                else
                    prMap[key] = (issue, source);
            }
        }

        AddResults(authoredOpen, PrSource.AuthoredOpen);
        AddResults(authoredMerged, PrSource.AuthoredMerged);
        AddResults(reviewRequested, PrSource.ReviewRequested);
        AddResults(reviewedBy, PrSource.ReviewedBy);

        _logger.LogInformation("Deduplicated to {Unique} unique PRs from {Total} search results",
            prMap.Count, authoredOpen.Count + authoredMerged.Count + reviewRequested.Count + reviewedBy.Count);

        // Fetch details for all unique PRs (one PullRequest.Get + Review.GetAll per PR)
        var detailTasks = prMap.Select(async kvp =>
        {
            await _rateLimiter.WaitAsync(cancellationToken);
            try
            {
                var (ticketKey, pr) = await GetPrDetailsAsync(kvp.Value.Issue);
                return (Sources: kvp.Value.Sources, TicketKey: ticketKey, Pr: pr);
            }
            finally
            {
                _rateLimiter.Release();
            }
        });

        var details = await Task.WhenAll(detailTasks);

        // Route each PR to the appropriate result bucket(s) based on source tags
        var authoredPrsByTicket = new Dictionary<string, List<GitHubPullRequest>>(StringComparer.OrdinalIgnoreCase);
        var reviewRequestsList = new List<(string? TicketKey, GitHubPullRequest Pr)>();
        var reviewedPrsList = new List<(string? TicketKey, GitHubPullRequest Pr)>();

        foreach (var detail in details)
        {
            if (detail.Pr is null) continue;

            // Authored PRs (open or merged)
            if ((detail.Sources & (PrSource.AuthoredOpen | PrSource.AuthoredMerged)) != 0
                && detail.TicketKey is not null)
            {
                if (!authoredPrsByTicket.TryGetValue(detail.TicketKey, out var list))
                {
                    list = [];
                    authoredPrsByTicket[detail.TicketKey] = list;
                }
                list.Add(detail.Pr);
            }

            // Review requests — filter to PRs where user is still a requested reviewer
            if ((detail.Sources & PrSource.ReviewRequested) != 0)
            {
                if (detail.Pr.PendingReviewers.Any(r =>
                    string.Equals(r, login, StringComparison.OrdinalIgnoreCase)))
                {
                    reviewRequestsList.Add((detail.TicketKey, detail.Pr));
                }
                else
                {
                    _logger.LogDebug("Skipping PR #{Number} — {Login} not directly requested as reviewer",
                        detail.Pr.Number, login);
                }
            }

            // Already-reviewed PRs
            if ((detail.Sources & PrSource.ReviewedBy) != 0)
            {
                reviewedPrsList.Add((detail.TicketKey, detail.Pr));
            }
        }

        return new GitHubFetchResult
        {
            AuthoredPrsByTicket = authoredPrsByTicket,
            ReviewRequests = reviewRequestsList,
            ReviewedPrs = reviewedPrsList
        };
    }

    private async Task<IReadOnlyList<Issue>> SearchPrsAsync(string query)
    {
        _logger.LogDebug("Searching GitHub PRs: {Query}", query);

        var request = new SearchIssuesRequest(query);
        var results = await _client.Search.SearchIssues(request);
        return results.Items;
    }

    private async Task<(string? TicketKey, GitHubPullRequest? Pr)> GetPrDetailsAsync(Issue searchResult)
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
    private static string? ExtractRepoFullName(Issue issue)
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

    [Flags]
    private enum PrSource
    {
        AuthoredOpen = 1,
        AuthoredMerged = 2,
        ReviewRequested = 4,
        ReviewedBy = 8
    }

    [GeneratedRegex(@"^ac/([A-Za-z]+-\d+)/", RegexOptions.IgnoreCase)]
    private static partial Regex TicketKeyRegex();

    [GeneratedRegex(@"([A-Z]+-\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FallbackTicketKeyRegex();

    [GeneratedRegex(@"^\[([A-Za-z]+-\d+)\]")]
    private static partial Regex TitleTicketKeyRegex();
}
