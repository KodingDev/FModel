using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using FModel.ViewModels;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.AspNetCore;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Serilog;

namespace FModel.Services;

public static class McpService
{
    public static McpServerHandler McpServer { get; } = new();
}

public class McpServerHandler
{
    private WebApplication _app;
    private CancellationTokenSource _cts;

    public bool IsRunning { get; private set; }

    internal CUE4ParseViewModel CUE4Parse { get; private set; }

    public void SetProvider(CUE4ParseViewModel vm) => CUE4Parse = vm;

    public void Start(int port)
    {
        if (IsRunning) return;

        try
        {
            _cts = new CancellationTokenSource();

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://localhost:{port}");
            builder.Services.AddSingleton(this);
            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithTools<FModelMcpTools>();

            _app = builder.Build();
            _app.MapMcp();

            Task.Run(() => _app.RunAsync(), _cts.Token);

            IsRunning = true;
            Log.Information("[MCP] Server started on http://localhost:{Port}/sse", port);
            EnsureClaudeCodeRegistration(port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MCP] Failed to start server");
            IsRunning = false;
        }
    }

    public static void EnsureClaudeCodeRegistration(int port)
    {
        var url = $"http://localhost:{port}/sse";
        var expected = new JObject { ["type"] = "sse", ["url"] = url };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        RegisterInFile(Path.Combine(home, ".claude.json"), expected);
        RegisterInFile(Path.Combine(home, ".claude", "settings.json"), expected);
    }

    private static void RegisterInFile(string path, JObject expected)
    {
        try
        {
            JObject root;
            if (File.Exists(path))
                root = JObject.Parse(File.ReadAllText(path));
            else
                return;

            var servers = root["mcpServers"] as JObject;
            if (servers == null)
            {
                servers = new JObject();
                root["mcpServers"] = servers;
            }

            var existing = servers["fmodel"] as JObject;
            if (existing != null && JToken.DeepEquals(existing, expected))
                return;

            servers["fmodel"] = expected.DeepClone();
            File.WriteAllText(path, root.ToString(Formatting.Indented));
            Log.Information("[MCP] Registered fmodel in {Path}", path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MCP] Could not register in {Path}", path);
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        try
        {
            _cts?.Cancel();
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (_app != null)
                await _app.StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MCP] Error stopping server");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _app = null;
            IsRunning = false;
            Log.Information("[MCP] Server stopped");
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        Task.Run(() => StopAsync()).GetAwaiter().GetResult();
    }
}
