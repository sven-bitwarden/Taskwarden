namespace Taskwarden.Configuration;

public class JiraOptions
{
    public const string SectionName = "Jira";

    public string BaseUrl { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Maps Jira status names to WorkflowStage enum values.
    /// Key = Jira status name (case-insensitive match), Value = WorkflowStage name.
    /// </summary>
    public Dictionary<string, string> StatusMappings { get; set; } = new();

    /// <summary>
    /// Jira board name used to look up the active sprint (e.g. "Admin Console").
    /// </summary>
    public string? BoardName { get; set; }
}
