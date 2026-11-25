using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hokusai.Runtime;

namespace Hokusai.Core;

public static class HkJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, ct).ConfigureAwait(false);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"{path} is empty.");
    }
}

/// <summary>The data source Hokusai observes. The connection string itself lives in the DPAPI-protected secret store.</summary>
public sealed class DataSourceConfig
{
    public ProviderKind Provider { get; set; }

    /// <summary>Connection string without secrets, for display.</summary>
    public string Display { get; set; } = "";

    /// <summary>Database file for SQLite and Access sources (copied for baselines and verification).</summary>
    public string? FilePath { get; set; }

    [JsonIgnore]
    public string ConnectionString { get; set; } = "";

    /// <summary>The same connection pointed at another database file or catalog.</summary>
    public string ConnectionStringFor(string fileOrCatalog)
    {
        if (Provider == ProviderKind.SqlServer)
        {
            var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = fileOrCatalog };
            return b.ConnectionString;
        }

        if (FilePath is null)
        {
            throw new InvalidOperationException("This data source has no database file to redirect.");
        }

        var index = ConnectionString.IndexOf(FilePath, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            throw new InvalidOperationException("The connection string does not mention the configured database file.");
        }

        return string.Concat(ConnectionString.AsSpan(0, index), fileOrCatalog, ConnectionString.AsSpan(index + FilePath.Length));
    }
}

public sealed class WorkspaceConfig
{
    public DataSourceConfig? DataSource { get; set; }

    /// <summary>Extra cultures tried when correlating typed numbers and dates, after the app's own culture.</summary>
    public List<string> Cultures { get; set; } = ["en-US", "de-DE", "fr-FR", "en-GB"];

    /// <summary>Folders whose structured text files are snapshotted so file writes can be diffed.</summary>
    public List<string> WatchFolders { get; set; } = [];
}

/// <summary>
/// Stores connection strings encrypted with DPAPI for the current Windows user. Secrets never appear in
/// hokusai.json, recordings or generated projects.
/// </summary>
public static class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("hokusai.credential.v1");

    public static string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Credential storage uses DPAPI and requires Windows.");
        }

        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string protectedSecret)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Credential storage uses DPAPI and requires Windows.");
        }

        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
    }
}

/// <summary>
/// Layout of a Hokusai workspace:
/// <c>.hokusai/hokusai.json</c>, <c>.hokusai/secrets.json</c> (DPAPI), and
/// <c>recordings/&lt;action&gt;/demo-NNN/</c> with <c>template.json</c> and <c>verification.json</c> per action.
/// </summary>
public sealed class Workspace(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string ConfigDirectory => Path.Combine(Root, ".hokusai");
    public string ConfigPath => Path.Combine(ConfigDirectory, "hokusai.json");
    public string SecretsPath => Path.Combine(ConfigDirectory, "secrets.json");
    public string RecordingsDirectory => Path.Combine(Root, "recordings");

    public static string SafeName(string action)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. action.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : char.ToLowerInvariant(c))]);
        return cleaned.Length == 0 ? throw new ArgumentException("Action name is empty.", nameof(action)) : cleaned;
    }

    public string ActionDirectory(string action) => Path.Combine(RecordingsDirectory, SafeName(action));
    public string TemplatePath(string action) => Path.Combine(ActionDirectory(action), "template.json");
    public string VerificationPath(string action) => Path.Combine(ActionDirectory(action), "verification.json");

    public string GuardsPath(string action) => Path.Combine(ActionDirectory(action), "guards.json");

    public async Task<Guards> LoadGuardsAsync(string action, CancellationToken ct) =>
        File.Exists(GuardsPath(action)) ? await HkJson.ReadAsync<Guards>(GuardsPath(action), ct).ConfigureAwait(false) : new Guards();

    public Task SaveGuardsAsync(string action, Guards guards, CancellationToken ct) => HkJson.WriteAsync(GuardsPath(action), guards, ct);

    public string DemonstrationDirectory(string action, int index) =>
        Path.Combine(ActionDirectory(action), "demo-" + index.ToString("000", System.Globalization.CultureInfo.InvariantCulture));

    public async Task<WorkspaceConfig> LoadConfigAsync(CancellationToken ct)
    {
        if (!File.Exists(ConfigPath))
        {
            return new WorkspaceConfig();
        }

        var config = await HkJson.ReadAsync<WorkspaceConfig>(ConfigPath, ct).ConfigureAwait(false);
        if (config.DataSource is not null && File.Exists(SecretsPath))
        {
            var secrets = await HkJson.ReadAsync<Dictionary<string, string>>(SecretsPath, ct).ConfigureAwait(false);
            if (secrets.TryGetValue("dataSource", out var p))
            {
                config.DataSource.ConnectionString = CredentialStore.Unprotect(p);
            }
        }

        return config;
    }

    public async Task SaveConfigAsync(WorkspaceConfig config, CancellationToken ct)
    {
        await HkJson.WriteAsync(ConfigPath, config, ct).ConfigureAwait(false);
        if (config.DataSource is { ConnectionString.Length: > 0 } ds)
        {
            await HkJson.WriteAsync(SecretsPath, new Dictionary<string, string> { ["dataSource"] = CredentialStore.Protect(ds.ConnectionString) }, ct).ConfigureAwait(false);
        }
    }

    public IEnumerable<string> Actions() =>
        Directory.Exists(RecordingsDirectory)
            ? Directory.EnumerateDirectories(RecordingsDirectory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)
            : [];

    public async Task<List<Demonstration>> LoadDemonstrationsAsync(string action, CancellationToken ct)
    {
        var list = new List<Demonstration>();
        var dir = ActionDirectory(action);
        if (!Directory.Exists(dir))
        {
            return list;
        }

        foreach (var demoDir in Directory.EnumerateDirectories(dir, "demo-*").Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(demoDir, "demonstration.json");
            if (File.Exists(file))
            {
                list.Add(await HkJson.ReadAsync<Demonstration>(file, ct).ConfigureAwait(false));
            }
        }

        return list;
    }

    public Task SaveDemonstrationAsync(Demonstration demo, CancellationToken ct) =>
        HkJson.WriteAsync(Path.Combine(DemonstrationDirectory(demo.Action, demo.Index), "demonstration.json"), demo, ct);

    public int NextDemonstrationIndex(string action)
    {
        var dir = ActionDirectory(action);
        return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir, "demo-*").Count() + 1 : 1;
    }

    public async Task<ActionTemplate?> LoadTemplateAsync(string action, CancellationToken ct) =>
        File.Exists(TemplatePath(action)) ? await HkJson.ReadAsync<ActionTemplate>(TemplatePath(action), ct).ConfigureAwait(false) : null;

    public Task SaveTemplateAsync(ActionTemplate template, CancellationToken ct) => HkJson.WriteAsync(TemplatePath(template.Name), template, ct);

    public async Task<VerificationReport?> LoadVerificationAsync(string action, CancellationToken ct) =>
        File.Exists(VerificationPath(action)) ? await HkJson.ReadAsync<VerificationReport>(VerificationPath(action), ct).ConfigureAwait(false) : null;
}
