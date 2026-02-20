namespace Taskwarden.Configuration;

public class TaskWardenOptions
{
    public const string SectionName = "TaskWarden";

    public int RefreshIntervalMinutes { get; set; } = 10;
}
