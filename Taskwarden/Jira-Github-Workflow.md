# Workflow Summary
The following document summarizes the expected workflow of a developer on the Admin Console team at Bitwarden.

## Routine Feature Work
1. Jira tickets will be assigned at the start of the sprint
2. Developer moves the desired ticket to `In Progress`
   1. Developer creates the appropriate branch, always `ac/<jira ticket ID>/<small form description>` 
      1. e.x. `ac/pm-1234/fix-password-regression-on-confirm`
3. Developer may choose to push branch to GitHub in a draft PR state for early reviews
4. Upon completion of work, developer moves ticket to Code Review, publishes the branch, and opens the PR (or undrafts it)
5. If the code is **not** feature flagged, developer adds the `needs-qa` label to prevent accidental merging
6. Developer should nearly always add `ai-review` label to ask for AI reviews
7. Developer moves jira ticket to `Code Review`
8. Developer iterates on feedback from human and AI code reviews
9. Upon approval, developer marks Jira ticket as `Ready for QA`, and fills out regression testing information for the change
10. Developer waits for QA process
    1. QA moves ticket to `In QA`
    2. QA may assign ticket to themselves
    3. QA writes a comment in Jira indicating what testing was done, and if it was successful
    4. QA may reassign ticket back to developer
    5. If successful, QA moves ticket to `Ready for Merge`
11. If successful, developer may merge the PR
12. Developer moves Jira ticket to `Done`
13. :)

