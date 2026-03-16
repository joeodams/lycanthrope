using System.ComponentModel.DataAnnotations;

namespace lycanthrope.Configuration;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    [Required(AllowEmptyStrings = false)]
    public string Configuration { get; set; } = "localhost:6379";

    public bool AbortOnConnectFail { get; set; }

    public bool AllowAdmin { get; set; }

    [Range(0, 10)]
    public int ConnectRetry { get; set; } = 3;

    [Range(100, 30000)]
    public int ConnectTimeoutMs { get; set; } = 5000;

    [Range(100, 30000)]
    public int SyncTimeoutMs { get; set; } = 5000;
}
