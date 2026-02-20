using Taskwarden.Models;

namespace Taskwarden.Services;

public interface IWorkItemAggregator
{
    Task<IReadOnlyList<WorkItem>> AggregateAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}
