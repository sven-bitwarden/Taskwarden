namespace Taskwarden.Configuration;

public class GitHubOptions
{
    public const string SectionName = "GitHub";

    public string PersonalAccessToken { get; set; } = string.Empty;
    public string Organization { get; set; } = string.Empty;
}
