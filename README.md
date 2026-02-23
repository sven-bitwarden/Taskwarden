# TaskWarden

Personal dashboard that aggregates Jira tickets and GitHub PRs across an organization. Shows what needs your attention vs. what's waiting on others.

Built with .NET 10, Blazor Server, and a Bitwarden dark theme.

## Setup

1. Copy `.env.template` to `.env` and fill in your credentials:

   ```
   cp .env.template .env
   ```

   - **Jira API token**: generate at https://id.atlassian.com/manage-profile/security/api-tokens, uses scoped tokens with the following read permissions:
     - read:board-scope:jira-software
     - read:board-scope.admin:jira-software
     - read:dashboard:jira
     - read:dashboard.property:jira
     - read:project:jira
     - read:project.property:jira
     - read:project.feature:jira
     - read:project.component:jira
     - read:project-category:jira
     - read:sprint:jira-software
     - read:account
     - read:jira-user
     - read:me
     - read:jira-work
   - **GitHub PAT**: uses classic tokens with `repo` and `user` scope
     - Sadly there is an issue with fine-grained tokens not being reliable for search results

2. Build and run:

   ```
   dotnet run --project Taskwarden
   ```

3. Open https://localhost:7112
