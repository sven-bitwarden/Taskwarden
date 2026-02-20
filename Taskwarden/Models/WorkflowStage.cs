namespace Taskwarden.Models;

public enum WorkflowStage
{
    ToDo,
    InProgress,
    CodeReview,
    ReadyForQa,
    InQa,
    ReadyForMerge,
    Blocked,
    ProductReview,
    Done,
    Unknown
}
