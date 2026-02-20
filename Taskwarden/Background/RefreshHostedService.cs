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

        try
        {
            using var scope = scopeFactory.CreateScope();

            if (!_userInfoFetched)
            {
                try
                {
                    var ghService = scope.ServiceProvider.GetRequiredService<IGitHubService>();
                    var jiraService = scope.ServiceProvider.GetRequiredService<IJiraService>();
                    var ghLogin = await ghService.GetCurrentUserLoginAsync();
                    var jiraName = await jiraService.GetCurrentUserDisplayNameAsync(cancellationToken);
                    var activeSprint = await jiraService.GetActiveSprintAsync(cancellationToken);
                    stateContainer.SetUserInfo(ghLogin, jiraName, activeSprint);
                    _userInfoFetched = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to fetch user info");
                }
            }

            var aggregator = scope.ServiceProvider.GetRequiredService<IWorkItemAggregator>();
            var workItems = await aggregator.AggregateAsync(cancellationToken);
            stateContainer.SetData(workItems);
            logger.LogInformation("Dashboard refresh complete: {Count} work items", workItems.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Dashboard refresh failed");
            stateContainer.SetError(ex.Message);
        }
    }
}
