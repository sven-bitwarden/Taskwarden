# TaskWarden

Personal Blazor Server dashboard that aggregates Jira tickets and GitHub PRs across multiple repos at Bitwarden. Shows what needs attention vs. what's waiting on others.

## Tech Stack

- .NET 10.0, Blazor Server (Interactive Server rendering)
- Octokit 14.0.0 (GitHub API)
- Jira REST API v3 (HTTP + Basic auth)
- Catppuccin Mocha dark theme (CSS custom properties in `wwwroot/app.css`)

## Project Structure

```
Taskwarden/
  Configuration/       Options pattern classes (JiraOptions, GitHubOptions, TaskWardenOptions)
  Models/              Immutable records and enums (WorkItem, JiraTicket, GitHubPullRequest, etc.)
  Services/            API integrations and aggregation logic
    JiraService.cs       POST /rest/api/3/search/jql, cursor pagination, sprint field discovery
    GitHubService.cs     Octokit search PRs by user, match branches to ticket keys
    WorkItemAggregator.cs  Correlates Jira + GitHub, computes stage + attention
  State/               DashboardStateContainer (singleton, volatile snapshot, StateChanged event)
  Background/          RefreshHostedService (10-min timer + manual trigger)
  Components/
    Layout/            MainLayout (no sidebar, full-width container)
    Pages/             Dashboard.razor (main page at /)
    Shared/            WorkItemCard, StatusBadge, AttentionIndicator, DashboardToolbar, etc.
  wwwroot/app.css      Catppuccin Mocha theme with CSS custom properties
  Program.cs           DI wiring, options binding, service registration
```

## Architecture & Data Flow

```
RefreshHostedService (10-min timer or manual)
  -> WorkItemAggregator.AggregateAsync()
     -> JiraService.GetMyTicketsAsync()     (parallel)
     -> GitHubService.FindPullRequestsForUserAsync()  (parallel)
     -> Match PRs to tickets by branch name
     -> Compute WorkflowStage + AttentionStatus
     -> Sort: NeedsMyAttention > WaitingOnOthers > None
  -> DashboardStateContainer.SetData()
     -> StateChanged event
        -> Dashboard.razor re-renders
```

## Key Design Decisions

### Jira API
- All API calls go through the Atlassian API gateway: `https://api.atlassian.com/ex/jira/{CloudId}/rest/...`
- `SiteUrl` (e.g. `https://bitwarden.atlassian.net/`) is only used for browse links shown to the user
- Uses POST `/rest/api/3/search/jql` (GET endpoint returns 410 Gone)
- Cursor-based pagination via `nextPageToken` (not `startAt`)
- Auth: per-request `Authorization: Basic` header (Base64 of `email:apiToken`)
- Sprint field is a custom field (e.g. `customfield_10020`). Discovered dynamically at startup via `GET /rest/api/3/field` by matching `clauseNames` containing `"sprint"`. Cached after first call.
- JQL: `assignee = currentUser() AND (statusCategory != Done OR (status changed TO Done AFTER -7d))`

### GitHub API
- Single org-wide search call instead of N calls per ticket
- Open PRs: `org:{org} author:{login} is:pr is:open`
- Recently merged: `org:{org} author:{login} is:pr is:merged merged:>{7d ago}`
- Branch-to-ticket matching: regex `^ac/([A-Za-z]+-\d+)/` (primary), fallback `[A-Z]+-\d+` anywhere
- Semaphore (5 concurrent) for rate limiting on PR detail fetches
- `Issue.Repository` is null from search results; repo extracted from `HtmlUrl` as fallback

### State Management
- `DashboardStateContainer`: singleton, holds a `volatile DashboardSnapshot` (immutable record)
- Thread-safe via reference swap, no locks
- Blazor components subscribe to `StateChanged` event, call `InvokeAsync(StateHasChanged)`

### Workflow Stage Mapping
Jira status names are mapped to `WorkflowStage` enum via `StatusMappings` dictionary in config (case-insensitive). Add new mappings in `appsettings.json` under `Jira.StatusMappings`.

Current mappings: To Do, In Progress, Code Review, Ready for QA, In QA, Ready for Merge, Blocked, Done.

### Attention Logic
```
Future sprint          -> None ("Future sprint (name)")
ToDo                   -> NeedsMyAttention ("Start work")
InProgress + no PR     -> NeedsMyAttention ("Create a branch and PR")
InProgress + draft     -> None (working)
InProgress + open PR   -> NeedsMyAttention ("Move to Code Review?")
CodeReview + changes   -> NeedsMyAttention ("Address feedback")
CodeReview             -> WaitingOnOthers ("Waiting for review")
ReadyForQa             -> WaitingOnOthers
InQa                   -> WaitingOnOthers
ReadyForMerge          -> NeedsMyAttention ("Merge the PR")
Blocked                -> WaitingOnOthers
Done                   -> None
```

## Configuration

Credentials are in `appsettings.json` (should be moved to user-secrets):

```json
{
  "Jira": {
    "SiteUrl": "https://bitwarden.atlassian.net/",
    "Email": "...",
    "ApiToken": "...",
    "StatusMappings": { "To Do": "ToDo", "In Progress": "InProgress", ... }
  },
  "GitHub": {
    "PersonalAccessToken": "ghp_...",
    "Organization": "bitwarden"
  },
  "TaskWarden": {
    "RefreshIntervalMinutes": 10
  }
}
```

Required token scopes:
- **Jira**: API token from https://id.atlassian.com/manage-profile/security/api-tokens (inherits account permissions)
- **GitHub PAT (classic)**: `repo` scope. Fine-grained: Pull requests (read), Contents (read)

## Build & Run

```sh
dotnet build
dotnet run --project Taskwarden
# Dashboard at https://localhost:7112
```

## UI Notes

- Catppuccin Mocha palette defined as CSS custom properties in `wwwroot/app.css`
- Cards use CSS `columns` layout (masonry-style, 340px column width)
- Each card has a debug modal (info icon, top-right) showing raw Jira/GitHub data and computed state
- Scoped component CSS files (`.razor.css`) use Catppuccin color literals, not the CSS variables (Blazor CSS isolation doesn't support `:root` vars in scoped styles)
- Inline SVG icons for Jira (blue diamond) and GitHub (PR merge arrow) links

## Common Tasks

### Add a new Jira status mapping
1. Add entry to `appsettings.json` -> `Jira.StatusMappings` (key = exact Jira status name, value = WorkflowStage enum name)
2. If it's a new stage: add to `WorkflowStage` enum, add attention rule in `WorkItemAggregator.ComputeAttention`, add badge color in `StatusBadge.razor`

### Add a new filter
1. Add value to `DashboardToolbar.Filter` enum
2. Add button in `DashboardToolbar.razor`
3. Add case in `Dashboard.razor` `GetFilteredItems`

### Debug why a ticket shows wrong status
Click the info icon on any card to see the debug modal. Check:
- `Jira > Status`: raw status name from Jira
- `Jira > Sprint` / `Sprint State`: whether it's in a future sprint
- `Computed > Stage`: what WorkflowStage it mapped to
- `Computed > Attention` / `Reason`: the attention logic result
- If stage is "Unknown", the Jira status name is missing from `StatusMappings`
