using Taskwarden.Models;

namespace Taskwarden.Services;

public interface IJiraService
{
    Task<IReadOnlyList<JiraTicket>> GetMyTicketsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JiraTicket>> GetTicketsByKeysAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default);

    Task<string> GetCurrentUserDisplayNameAsync(CancellationToken cancellationToken = default);

    Task<SprintInfo?> GetActiveSprintAsync(CancellationToken cancellationToken = default);
}
