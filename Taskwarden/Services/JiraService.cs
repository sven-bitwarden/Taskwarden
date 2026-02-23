using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Taskwarden.Configuration;
using Taskwarden.Models;

namespace Taskwarden.Services;

public class JiraService(HttpClient httpClient, IOptions<JiraOptions> options, ILogger<JiraService> logger)
    : IJiraService
{
    private const int PageSize = 50;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly JiraOptions _options = options.Value;
    private string ApiBaseUrl => $"https://api.atlassian.com/ex/jira/{_options.CloudId}";
    private string? _sprintFieldId;
    private int? _boardId;

    public async Task<IReadOnlyList<JiraTicket>> GetMyTicketsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.CloudId) ||
            string.IsNullOrWhiteSpace(_options.Email) ||
            string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            throw new InvalidOperationException("Jira configuration is incomplete. Set CloudId, Email, and ApiToken.");
        }

        _sprintFieldId ??= await DiscoverSprintFieldIdAsync(cancellationToken);

        var requestedFields = new List<string>
            { "summary", "status", "issuetype", "priority", "project", "updated", "labels" };
        if (_sprintFieldId is not null)
            requestedFields.Add(_sprintFieldId);

        var jql = "assignee = currentUser() AND (statusCategory != Done OR (status changed TO Done AFTER -7d)) ORDER BY updated DESC";
        var tickets = new List<JiraTicket>();
        string? nextPageToken = null;

        while (true)
        {
            var url = $"{ApiBaseUrl}/rest/api/3/search/jql";
            var bodyDict = new Dictionary<string, object>
            {
                ["jql"] = jql,
                ["maxResults"] = PageSize,
                ["fields"] = requestedFields
            };
            if (nextPageToken is not null)
                bodyDict["nextPageToken"] = nextPageToken;

            var request = CreateRequest(HttpMethod.Post, url);
            request.Content = JsonContent.Create(bodyDict);
            var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
            var issues = json.GetProperty("issues");

            foreach (var issue in issues.EnumerateArray())
            {
                tickets.Add(ParseTicket(issue));
            }

            if (json.TryGetProperty("nextPageToken", out var tokenProp) &&
                tokenProp.ValueKind == JsonValueKind.String)
            {
                nextPageToken = tokenProp.GetString();
            }
            else
            {
                break;
            }
        }

        logger.LogInformation("Fetched {Count} Jira tickets", tickets.Count);
        return tickets;
    }

    public async Task<IReadOnlyList<JiraTicket>> GetTicketsByKeysAsync(
        IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0)
            return [];

        _sprintFieldId ??= await DiscoverSprintFieldIdAsync(cancellationToken);

        var requestedFields = new List<string>
            { "summary", "status", "issuetype", "priority", "project", "updated", "labels" };
        if (_sprintFieldId is not null)
            requestedFields.Add(_sprintFieldId);

        var keyList = string.Join(", ", keys);
        var jql = $"key in ({keyList}) ORDER BY updated DESC";

        var url = $"{ApiBaseUrl}/rest/api/3/search/jql";
        var bodyDict = new Dictionary<string, object>
        {
            ["jql"] = jql,
            ["maxResults"] = keys.Count,
            ["fields"] = requestedFields
        };

        var request = CreateRequest(HttpMethod.Post, url);
        request.Content = JsonContent.Create(bodyDict);
        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
        var issues = json.GetProperty("issues");

        var tickets = new List<JiraTicket>();
        foreach (var issue in issues.EnumerateArray())
        {
            tickets.Add(ParseTicket(issue));
        }

        logger.LogInformation("Fetched {Count} tickets by key", tickets.Count);
        return tickets;
    }

    private async Task<string?> DiscoverSprintFieldIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{ApiBaseUrl}/rest/api/3/field";
            var request = CreateRequest(HttpMethod.Get, url);
            var response = await httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var fields = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
            foreach (var field in fields.EnumerateArray())
            {
                // Look for the Sprint field — it's a custom field with a known clause name
                var name = field.TryGetProperty("name", out var n) ? n.GetString() : null;
                var clauseNames = field.TryGetProperty("clauseNames", out var cn) ? cn : default;
                var id = field.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

                if (id is null) continue;

                // Match by clause name "sprint" or field name "Sprint"
                if (clauseNames.ValueKind == JsonValueKind.Array &&
                    clauseNames.EnumerateArray().Any(c => string.Equals(c.GetString(), "sprint", StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogInformation("Discovered sprint field: {Id} ({Name})", id, name);
                    return id;
                }
            }

            logger.LogWarning("Could not find sprint field in Jira field metadata");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to discover sprint field ID, sprint data will be unavailable");
            return null;
        }
    }

    public async Task<SprintInfo?> GetActiveSprintAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BoardName))
            return null;

        try
        {
            _boardId ??= await ResolveBoardIdAsync(cancellationToken);
            if (_boardId is null)
                return null;

            var sprintUrl = $"{ApiBaseUrl}/rest/agile/1.0/board/{_boardId.Value}/sprint?state=active";
            var sprintRequest = CreateRequest(HttpMethod.Get, sprintUrl);
            var sprintResponse = await httpClient.SendAsync(sprintRequest, cancellationToken);
            sprintResponse.EnsureSuccessStatusCode();

            var sprintJson = await sprintResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
            var sprints = sprintJson.GetProperty("values");
            if (sprints.GetArrayLength() == 0)
                return null;

            var sprint = sprints[0];
            var name = sprint.GetProperty("name").GetString()!;
            var startDate = DateTimeOffset.Parse(sprint.GetProperty("startDate").GetString()!);
            var endDate = DateTimeOffset.Parse(sprint.GetProperty("endDate").GetString()!);

            return new SprintInfo(name, startDate, endDate);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch active sprint for board '{BoardName}'", _options.BoardName);
            return null;
        }
    }

    private async Task<int?> ResolveBoardIdAsync(CancellationToken cancellationToken)
    {
        var boardUrl = $"{ApiBaseUrl}/rest/agile/1.0/board?name={Uri.EscapeDataString(_options.BoardName!)}";
        var boardRequest = CreateRequest(HttpMethod.Get, boardUrl);
        var boardResponse = await httpClient.SendAsync(boardRequest, cancellationToken);
        boardResponse.EnsureSuccessStatusCode();

        var boardJson = await boardResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
        var boards = boardJson.GetProperty("values");
        if (boards.GetArrayLength() == 0)
        {
            logger.LogWarning("No board found with name '{BoardName}'", _options.BoardName);
            return null;
        }

        // Prefer exact name match since the API does a "contains" search
        foreach (var board in boards.EnumerateArray())
        {
            var boardName = board.GetProperty("name").GetString();
            if (string.Equals(boardName, _options.BoardName, StringComparison.OrdinalIgnoreCase))
                return board.GetProperty("id").GetInt32();
        }

        return boards[0].GetProperty("id").GetInt32();
    }

    public async Task<string> GetCurrentUserDisplayNameAsync(CancellationToken cancellationToken = default)
    {
        var url = $"{ApiBaseUrl}/rest/api/3/myself";
        var request = CreateRequest(HttpMethod.Get, url);
        var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
        return json.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()!
            : _options.Email;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Email}:{_options.ApiToken}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private (string? Name, string? State) ParseSprintFromFields(JsonElement fields)
    {
        if (_sprintFieldId is null)
            return (null, null);

        if (!fields.TryGetProperty(_sprintFieldId, out var sprint) || sprint.ValueKind == JsonValueKind.Null)
            return (null, null);

        // Sprint can be an object (single sprint) or array
        if (sprint.ValueKind == JsonValueKind.Object)
            return (GetString(sprint, "name"), GetString(sprint, "state"));

        if (sprint.ValueKind == JsonValueKind.Array)
        {
            // Prefer active sprint, then future, then last closed
            foreach (var s in sprint.EnumerateArray())
            {
                if (GetString(s, "state") == "active")
                    return (GetString(s, "name"), "active");
            }
            foreach (var s in sprint.EnumerateArray())
            {
                if (GetString(s, "state") == "future")
                    return (GetString(s, "name"), "future");
            }
            var last = sprint.EnumerateArray().LastOrDefault();
            if (last.ValueKind == JsonValueKind.Object)
                return (GetString(last, "name"), GetString(last, "state"));
        }

        return (null, null);
    }

    private static string? GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private JiraTicket ParseTicket(JsonElement issue)
    {
        var fields = issue.GetProperty("fields");
        var key = issue.GetProperty("key").GetString()!;
        var status = fields.GetProperty("status");
        var (sprintName, sprintState) = ParseSprintFromFields(fields);

        return new JiraTicket
        {
            Key = key,
            Summary = fields.GetProperty("summary").GetString() ?? string.Empty,
            StatusName = status.GetProperty("name").GetString() ?? "Unknown",
            StatusCategoryKey = status.TryGetProperty("statusCategory", out var cat)
                ? cat.GetProperty("key").GetString()
                : null,
            IssueTypeName = fields.TryGetProperty("issuetype", out var issueType) && issueType.ValueKind != JsonValueKind.Null
                ? issueType.GetProperty("name").GetString()
                : null,
            PriorityName = fields.TryGetProperty("priority", out var priority) && priority.ValueKind != JsonValueKind.Null
                ? priority.GetProperty("name").GetString()
                : null,
            ProjectKey = fields.TryGetProperty("project", out var project) && project.ValueKind != JsonValueKind.Null
                ? project.GetProperty("key").GetString()
                : null,
            BrowseUrl = $"{_options.SiteUrl.TrimEnd('/')}/browse/{key}",
            UpdatedAt = fields.TryGetProperty("updated", out var updated)
                ? DateTimeOffset.Parse(updated.GetString()!)
                : null,
            Labels = fields.TryGetProperty("labels", out var labels)
                ? labels.EnumerateArray().Select(l => l.GetString()!).ToList()
                : [],
            SprintName = sprintName,
            SprintState = sprintState
        };
    }
}
