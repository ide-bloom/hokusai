using Hokusai.Capture.Odbc;
using Hokusai.Core;
using Hokusai.EndToEnd.Tests.Infrastructure;

namespace Hokusai.EndToEnd.Tests;

/// <summary>
/// Loads the native agent into 64- and 32-bit processes that use ODBC (Windows PowerShell with System.Data.Odbc and
/// the "SQL Server" driver that ships with Windows) and checks the captured statements, bound values and rows.
/// </summary>
public sealed class OdbcAgentTests
{
    public static TheoryData<string> PowerShells() => new()
    {
        Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe"),
        Environment.ExpandEnvironmentVariables(@"%WINDIR%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe"),
    };

    [Theory]
    [MemberData(nameof(PowerShells))]
    public void Agent_captures_statements_parameters_and_rows(string powershell)
    {
        Assert.SkipUnless(LocalDb.IsAvailable(), "SQL Server LocalDB is not available.");
        var agentDir = OdbcAgent.FindAgentDirectory();
        Assert.SkipWhen(agentDir is null, "The native agent has not been built.");

        var pipe = LocalDb.PipeName();
        var script = $$"""
            Start-Sleep -Seconds 2
            $c = New-Object System.Data.Odbc.OdbcConnection 'Driver={SQL Server};Server={{pipe}};Trusted_Connection=yes'
            $c.Open()
            $cmd = $c.CreateCommand()
            $cmd.CommandText = 'SELECT ? + 1 AS answer, ? AS who'
            [void]$cmd.Parameters.Add((New-Object System.Data.Odbc.OdbcParameter('a', [int]41)))
            [void]$cmd.Parameters.Add((New-Object System.Data.Odbc.OdbcParameter('b', 'hokusai')))
            $r = $cmd.ExecuteReader()
            while ($r.Read()) { [void]$r.GetValue(0); [void]$r.GetValue(1) }
            $r.Close()
            $c.Close()
            Start-Sleep -Seconds 5
            """;
        using var agent = OdbcAgent.Launch(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], agentDir!, TimeSpan.FromSeconds(20));
        Assert.Equal(0, agent.HookError);

        var captured = new List<StatementEvent>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !captured.Any(s => s.Result is { Rows.Count: > 0 }))
        {
            captured.AddRange(agent.Drain());
            Thread.Sleep(250);
        }

        var statement = Assert.Single(captured, s => s.Text.Contains("SELECT ? + 1", StringComparison.Ordinal));
        Assert.Equal(["41", "hokusai"], statement.Parameters.Select(p => p.Value));
        Assert.NotNull(statement.Result);
        Assert.Equal(["answer", "who"], statement.Result.Columns);
        Assert.Equal(["42", "hokusai"], statement.Result.Rows.Single());
        Assert.Equal(0, agent.LostEvents);
    }
}
