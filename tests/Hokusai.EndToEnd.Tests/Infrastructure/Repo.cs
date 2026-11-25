using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace Hokusai.EndToEnd.Tests.Infrastructure;

/// <summary>Locations of the repository, the test apps and the seed scripts.</summary>
internal static class Repo
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "hokusai.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static string Seed(string name) => Path.Combine(Root, "testapps", "seed", name);

    /// <summary>The newest build of a test app executable.</summary>
    public static string TestApp(string name) =>
        Directory.EnumerateFiles(Path.Combine(Root, "testapps", name, "bin"), name + ".exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        ?? throw new FileNotFoundException($"{name}.exe has not been built.");

    public static string NewTempDirectory(string purpose)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hokusai-e2e", purpose + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static (int ExitCode, string Output) Run(string file, string arguments, string? workingDirectory = null, TimeSpan? timeout = null, IDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Root,
        };
        foreach (var (k, v) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[k] = v;
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{file} {arguments} timed out.");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}

/// <summary>Creates and drops LocalDB databases for the legacy app.</summary>
internal static class LocalDb
{
    public const string Server = @"(localdb)\MSSQLLocalDB";

    public static string ConnectionString(string database) =>
        new SqlConnectionStringBuilder { DataSource = Server, InitialCatalog = database, IntegratedSecurity = true, TrustServerCertificate = true, ConnectTimeout = 60, CommandTimeout = 120 }.ConnectionString;

    public static bool IsAvailable()
    {
        try
        {
            using var c = new SqlConnection(ConnectionString("master"));
            c.Open();
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    public static async Task<string> CreateAsync(string seedScript)
    {
        var name = "hokusai_e2e_" + Guid.NewGuid().ToString("N")[..10];
        var folder = Repo.NewTempDirectory("localdb");
        await using (var master = new SqlConnection(ConnectionString("master")))
        {
            await master.OpenAsync();
            await using var cmd = master.CreateCommand();
            cmd.CommandTimeout = 300;
            cmd.CommandText = $"CREATE DATABASE [{name}] ON (NAME = N'{name}', FILENAME = N'{Path.Combine(folder, name + ".mdf")}')";
            await cmd.ExecuteNonQueryAsync();
        }

        await Hokusai.Verify.DatabaseCopies.RunScriptAsync(Hokusai.Runtime.ProviderKind.SqlServer, ConnectionString(name), await File.ReadAllTextAsync(seedScript), CancellationToken.None);
        return name;
    }

    public static async Task DropAsync(string name) =>
        await Hokusai.Runtime.HkCopies.DropSqlServerAsync(ConnectionString("master"), name, null);

    /// <summary>The instance's named pipe; classic SQLOLEDB cannot resolve "(localdb)" names itself.</summary>
    public static string PipeName()
    {
        var exe = Directory.EnumerateFiles(@"C:\Program Files\Microsoft SQL Server", "SqlLocalDB.exe", SearchOption.AllDirectories).OrderDescending().First();
        Repo.Run(exe, "start MSSQLLocalDB");
        var info = Repo.Run(exe, "info MSSQLLocalDB").Output;
        var line = info.Split('\n').First(l => l.Contains("pipe", StringComparison.OrdinalIgnoreCase));
        return line[(line.IndexOf("np:", StringComparison.Ordinal))..].Trim();
    }
}
