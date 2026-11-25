using Hokusai.Core;
using Hokusai.EndToEnd.Tests.Infrastructure;
using Hokusai.Engine;
using Hokusai.Runtime;
using Microsoft.Data.SqlClient;

namespace Hokusai.EndToEnd.Tests;

/// <summary>Legacy .NET Framework 4.8 app, classic ADO through COM, SQL Server LocalDB, captured with Extended Events.</summary>
internal sealed class LegacyAdoScenario : AppScenario
{
    private string _pipe = "";

    protected override string AppName => "LegacyInvoices";

    public override ProviderKind Provider => ProviderKind.SqlServer;

    public override async Task InitializeAsync()
    {
        var database = await LocalDb.CreateAsync(Repo.Seed("sqlserver.sql"));
        OnCleanup(() => LocalDb.DropAsync(database));
        _pipe = LocalDb.PipeName();
        Source = new DataSourceConfig { Provider = ProviderKind.SqlServer, ConnectionString = LocalDb.ConnectionString(database) };
    }

    // Classic SQLOLEDB predates LocalDB names, so the app connects through the instance's named pipe.
    protected override string AppArgument(string connectionString) =>
        $"Provider=SQLOLEDB;Data Source={_pipe};Initial Catalog={new SqlConnectionStringBuilder(connectionString).InitialCatalog};Integrated Security=SSPI";
}

/// <summary>Access database through ACE OLE DB, captured with row-level state diffs.</summary>
internal sealed class AccessScenario : AppScenario
{
    protected override string AppName => "AccessInvoices";

    public override ProviderKind Provider => ProviderKind.Access;

    public override bool CapturesReads => false;

    public override Task InitializeAsync()
    {
        var path = Path.Combine(Repo.NewTempDirectory("access"), "invoices.accdb");
        var (code, output) = Repo.Run(Repo.TestApp(AppName), $"--create \"{path}\" \"{Repo.Seed("access.sql")}\"");
        Assert.True(code == 0 && File.Exists(path), "Creating the Access database failed: " + output);
        var provider = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("Microsoft.ACE.OLEDB.16.0") is not null ? "Microsoft.ACE.OLEDB.16.0" : "Microsoft.ACE.OLEDB.12.0";
        Source = new DataSourceConfig { Provider = ProviderKind.Access, FilePath = path, ConnectionString = $"Provider={provider};Data Source={path}" };
        return Task.CompletedTask;
    }

    protected override string AppArgument(string connectionString) =>
        new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString }["Data Source"].ToString()!;
}

/// <summary>Modern WinForms app reaching SQLite through the SQLite ODBC driver, captured by the in-process agent.</summary>
internal sealed class OdbcSqliteScenario : AppScenario
{
    public const string Driver = "SQLite3 ODBC Driver";

    protected override string AppName => "ModernInvoices";

    public override ProviderKind Provider => ProviderKind.Odbc;

    public static bool DriverInstalled() => Doctor.OdbcDrivers().Any(d => d.Name == Driver && d.Bits == 64);

    public override async Task InitializeAsync()
    {
        var path = Path.Combine(Repo.NewTempDirectory("sqlite"), "invoices.db");
        await using (var seed = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await seed.OpenAsync();
            await using var cmd = seed.CreateCommand();
            cmd.CommandText = await File.ReadAllTextAsync(Repo.Seed("sqlite.sql"));
            await cmd.ExecuteNonQueryAsync();
        }

        Source = new DataSourceConfig { Provider = ProviderKind.Odbc, FilePath = path, ConnectionString = $"Driver={{{Driver}}};Database={path}" };
    }

    protected override string AppArgument(string connectionString) => connectionString;
}

public sealed class EndToEndTests
{
    [Fact(Explicit = true)]
    public async Task Legacy_ado_app_on_sql_server_localdb()
    {
        Assert.SkipUnless(LocalDb.IsAvailable(), "SQL Server LocalDB is not available.");
        await using var scenario = new LegacyAdoScenario();
        await scenario.InitializeAsync();
        await scenario.RunAsync();
    }

    [Fact(Explicit = true)]
    public async Task Access_app_through_ace_oledb()
    {
        Assert.SkipUnless(Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("Microsoft.ACE.OLEDB.16.0") is not null || Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("Microsoft.ACE.OLEDB.12.0") is not null,
            "The ACE OLE DB provider is not installed (it is not available on GitHub-hosted runners; run this test locally).");
        await using var scenario = new AccessScenario();
        await scenario.InitializeAsync();
        await scenario.RunAsync();
    }

    [Fact(Explicit = true)]
    public async Task Modern_app_through_odbc_on_sqlite()
    {
        Assert.SkipUnless(OdbcSqliteScenario.DriverInstalled(), $"The {OdbcSqliteScenario.Driver} (64-bit) is not installed.");
        await using var scenario = new OdbcSqliteScenario();
        await scenario.InitializeAsync();
        await scenario.RunAsync();
    }
}
