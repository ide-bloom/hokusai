using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hokusai.Runtime;
using ModelContextProtocol.Client;

namespace Hokusai.EndToEnd.Tests.Infrastructure;

/// <summary>A generated solution on disk: builds it, runs its tests, and starts its API and MCP server.</summary>
internal sealed class GeneratedSolution(string directory, string name)
{
    public string Directory { get; } = directory;

    public string Name { get; } = name;

    public void BuildAndTest()
    {
        var build = Repo.Run("dotnet", "build -c Release", Directory, TimeSpan.FromMinutes(10));
        Assert.True(build.ExitCode == 0, "Generated solution failed to build:\n" + Tail(build.Output));
        var test = Repo.Run("dotnet", "test -c Release --no-build", Directory, TimeSpan.FromMinutes(15));
        Assert.True(test.ExitCode == 0, "Generated regression tests failed:\n" + Tail(test.Output));
    }

    private static string Tail(string text) => text.Length > 6000 ? text[^6000..] : text;

    private string Dll(string project) => Path.Combine(Directory, "src", $"{Name}.{project}", "bin", "Release", "net10.0", $"{Name}.{project}.dll");

    public static Dictionary<string, string> Environment(ProviderKind provider, string connectionString, string work) => new()
    {
        ["ConnectionStrings__Hokusai"] = connectionString,
        ["Hokusai__Provider"] = provider.ToString(),
        ["Hokusai__Audit__Path"] = Path.Combine(work, "audit.jsonl"),
        ["Hokusai__Idempotency__Path"] = Path.Combine(work, "idempotency.jsonl"),
        ["DOTNET_ENVIRONMENT"] = "Production",
    };

    public async Task<RunningApi> StartApiAsync(ProviderKind provider, string connectionString)
    {
        var work = Repo.NewTempDirectory("api");
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var port = FreePort();
        var info = new ProcessStartInfo("dotnet", $"\"{Dll("Api")}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = work };
        foreach (var (k, v) in Environment(provider, connectionString, work))
        {
            info.Environment[k] = v;
        }

        info.Environment["Hokusai__Auth__ApiKeyHashes__0"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        info.Environment["Urls"] = $"http://127.0.0.1:{port}";
        var process = Process.Start(info)!;
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => output.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => output.AppendLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException("The generated API exited: " + output);
            }

            try
            {
                using var r = await client.GetAsync("/openapi/v1.json");
                if (r.IsSuccessStatusCode)
                {
                    return new RunningApi(process, client);
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(300);
        }

        process.Kill(entireProcessTree: true);
        throw new TimeoutException("The generated API did not start: " + output);
    }

    public async Task<McpClient> StartMcpAsync(ProviderKind provider, string connectionString)
    {
        var work = Repo.NewTempDirectory("mcp");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = Name,
            Command = "dotnet",
            Arguments = [Dll("Mcp")],
            WorkingDirectory = work,
            EnvironmentVariables = Environment(provider, connectionString, work)!,
        });
        return await McpClient.CreateAsync(transport);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed class RunningApi(Process process, HttpClient client) : IDisposable
{
    public async Task<JsonDocument> PostAsync(string route, object input, bool dryRun = false, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/actions/{route}{(dryRun ? "?dryRun=true" : "")}") { Content = JsonContent.Create(input) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"POST /actions/{route}: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }

    public async Task<HttpStatusCode> PostStatusAsync(string route, object input)
    {
        using var response = await client.PostAsync($"/actions/{route}", JsonContent.Create(input));
        return response.StatusCode;
    }

    public void Dispose()
    {
        client.Dispose();
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        process.Dispose();
    }
}
