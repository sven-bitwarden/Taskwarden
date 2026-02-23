using Microsoft.Extensions.Options;
using Taskwarden.Configuration;
using Taskwarden.Services;
using Taskwarden.State;

namespace Taskwarden.Background;

public class RefreshHostedService(
    IServiceScopeFactory scopeFactory,
    DashboardStateContainer stateContainer,
    IOptions<TaskWardenOptions> options,
    ILogger<RefreshHostedService> logger)
    : BackgroundService
{
    private readonly TaskWardenOptions _options = options.Value;
    private readonly ManualResetEventSlim _manualRefresh = new(false);
    private bool _userInfoFetched;

    public void RequestManualRefresh()
    {
        logger.LogInformation("Manual refresh requested");
        _manualRefresh.Set();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let the app start up
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(stoppingToken);

            // Wait for either the interval to elapse or a manual refresh
            var interval = TimeSpan.FromMinutes(_options.RefreshIntervalMinutes);
            _manualRefresh.Reset();

            try
            {
                await Task.Run(() => _manualRefresh.Wait(interval, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting dashboard refresh");
        stateContainer.SetLoading();

        var progress = new Progress<string>(msg => stateContainer.AddProgress(msg));

        try
        {
            using var scope = scopeFactory.CreateScope();

            var jiraService = scope.ServiceProvider.GetRequiredService<IJiraService>();

            if (!_userInfoFetched)
            {
                try
                {
                    stateContainer.AddProgress("Authenticating with Jira and GitHub");
                    var ghService = scope.ServiceProvider.GetRequiredService<IGitHubService>();
                    var ghLogin = await ghService.GetCurrentUserLoginAsync();
                    var jiraName = await jiraService.GetCurrentUserDisplayNameAsync(cancellationToken);
                    stateContainer.SetUserInfo(ghLogin, jiraName);
                    _userInfoFetched = true;
                }
                catch (Exception ex)
                {
                    stateContainer.AddProgress("Warning: failed to fetch user info");
                    logger.LogWarning(ex, "Failed to fetch user info");
                }
            }

            // Refresh sprint every cycle so it stays current across sprint boundaries
            try
            {
                stateContainer.AddProgress("Fetching active sprint");
                var activeSprint = await jiraService.GetActiveSprintAsync(cancellationToken);
                stateContainer.SetActiveSprint(activeSprint);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh active sprint");
            }

            var aggregator = scope.ServiceProvider.GetRequiredService<IWorkItemAggregator>();
            var workItems = await aggregator.AggregateAsync(progress, cancellationToken);
            stateContainer.AddProgress($"Done — {workItems.Count} work items loaded");
            stateContainer.SetData(workItems);
            logger.LogInformation("Dashboard refresh complete: {Count} work items", workItems.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stateContainer.AddProgress($"Error: {ex.Message}");
            logger.LogError(ex, "Dashboard refresh failed");
            stateContainer.SetError(ex.Message);
        }
    }
}
