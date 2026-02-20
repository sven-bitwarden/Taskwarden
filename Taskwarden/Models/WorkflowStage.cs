namespace Taskwarden.Models;

public enum WorkflowStage
{
    ToDo,
    InAnalysis,
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
