namespace Taskwarden.Models;

public record JiraLinkedIssue
{
    public required string Key { get; init; }
    public required string StatusCategoryKey { get; init; }
    public required string LinkType { get; init; }
}

public record JiraTicket
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public required string StatusName { get; init; }
    public string? StatusCategoryKey { get; init; }
    public string? IssueTypeName { get; init; }
    public string? PriorityName { get; init; }
    public string? ProjectKey { get; init; }
    public required string BrowseUrl { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<JiraLinkedIssue> LinkedIssues { get; init; } = [];
    public string? SprintName { get; init; }
    public string? SprintState { get; init; }
}
