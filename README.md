# TaskWarden

Personal dashboard that aggregates Jira tickets and GitHub PRs across an organization. Shows what needs your attention vs. what's waiting on others.

Built with .NET 10, Blazor Server, and a Bitwarden dark theme.

## Setup

1. Copy `.env.template` to `.env` and fill in your credentials:

   ```
   cp .env.template .env
   ```

   - **Jira API token**: generate at https://id.atlassian.com/manage-profile/security/api-tokens, use a scoped token, with classic "read" permissions
   - **GitHub PAT (classic)**: uses `repo` scope

2. Build and run:

   ```
   dotnet run --project Taskwarden
   ```

3. Open https://localhost:7112
