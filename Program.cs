using lycanthrope.Components;
using lycanthrope.Configuration;
using lycanthrope.HealthChecks;
using lycanthrope.Interfaces;
using lycanthrope.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var redisConfiguration =
    builder.Configuration[$"{RedisOptions.SectionName}:Configuration"] ?? "localhost:6379";

builder.Services
    .AddOptions<RedisOptions>()
    .BindConfiguration(RedisOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Configuration),
        $"{RedisOptions.SectionName}:Configuration must be set."
    )
    .ValidateOnStart();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR().AddStackExchangeRedis(
    redisConfiguration,
    options => options.Configuration.ChannelPrefix = RedisChannel.Literal("lycanthrope")
);
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
});

builder.Services.AddSingleton<IConnectionMultiplexer>(serviceProvider =>
{
    var redisOptions = serviceProvider.GetRequiredService<IOptions<RedisOptions>>().Value;
    var configuration = ConfigurationOptions.Parse(redisOptions.Configuration, true);

    configuration.ClientName = builder.Environment.ApplicationName;
    configuration.AbortOnConnectFail = redisOptions.AbortOnConnectFail;
    configuration.AllowAdmin = redisOptions.AllowAdmin;
    configuration.ConnectRetry = redisOptions.ConnectRetry;
    configuration.ConnectTimeout = redisOptions.ConnectTimeoutMs;
    configuration.SyncTimeout = redisOptions.SyncTimeoutMs;

    return ConnectionMultiplexer.Connect(configuration);
});
builder.Services.AddScoped(serviceProvider =>
    serviceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase()
);

builder.Services.AddSingleton<ILobbyNotificationService, SignalRLobbyNotificationService>();
builder.Services.AddScoped<IGameEngineService, GameEngineService>();

var app = builder.Build();

// Fail fast on startup when Redis is unavailable or misconfigured.
_ = app.Services.GetRequiredService<IConnectionMultiplexer>();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("live"),
    }
);
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = _ => true,
    }
);

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHub<LobbyHub>("/lobbyhub");

app.Run();
