using Microsoft.Extensions.Options;
using Taskwarden.Configuration;
using Taskwarden.Models;

namespace Taskwarden.Services;

public class WorkItemAggregator(
    IJiraService jiraService,
    IGitHubService gitHubService,
    IOptions<JiraOptions> jiraOptions,
    ILogger<WorkItemAggregator> logger)
    : IWorkItemAggregator
{
    private readonly Dictionary<string, string> _statusMappings = jiraOptions.Value.StatusMappings;

    public async Task<IReadOnlyList<WorkItem>> AggregateAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Fetching Jira tickets, GitHub PRs, and review requests");

        var ticketsTask = jiraService.GetMyTicketsAsync(cancellationToken);
        var githubTask = gitHubService.FetchAllPullRequestDataAsync(cancellationToken);

        await Task.WhenAll(ticketsTask, githubTask);

        var tickets = ticketsTask.Result;
        var githubData = githubTask.Result;
        var prsByTicket = githubData.AuthoredPrsByTicket;
        var reviewRequests = githubData.ReviewRequests;
        var reviewedPrs = githubData.ReviewedPrs;
        var now = DateTimeOffset.UtcNow;

        progress?.Report($"Received {tickets.Count} tickets, {prsByTicket.Count} PR groups, {reviewRequests.Count} review requests, {reviewedPrs.Count} reviewed PRs");

        logger.LogInformation("Aggregating {TicketCount} tickets with {PrGroups} PR groups and {ReviewCount} review requests",
            tickets.Count, prsByTicket.Count, reviewRequests.Count);

        // Find PR ticket keys that aren't in the Jira results and fetch them
        var ticketKeys = new HashSet<string>(tickets.Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
        var missingKeys = prsByTicket.Keys
            .Where(k => !ticketKeys.Contains(k))
            .ToList();

        if (missingKeys.Count > 0)
        {
            progress?.Report($"Fetching {missingKeys.Count} extra tickets found via PRs");
            logger.LogInformation("Fetching {Count} tickets found via PRs but not assigned to me: {Keys}",
                missingKeys.Count, string.Join(", ", missingKeys));

            var extraTickets = await jiraService.GetTicketsByKeysAsync(missingKeys, cancellationToken);
            tickets = tickets.Concat(extraTickets).ToList();
        }

        var workItems = tickets.Select(ticket =>
        {
            var prs = prsByTicket.TryGetValue(ticket.Key, out var list) ? list : [];
            var primaryPr = SelectPrimaryPr(prs);
            var stage = MapStage(ticket.StatusName);
            var (attention, reason) = ComputeAttention(ticket, stage, primaryPr, prs);

            return new WorkItem
            {
                TicketKey = ticket.Key,
                Ticket = ticket,
                PullRequests = prs,
                PrimaryPullRequest = primaryPr,
                Stage = stage,
                Attention = attention,
                AttentionReason = reason,
                LastRefreshed = now
            };
        }).ToList();

        // Build review request WorkItems
        var existingKeys = new HashSet<string>(workItems.Select(w => w.TicketKey), StringComparer.OrdinalIgnoreCase);

        // Group review PRs by ticket key (or by PR identity for orphans)
        var reviewsByTicket = new Dictionary<string, List<GitHubPullRequest>>(StringComparer.OrdinalIgnoreCase);
        var orphanReviewPrs = new List<GitHubPullRequest>();

        foreach (var (ticketKey, pr) in reviewRequests)
        {
            if (ticketKey is not null)
            {
                if (!reviewsByTicket.TryGetValue(ticketKey, out var list))
                {
                    list = [];
                    reviewsByTicket[ticketKey] = list;
                }
                list.Add(pr!);
            }
            else
            {
                orphanReviewPrs.Add(pr!);
            }
        }

        progress?.Report("Matching review requests to Jira tickets");

        // Fetch Jira tickets for review PRs that have ticket keys
        var reviewTicketKeys = reviewsByTicket.Keys
            .Where(k => !existingKeys.Contains(k))
            .ToList();

        var reviewTicketsById = new Dictionary<string, JiraTicket>(StringComparer.OrdinalIgnoreCase);
        if (reviewTicketKeys.Count > 0)
        {
            var reviewTickets = await jiraService.GetTicketsByKeysAsync(reviewTicketKeys, cancellationToken);
            foreach (var t in reviewTickets)
                reviewTicketsById[t.Key] = t;
        }

        // Create WorkItems for review PRs with ticket keys
        foreach (var (key, prs) in reviewsByTicket)
        {
            // Skip if this ticket is already in our work items (it's our own PR)
            if (existingKeys.Contains(key))
                continue;

            var ticket = reviewTicketsById.TryGetValue(key, out var t)
                ? t
                : CreateStubTicket(key, prs[0]);

            var primaryPr = SelectPrimaryPr(prs);
            var stage = MapStage(ticket.StatusName);
            var (jiraAttention, jiraReason) = ComputeAttention(ticket, stage, primaryPr, prs);

            // If the Jira status indicates waiting or idle, the review IS actionable for us
            var attention = jiraAttention is AttentionStatus.WaitingOnOthers or AttentionStatus.None
                ? AttentionStatus.NeedsMyReview
                : AttentionStatus.WaitingOnOthers;

            workItems.Add(new WorkItem
            {
                TicketKey = key,
                Ticket = ticket,
                PullRequests = prs,
                PrimaryPullRequest = primaryPr,
                Stage = stage,
                Attention = attention,
                AttentionReason = attention == AttentionStatus.NeedsMyReview
                    ? "Review requested"
                    : jiraReason ?? $"Waiting ({ticket.StatusName})",
                LastRefreshed = now
            });
        }

        // Create WorkItems for orphan review PRs (no ticket key found)
        foreach (var pr in orphanReviewPrs)
        {
            var stubKey = $"{FormatRepoShortName(pr.RepositoryFullName)}#{pr.Number}";
            var ticket = CreateStubTicket(stubKey, pr);

            workItems.Add(new WorkItem
            {
                TicketKey = stubKey,
                Ticket = ticket,
                PullRequests = [pr],
                PrimaryPullRequest = pr,
                Stage = WorkflowStage.CodeReview,
                Attention = AttentionStatus.NeedsMyReview,
                AttentionReason = "Review requested",
                LastRefreshed = now
            });
        }

        // Build "already reviewed" WorkItems from PRs the user has reviewed
        var allKeys = new HashSet<string>(workItems.Select(w => w.TicketKey), StringComparer.OrdinalIgnoreCase);
        var reviewedByTicket = new Dictionary<string, List<GitHubPullRequest>>(StringComparer.OrdinalIgnoreCase);
        var orphanReviewedPrs = new List<GitHubPullRequest>();

        foreach (var (ticketKey, pr) in reviewedPrs)
        {
            if (ticketKey is not null)
            {
                if (!reviewedByTicket.TryGetValue(ticketKey, out var list))
                {
                    list = [];
                    reviewedByTicket[ticketKey] = list;
                }
                list.Add(pr!);
            }
            else
            {
                orphanReviewedPrs.Add(pr!);
            }
        }

        // Fetch Jira tickets for reviewed PRs
        var reviewedTicketKeys = reviewedByTicket.Keys
            .Where(k => !allKeys.Contains(k))
            .ToList();

        var reviewedTicketsById = new Dictionary<string, JiraTicket>(StringComparer.OrdinalIgnoreCase);
        if (reviewedTicketKeys.Count > 0)
        {
            var reviewedTickets = await jiraService.GetTicketsByKeysAsync(reviewedTicketKeys, cancellationToken);
            foreach (var t in reviewedTickets)
                reviewedTicketsById[t.Key] = t;
        }

        foreach (var (key, prs) in reviewedByTicket)
        {
            if (allKeys.Contains(key))
                continue;

            var ticket = reviewedTicketsById.TryGetValue(key, out var t)
                ? t
                : CreateStubTicket(key, prs[0]);

            var primaryPr = SelectPrimaryPr(prs);

            workItems.Add(new WorkItem
            {
                TicketKey = key,
                Ticket = ticket,
                PullRequests = prs,
                PrimaryPullRequest = primaryPr,
                Stage = MapStage(ticket.StatusName),
                Attention = AttentionStatus.Reviewed,
                AttentionReason = "Review submitted",
                LastRefreshed = now
            });
        }

        foreach (var pr in orphanReviewedPrs)
        {
            var stubKey = $"{FormatRepoShortName(pr.RepositoryFullName)}#{pr.Number}";
            if (allKeys.Contains(stubKey))
                continue;

            var ticket = CreateStubTicket(stubKey, pr);

            workItems.Add(new WorkItem
            {
                TicketKey = stubKey,
                Ticket = ticket,
                PullRequests = [pr],
                PrimaryPullRequest = pr,
                Stage = WorkflowStage.CodeReview,
                Attention = AttentionStatus.Reviewed,
                AttentionReason = "Review submitted",
                LastRefreshed = now
            });
        }

        progress?.Report($"Built {workItems.Count} work items, sorting");

        // Sort: NeedsMyAttention first, then WaitingOnOthers, then None, then NeedsMyReview
        workItems.Sort((a, b) =>
        {
            var cmp = a.Attention.CompareTo(b.Attention);
            if (cmp != 0) return cmp;
            // Within same attention level, sort by updated descending
            return (b.Ticket.UpdatedAt ?? DateTimeOffset.MinValue)
                .CompareTo(a.Ticket.UpdatedAt ?? DateTimeOffset.MinValue);
        });

        return workItems;
    }

    private WorkflowStage MapStage(string jiraStatusName)
    {
        if (_statusMappings.TryGetValue(jiraStatusName, out var mapped) &&
            Enum.TryParse<WorkflowStage>(mapped, ignoreCase: true, out var stage))
        {
            return stage;
        }

        // Case-insensitive fallback lookup
        var match = _statusMappings.FirstOrDefault(kvp =>
            string.Equals(kvp.Key, jiraStatusName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(match.Value) &&
            Enum.TryParse<WorkflowStage>(match.Value, ignoreCase: true, out var fallbackStage))
        {
            return fallbackStage;
        }

        logger.LogWarning("Unknown Jira status '{Status}', mapping to Unknown", jiraStatusName);
        return WorkflowStage.Unknown;
    }

    private static GitHubPullRequest? SelectPrimaryPr(IReadOnlyList<GitHubPullRequest> prs)
    {
        if (prs.Count == 0) return null;

        // Prefer open non-draft PRs, then open draft, then most recently updated
        return prs
            .OrderByDescending(pr => pr.State == "open" && !pr.IsDraft ? 2 : pr.State == "open" ? 1 : 0)
            .ThenByDescending(pr => pr.UpdatedAt ?? DateTimeOffset.MinValue)
            .First();
    }

    private static (AttentionStatus Attention, string? Reason) ComputeAttention(
        JiraTicket ticket,
        WorkflowStage stage,
        GitHubPullRequest? primaryPr,
        IReadOnlyList<GitHubPullRequest> prs)
    {
        // Future sprint tickets are deprioritized regardless of status
        if (string.Equals(ticket.SprintState, "future", StringComparison.OrdinalIgnoreCase))
            return (AttentionStatus.None, $"Future sprint ({ticket.SprintName})");

        var hasAnyPr = prs.Count > 0;
        var hasOpenPr = prs.Any(p => p.State == "open");
        var hasDraftPr = prs.Any(p => p is { State: "open", IsDraft: true });
        var hasNonDraftOpenPr = prs.Any(p => p is { State: "open", IsDraft: false });

        return stage switch
        {
            WorkflowStage.ToDo =>
                (AttentionStatus.NeedsMyAttention, "Start work on this ticket"),

            WorkflowStage.InAnalysis =>
                (AttentionStatus.NeedsMyAttention, "In analysis"),

            WorkflowStage.InProgress when !hasAnyPr =>
                (AttentionStatus.NeedsMyAttention, "Create a branch and PR"),

            WorkflowStage.InProgress when hasDraftPr && !hasNonDraftOpenPr =>
                (AttentionStatus.None, null),

            WorkflowStage.InProgress when hasNonDraftOpenPr =>
                (AttentionStatus.NeedsMyAttention, "PR is open - move ticket to Code Review?"),

            WorkflowStage.CodeReview when primaryPr?.ReviewState == "changes_requested" =>
                (AttentionStatus.NeedsMyAttention, "Address review feedback"),

            WorkflowStage.CodeReview when primaryPr?.ReviewState == "approved" && primaryPr.PendingReviewers.Count > 0 =>
                (AttentionStatus.WaitingOnOthers, "Waiting for remaining reviewers"),

            WorkflowStage.CodeReview when primaryPr?.ReviewState == "approved" =>
                (AttentionStatus.NeedsMyAttention, "PR approved — move to QA or merge"),

            WorkflowStage.CodeReview =>
                (AttentionStatus.WaitingOnOthers, "Waiting for code review"),

            WorkflowStage.ReadyForQa =>
                (AttentionStatus.WaitingOnOthers, "Waiting for QA"),

            WorkflowStage.InQa =>
                (AttentionStatus.WaitingOnOthers, "In QA testing"),

            WorkflowStage.ReadyForMerge =>
                (AttentionStatus.NeedsMyAttention, "Merge the PR"),

            WorkflowStage.Blocked =>
                (AttentionStatus.WaitingOnOthers, "Blocked"),

            WorkflowStage.ProductReview =>
                (AttentionStatus.WaitingOnOthers, "Waiting for product review"),

            WorkflowStage.Done =>
                (AttentionStatus.None, null),

            _ => (AttentionStatus.None, null)
        };
    }

    private static JiraTicket CreateStubTicket(string key, GitHubPullRequest pr)
    {
        return new JiraTicket
        {
            Key = key,
            Summary = pr.Title,
            StatusName = "Code Review",
            BrowseUrl = pr.Url,
            UpdatedAt = pr.UpdatedAt
        };
    }

    private static string FormatRepoShortName(string repoFullName)
    {
        return repoFullName.Split('/').Last();
    }
}
