using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Corridor.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the whole Corridor stack once per test assembly: a SQL Server database with
/// the three db/sql scripts applied, plus the four .NET services as real child
/// processes (dotnet run) on their contract ports. The database is the compose db
/// (docker-compose.yml --profile ci) when it is already listening on localhost:1433,
/// otherwise the fixture starts its own Testcontainers SQL Server on a free port.
/// Every service receives ConnectionStrings__Corridor pointing at whichever endpoint
/// was chosen, so the suite never fights anything over 1433. Tear down kills the
/// process trees and disposes the fixture-owned container (never the compose db).
/// </summary>
public sealed partial class CorridorStackFixture : IAsyncLifetime
{
    public const int OktaPort = 8080;
    public const int AdfsPort = 8090;
    public const int LegacyPort = 8000;
    public const int PortalPort = 5200;
    public const int ComposeDbPort = 1433;

    public const string SaPassword = "CorridorDev1!";

    public Uri OktaBase { get; } = new($"http://localhost:{OktaPort}");
    public Uri AdfsBase { get; } = new($"http://localhost:{AdfsPort}");
    public Uri LegacyBase { get; } = new($"http://localhost:{LegacyPort}");
    public Uri PortalBase { get; } = new($"http://localhost:{PortalPort}");

    private MsSqlContainer? _container;
    private readonly List<(string Name, Process Process, string LogPath)> _processes = [];

    public string MasterConnectionString { get; private set; } = string.Empty;
    public string CorridorConnectionString { get; private set; } = string.Empty;
    public bool UsesComposeDatabase { get; private set; }

    public string RepoRoot { get; } = FindRepoRoot();

    public async Task InitializeAsync()
    {
        var used = PortsInUse([OktaPort, AdfsPort, LegacyPort, PortalPort]);
        if (used.Count > 0)
        {
            throw new InvalidOperationException(
                "Contract ports are already taken, another Corridor stack is probably still running: "
                + string.Join(", ", used) + ". Stop it before running the integration suite.");
        }

        var compose = await TryConnectToComposeDatabaseAsync();
        if (compose is not null)
        {
            (MasterConnectionString, CorridorConnectionString) = compose.Value;
            UsesComposeDatabase = true;
        }
        else
        {
            await StartOwnDatabaseAsync();
        }

        // The db/sql scripts are idempotent (IF NOT EXISTS guards) and are T-SQL
        // batches separated by GO lines. azure-sql-edge ships no sqlcmd binary, so
        // the fixture splits the batches itself and runs them over one ADO.NET
        // connection: the same effect as sqlcmd -i file.sql, against whichever
        // database endpoint was chosen.
        await ApplySqlScriptAsync(Path.Combine(RepoRoot, "db", "sql", "001_schemas.sql"));
        await ApplySqlScriptAsync(Path.Combine(RepoRoot, "db", "sql", "002_trace_procs.sql"));
        await ApplySqlScriptAsync(Path.Combine(RepoRoot, "db", "sql", "seed", "003_seed.sql"));

        await StartServiceAsync("Corridor.OktaSim", OktaPort);
        await StartServiceAsync("Corridor.AdfsSim", AdfsPort);
        await StartServiceAsync("Corridor.Legacy", LegacyPort);
        await StartServiceAsync("Corridor.Portal", PortalPort);
    }

    /// <summary>The compose db from docker-compose.yml --profile ci, when it is up on 1433.</summary>
    private async Task<(string Master, string Corridor)?> TryConnectToComposeDatabaseAsync()
    {
        var master = $"Server=localhost,{ComposeDbPort};Database=master;User Id=sa;Password={SaPassword};TrustServerCertificate=True;Connect Timeout=3";
        try
        {
            await using var probe = new SqlConnection(master);
            await probe.OpenAsync();
            return (master, $"Server=localhost,{ComposeDbPort};Database=Corridor;User Id=sa;Password={SaPassword};TrustServerCertificate=True");
        }
        catch (SqlException)
        {
            return null;
        }
    }

    private async Task StartOwnDatabaseAsync()
    {
        // azure-sql-edge ships no sqlcmd binary, so the default MsSql wait strategy
        // (which shells into the container for sqlcmd) cannot be used; wait for the
        // engine's own readiness log line instead.
        var builder = new MsSqlBuilder("mcr.microsoft.com/azure-sql-edge:latest")
            .WithPassword(SaPassword)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("SQL Server is now ready for client connections"));
        var dockerEndpoint = ResolveDockerEndpoint();
        if (dockerEndpoint is not null)
        {
            // Docker Desktop's stale /var/run/docker.sock beats colima's context in
            // Testcontainers' default discovery on this class of dev machine; pin the
            // live endpoint when the standard socket does not answer.
            builder = builder.WithDockerEndpoint(dockerEndpoint);
        }
        _container = builder.Build();
        await _container.StartAsync();

        var sqlPort = _container.GetMappedPublicPort(1433);
        MasterConnectionString =
            $"Server=localhost,{sqlPort};Database=master;User Id=sa;Password={SaPassword};TrustServerCertificate=True";
        CorridorConnectionString =
            $"Server=localhost,{sqlPort};Database=Corridor;User Id=sa;Password={SaPassword};TrustServerCertificate=True";
    }

    public async Task DisposeAsync()
    {
        foreach (var (name, process, _) in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
            }
            catch (Exception)
            {
                // Best effort only; the port sweep below is the safety net.
            }
            process.Dispose();
            _ = name;
        }
        _processes.Clear();

        await SweepLeftoverListenersAsync();

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
    });

    /// <summary>A client with a cookie jar, the shape the portal flows need.</summary>
    public HttpClient CreateCookieClient(CookieContainer cookies) => new(new HttpClientHandler
    {
        CookieContainer = cookies,
        AllowAutoRedirect = true,
    });

    private async Task StartServiceAsync(string projectName, int port)
    {
        var projectPath = Path.Combine(RepoRoot, "src", projectName);
        var logDirectory = Path.Combine(Path.GetTempPath(), "corridor-it-logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, $"{projectName}-{port}.log");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{projectPath}\" --no-launch-profile",
            WorkingDirectory = projectPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Plain `dotnet run` would honor launchSettings.json (including a browser
        // launch on two of the services); --no-launch-profile plus explicit
        // ASPNETCORE_URLS boots the same binaries on the same contract ports headless.
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["ASPNETCORE_URLS"] = $"http://localhost:{port}";
        psi.Environment["ConnectionStrings__Corridor"] = CorridorConnectionString;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start dotnet for {projectName}.");
        var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { log.WriteLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { log.WriteLine(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add((projectName, process, logPath));

        await WaitForHealthzAsync(new Uri($"http://localhost:{port}"), projectName, logPath);
    }

    private static async Task WaitForHealthzAsync(Uri baseAddress, string serviceName, string logPath)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        var lastError = "no attempt made";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync(new Uri(baseAddress, "/healthz"));
                var body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode && body.Contains("\"ok\"", StringComparison.Ordinal))
                {
                    return;
                }
                lastError = $"healthz answered HTTP {(int)response.StatusCode}: {body}";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(750));
        }
        var logTail = File.Exists(logPath)
            ? string.Join(Environment.NewLine, await File.ReadAllLinesAsync(logPath)).TruncateForLog()
            : "(no log file)";
        throw new TimeoutException(
            $"{serviceName} did not become healthy in time. Last error: {lastError}. Log tail:{Environment.NewLine}{logTail}");
    }

    /// <summary>sqlcmd-free script runner: splits on GO lines and executes the batches.</summary>
    private async Task ApplySqlScriptAsync(string scriptPath)
    {
        var text = await File.ReadAllTextAsync(scriptPath);
        var batches = GoSeparator().Split(text)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0)
            .ToList();

        await using var connection = new SqlConnection(MasterConnectionString);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            try
            {
                await connection.OpenAsync();
                break;
            }
            catch (SqlException) when (DateTime.UtcNow < deadline)
            {
                // The readiness log line can beat the TDS endpoint by a moment.
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
        foreach (var batch in batches)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static List<int> PortsInUse(int[] ports)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Select(e => e.Port)
            .ToHashSet();
        return ports.Where(p => listeners.Contains(p)).ToList();
    }

    /// <summary>
    /// Finds a Docker endpoint Testcontainers can actually talk to. Returns null when
    /// the default discovery (DOCKER_HOST or /var/run/docker.sock) answers, otherwise
    /// the colima socket path if one exists.
    /// </summary>
    private static string? ResolveDockerEndpoint()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return null;
        }
        if (UnixSocketResponds("/var/run/docker.sock"))
        {
            return null;
        }
        var colima = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".colima", "default", "docker.sock");
        return File.Exists(colima) ? "unix://" + colima : null;
    }

    private static bool UnixSocketResponds(string socketPath)
    {
        try
        {
            if (!File.Exists(socketPath))
            {
                return false;
            }
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            socket.Send("GET /_ping HTTP/1.0\r\nHost: localhost\r\n\r\n"u8);
            var buffer = new byte[64];
            var received = socket.Receive(buffer);
            var preamble = System.Text.Encoding.ASCII.GetString(buffer, 0, received);
            return preamble.StartsWith("HTTP/", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Safety net for the dispose path: on macOS find anything still listening on the
    /// contract ports and kill it. This only ever runs after the fixture's own services
    /// were asked to exit, so it cannot hit unrelated processes on this machine.
    /// </summary>
    private async Task SweepLeftoverListenersAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        foreach (var port in new[] { OktaPort, AdfsPort, LegacyPort, PortalPort })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/lsof",
                    Arguments = $"-ti tcp:{port}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var probe = Process.Start(psi);
                if (probe is null)
                {
                    continue;
                }
                var output = await probe.StandardOutput.ReadToEndAsync();
                await probe.WaitForExitAsync();
                foreach (var pid in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (int.TryParse(pid, out var numeric) && numeric != Environment.ProcessId)
                    {
                        try
                        {
                            Process.GetProcessById(numeric).Kill(true);
                        }
                        catch (Exception)
                        {
                            // Already gone or not ours; nothing more to do.
                        }
                    }
                }
            }
            catch (Exception)
            {
                // lsof is best effort only.
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var watched = new[] { OktaPort, AdfsPort, LegacyPort, PortalPort };
        while (DateTime.UtcNow < deadline && PortsInUse(watched).Count > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300));
        }
    }

    private static string FindRepoRoot()
    {
        var marker = Path.Combine("db", "sql", "001_schemas.sql");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, marker)))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException(
            $"Could not locate the repository root above {AppContext.BaseDirectory} (missing {marker}).");
    }

    [GeneratedRegex(@"^GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex GoSeparator();
}

internal static class StringLogExtensions
{
    public static string TruncateForLog(this string value)
        => value.Length <= 4000 ? value : value[^4000..];
}
