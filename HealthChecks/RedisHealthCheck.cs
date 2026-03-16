#nullable enable

using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace lycanthrope.HealthChecks;

public sealed class RedisHealthCheck(IConnectionMultiplexer connectionMultiplexer) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (!connectionMultiplexer.IsConnected)
        {
            return HealthCheckResult.Unhealthy("Redis connection is not established");
        }

        try
        {
            var latency = await connectionMultiplexer.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy(
                "Redis responded to ping",
                new Dictionary<string, object>
                {
                    ["latencyMs"] = Math.Round(latency.TotalMilliseconds, 2),
                }
            );
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis ping failed", ex);
        }
    }
}
