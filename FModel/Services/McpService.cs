using System;
using System.Threading;
using System.Threading.Tasks;

using FModel.ViewModels;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.AspNetCore;

using Serilog;

namespace FModel.Services;

/// <summary>
/// Static entry point — keeps a single McpServerHandler alive for the lifetime of the process.
/// Referenced from ApplicationService via <c>McpService.McpServer</c>.
/// </summary>
public static class McpService
{
    public static McpServerHandler McpServer { get; } = new();
}

/// <summary>
/// Manages the ASP.NET Core host that serves the MCP HTTP endpoint embedded inside FModel.
/// Claude Desktop (or any MCP client) can connect to <c>http://localhost:{port}/sse</c>.
/// </summary>
public class McpServerHandler
{
    private WebApplication _app;
    private CancellationTokenSource _cts;

    public bool IsRunning { get; private set; }

    /// <summary>Set by <see cref="FModel.Views.SettingsView"/> after settings are saved.</summary>
    internal CUE4ParseViewModel CUE4Parse { get; private set; }

    public void SetProvider(CUE4ParseViewModel vm) => CUE4Parse = vm;

    public void Start(int port)
    {
        if (IsRunning) return;

        try
        {
            _cts = new CancellationTokenSource();

            var builder = WebApplication.CreateBuilder();

            // Silence ASP.NET Core's own console logging — FModel uses Serilog
            builder.Logging.ClearProviders();

            builder.WebHost.UseUrls($"http://localhost:{port}");

            // Register ourselves as a singleton so McpTools can inject it
            builder.Services.AddSingleton(this);

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithTools<FModelMcpTools>();

            _app = builder.Build();
            _app.MapMcp();

            // Run on a background thread so we don't block the WPF dispatcher
            Task.Run(() => _app.RunAsync(), _cts.Token);

            IsRunning = true;
            Log.Information("[MCP] Server started on http://localhost:{Port}/sse", port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MCP] Failed to start server");
            IsRunning = false;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;

        try
        {
            _cts?.Cancel();
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            _app?.StopAsync(stopCts.Token).GetAwaiter().GetResult();
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
}
