using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace NpuBridge.Configuration;

/// <summary>Thrown for an unparseable or out-of-range setting; the message names the key and the offending value.</summary>
public sealed class BridgeConfigurationException : Exception
{
    public BridgeConfigurationException(string message) : base(message)
    {
    }

    public BridgeConfigurationException()
    {
    }

    public BridgeConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Hand-written binder so values like <c>on</c>/<c>off</c>, <c>phi-silica</c> and <c>compact</c> work from every
/// source, and so a bad value fails fast with a message that says which key to fix.
/// </summary>
public static class BridgeOptionsBinder
{
    public static BridgeOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var o = new BridgeOptions();

        if (Get(configuration, nameof(BridgeOptions.Backend)) is { } backend)
        {
            o.Backend = BackendKinds.TryParse(backend, out var kind)
                ? kind
                : throw Bad(nameof(BridgeOptions.Backend), backend, "expected phi-silica, aion or fake");
        }

        if (Get(configuration, nameof(BridgeOptions.Listen)) is { } listen)
        {
            o.Listen = ValidateListen(listen);
        }

        o.QueueCapacity = GetInt(configuration, nameof(BridgeOptions.QueueCapacity), o.QueueCapacity, min: 1, max: 1000);
        o.DrainWarningSeconds = GetInt(configuration, nameof(BridgeOptions.DrainWarningSeconds), o.DrainWarningSeconds, min: 1, max: 86_400);
        o.ContextCacheSize = GetInt(configuration, nameof(BridgeOptions.ContextCacheSize), o.ContextCacheSize, min: 0, max: 256);
        o.ContextWindowHint = GetInt(configuration, nameof(BridgeOptions.ContextWindowHint), o.ContextWindowHint, min: 256, max: 1_000_000);
        o.TruncateHistory = GetBool(configuration, nameof(BridgeOptions.TruncateHistory), o.TruncateHistory);
        o.ToolEmulation = GetBool(configuration, nameof(BridgeOptions.ToolEmulation), o.ToolEmulation);
        o.Verbose = GetBool(configuration, nameof(BridgeOptions.Verbose), o.Verbose);
        o.SelfRelaunch = GetBool(configuration, nameof(BridgeOptions.SelfRelaunch), o.SelfRelaunch);
        o.HideConsole = GetBool(configuration, nameof(BridgeOptions.HideConsole), o.HideConsole);
        o.InstallModel = GetBool(configuration, nameof(BridgeOptions.InstallModel), o.InstallModel);

        if (Get(configuration, nameof(BridgeOptions.SupervisorPid)) is { } supervisor)
        {
            o.SupervisorPid = GetInt(configuration, nameof(BridgeOptions.SupervisorPid), 0, min: 1, max: int.MaxValue);
        }

        if (Get(configuration, nameof(BridgeOptions.TaskName)) is { } taskName)
        {
            if (!IsValidServiceName(taskName))
            {
                throw Bad(nameof(BridgeOptions.TaskName), taskName, "must be 1-256 characters with no whitespace, slashes or quotes");
            }

            o.TaskName = taskName;
        }

        if (Get(configuration, nameof(BridgeOptions.SystemPromptPlacement)) is { } placement)
        {
            o.SystemPromptPlacement = placement.Trim().ToLowerInvariant() switch
            {
                "auto" => SystemPromptPlacement.Auto,
                "native" => SystemPromptPlacement.Native,
                "prompt" => SystemPromptPlacement.Prompt,
                _ => throw Bad(nameof(BridgeOptions.SystemPromptPlacement), placement, "expected auto, native or prompt"),
            };
        }

        if (Get(configuration, nameof(BridgeOptions.ToolSchema)) is { } schema)
        {
            o.ToolSchema = schema.Trim().ToLowerInvariant() switch
            {
                "compact" => ToolSchemaMode.Compact,
                "full" => ToolSchemaMode.Full,
                _ => throw Bad(nameof(BridgeOptions.ToolSchema), schema, "expected compact or full"),
            };
        }

        o.LafToken = Get(configuration, nameof(BridgeOptions.LafToken));
        o.LafAttestation = Get(configuration, nameof(BridgeOptions.LafAttestation));

        if (Get(configuration, nameof(BridgeOptions.ServiceName)) is { } serviceName)
        {
            if (!IsValidServiceName(serviceName))
            {
                throw Bad(nameof(BridgeOptions.ServiceName), serviceName, "must be 1-256 characters with no whitespace, slashes or quotes");
            }

            o.ServiceName = serviceName;
        }

        return o;
    }

    /// <summary>Service names are single tokens: no whitespace, path separators or quotes (sc.exe rules).</summary>
    public static bool IsValidServiceName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 256
        && !name.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '"');

    /// <summary>Parses <c>true/false</c>, <c>on/off</c>, <c>yes/no</c>, <c>1/0</c> (case-insensitive).</summary>
    public static bool TryParseBool(string? value, out bool result)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "true":
            case "on":
            case "yes":
            case "1":
                result = true;
                return true;
            case "false":
            case "off":
            case "no":
            case "0":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    /// <summary>
    /// Accepts Kestrel's binding forms: <c>http://127.0.0.1:5273</c>, <c>127.0.0.1:5273</c> (http assumed),
    /// and the wildcard hosts <c>*</c> / <c>+</c>. Returns the normalised, semicolon-joined list.
    /// </summary>
    public static string ValidateListen(string listen)
    {
        ArgumentNullException.ThrowIfNull(listen);
        var parts = listen.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw Bad(nameof(BridgeOptions.Listen), listen, "expected at least one URL such as http://127.0.0.1:5273");
        }

        var normalized = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var candidate = part.Contains("://", StringComparison.Ordinal) ? part : "http://" + part;

            // Uri cannot parse wildcard hosts; validate the rest of the URL with a placeholder host.
            var probe = candidate
                .Replace("://*:", "://wildcard:", StringComparison.Ordinal)
                .Replace("://+:", "://wildcard:", StringComparison.Ordinal);

            if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri)
                || (uri.Scheme != "http" && uri.Scheme != "https")
                || uri.IsDefaultPort && !candidate.Contains($":{uri.Port}", StringComparison.Ordinal))
            {
                throw Bad(nameof(BridgeOptions.Listen), part, "expected http[s]://host:port (host may be *, + or an IP)");
            }

            normalized.Add(candidate);
        }

        return string.Join(';', normalized);
    }

    private static string? Get(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int GetInt(IConfiguration configuration, string key, int fallback, int min, int max)
    {
        if (Get(configuration, key) is not { } raw)
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw Bad(key, raw, "expected an integer");
        }

        if (value < min || value > max)
        {
            throw Bad(key, raw, $"expected a value between {min} and {max}");
        }

        return value;
    }

    private static bool GetBool(IConfiguration configuration, string key, bool fallback)
    {
        if (Get(configuration, key) is not { } raw)
        {
            return fallback;
        }

        return TryParseBool(raw, out var value) ? value : throw Bad(key, raw, "expected on/off, true/false, yes/no or 1/0");
    }

    private static BridgeConfigurationException Bad(string key, string value, string expectation) =>
        new($"Invalid value '{value}' for setting '{key}': {expectation}.");
}
