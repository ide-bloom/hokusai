using Hokusai.Core;
using Hokusai.Runtime;
using Microsoft.Data.Sqlite;

namespace Hokusai.Tests;

/// <summary>
/// Plays the part of a legacy app on a SQLite file: runs the app's statements, records their results and the
/// resulting state diff exactly as the recorder would, so inference and verification run against real data.
/// </summary>
internal sealed class SqliteApp : IDisposable
{
    public const string Seed = """
        CREATE TABLE Customer (CustomerId INTEGER PRIMARY KEY, Name TEXT NOT NULL, City TEXT, CreditLimit NUMERIC NOT NULL DEFAULT 0, CreatedAt TEXT DEFAULT (datetime('now')));
        CREATE TABLE Invoice (InvoiceId INTEGER PRIMARY KEY, CustomerId INTEGER NOT NULL REFERENCES Customer(CustomerId), Status TEXT NOT NULL, Taxable INTEGER NOT NULL, Total NUMERIC NOT NULL DEFAULT 0);
        CREATE TABLE InvoiceLine (LineId INTEGER PRIMARY KEY, InvoiceId INTEGER NOT NULL REFERENCES Invoice(InvoiceId), Description TEXT NOT NULL, Qty INTEGER NOT NULL, Price NUMERIC NOT NULL);
        CREATE TABLE InvoiceTax (InvoiceId INTEGER PRIMARY KEY REFERENCES Invoice(InvoiceId), Rate NUMERIC NOT NULL);
        INSERT INTO Customer (CustomerId, Name, City, CreditLimit, CreatedAt) VALUES (1, 'Acme Ltd', 'Graz', 1000, '2026-01-01 00:00:00'), (2, 'Globex', 'Linz', 500, '2026-01-02 00:00:00'), (3, 'Initech', NULL, 250, '2026-01-03 00:00:00');
        INSERT INTO Invoice (InvoiceId, CustomerId, Status, Taxable, Total) VALUES (10, 1, 'Draft', 1, 120), (11, 2, 'Sent', 0, 80.5);
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hokusai-tests", Guid.NewGuid().ToString("N"));

    public SqliteApp()
    {
        Directory.CreateDirectory(_dir);
        SeedPath = Path.Combine(_dir, "seed.db");
        using var c = new SqliteConnection($"Data Source={SeedPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Seed;
        cmd.ExecuteNonQuery();
    }

    public string SeedPath { get; }

    /// <summary>A fresh copy of the seed, optionally changed first so identity values differ from the demonstrations.</summary>
    public string Copy(string? prepare = null)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        File.Copy(SeedPath, path);
        if (prepare is not null)
        {
            using var c = new SqliteConnection(ConnectionString(path));
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = prepare;
            cmd.ExecuteNonQuery();
        }

        return path;
    }

    public static string ConnectionString(string path) => $"Data Source={path};Pooling=False";

    /// <summary>Runs the demonstration's statements on a fresh copy and fills in results and the state diff.</summary>
    public async Task<Demonstration> RecordAsync(Demonstration demo, string? prepare = null)
    {
        var path = Copy(prepare);
        await using var c = new SqliteConnection(ConnectionString(path));
        await c.OpenAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        var schema = await SchemaReader.ReadAsync(c, ProviderKind.Sqlite, ct);
        var tables = schema.Tables.Select(t => t.Name).ToList();
        var before = await StateDiffer.SnapshotAsync(c, null, ProviderKind.Sqlite, schema, tables, ct);
        ResultData? last = null;
        foreach (var e in demo.Statements)
        {
            foreach (var p in e.Parameters.Where(p => p.Value?.StartsWith('=') == true))
            {
                p.Value = last?.Value(0, p.Value![1..]);
            }

            await using var cmd = c.CreateCommand();
            cmd.CommandText = e.Text;
            foreach (var p in e.Parameters)
            {
                cmd.Parameters.AddWithValue("@" + p.Name, (object?)p.Value ?? DBNull.Value);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (reader.FieldCount > 0)
            {
                var result = new ResultData();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    result.Columns.Add(reader.GetName(i));
                }

                while (await reader.ReadAsync(ct))
                {
                    result.Rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : HkValue.Canon(reader.GetValue(i)))]);
                }

                e.Result = result;
                last = result;
            }
        }

        var after = await StateDiffer.SnapshotAsync(c, null, ProviderKind.Sqlite, schema, tables, ct);
        demo.StateDiff = StateDiffer.Diff(before, after);
        demo.Tables = tables;
        return demo;
    }

    public async Task<SchemaInfo> SchemaAsync()
    {
        await using var c = new SqliteConnection(ConnectionString(SeedPath));
        await c.OpenAsync(TestContext.Current.CancellationToken);
        return await SchemaReader.ReadAsync(c, ProviderKind.Sqlite, TestContext.Current.CancellationToken);
    }

    public async Task<DatabaseSnapshot> SeedSnapshotAsync()
    {
        await using var c = new SqliteConnection(ConnectionString(SeedPath));
        await c.OpenAsync(TestContext.Current.CancellationToken);
        var schema = await SchemaReader.ReadAsync(c, ProviderKind.Sqlite, TestContext.Current.CancellationToken);
        return await StateDiffer.SnapshotAsync(c, null, ProviderKind.Sqlite, schema, schema.Tables.Select(t => t.Name), TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Temp files are cleaned up by the OS eventually; a locked file must not fail the test.
        }
    }
}
