using Taskwarden.Background;
using Taskwarden.Components;
using Taskwarden.Configuration;
using Taskwarden.Services;
using Taskwarden.State;

var builder = WebApplication.CreateBuilder(args);

// Load .env file into configuration (keys use __ as section separator, e.g. Jira__ApiToken)
var envPath = Path.Combine(builder.Environment.ContentRootPath, "..", ".env");
if (File.Exists(envPath))
{
    var envVars = new Dictionary<string, string?>();
    foreach (var line in File.ReadAllLines(envPath))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            continue;
        var eq = trimmed.IndexOf('=');
        if (eq <= 0)
            continue;
        var key = trimmed[..eq].Trim();
        var value = trimmed[(eq + 1)..].Trim();
        envVars[key.Replace("__", ":")] = value;
    }
    builder.Configuration.AddInMemoryCollection(envVars);
}

// Configuration
builder.Services.Configure<JiraOptions>(builder.Configuration.GetSection(JiraOptions.SectionName));
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection(GitHubOptions.SectionName));
builder.Services.Configure<TaskWardenOptions>(builder.Configuration.GetSection(TaskWardenOptions.SectionName));

// Services
builder.Services.AddHttpClient<IJiraService, JiraService>();
builder.Services.AddSingleton<IGitHubService, GitHubService>();
builder.Services.AddTransient<IWorkItemAggregator, WorkItemAggregator>();

// State
builder.Services.AddSingleton<DashboardStateContainer>();

// Background refresh
builder.Services.AddSingleton<RefreshHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RefreshHostedService>());

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
