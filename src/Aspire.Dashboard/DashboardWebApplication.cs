// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Dashboard.Components;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Grpc;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Fast.Components.FluentUI;

namespace Aspire.Dashboard;

public class DashboardWebApplication : IHostedService
{
    private const string DashboardOtlpUrlVariableName = "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL";
    private const string DashboardOtlpUrlDefaultValue = "http://localhost:18889";
    private const string DashboardUrlVariableName = "DOTNET_DASHBOARD_URL";
    private const string DashboardUrlDefaultValue = "http://localhost:18888";

    private readonly WebApplication _app;

    public DashboardWebApplication(Action<IServiceCollection> configureServices)
    {
        var builder = WebApplication.CreateBuilder();

        var dashboardUris = GetAddressUris(DashboardUrlVariableName, DashboardUrlDefaultValue);
        var dashboardHttpsPort = dashboardUris.FirstOrDefault(IsHttps)?.Port;
        var otlpUris = GetAddressUris(DashboardOtlpUrlVariableName, DashboardOtlpUrlDefaultValue);

        if (otlpUris.Length > 1)
        {
            throw new InvalidOperationException("Only one URL for Aspire dashboard OTLP endpoint is supported.");
        }

        builder.WebHost.ConfigureKestrel(kestrelOptions =>
        {
            ConfigureListenAddresses(kestrelOptions, dashboardUris);
            ConfigureListenAddresses(kestrelOptions, otlpUris, HttpProtocols.Http2);
        });

        if (!builder.Environment.IsDevelopment())
        {
            // This is set up automatically by the DefaultBuilder when IsDevelopment is true
            // But since this gets packaged up and used in another app, we need it to look for
            // static assets on disk as if it were at development time even when it is not
            builder.WebHost.UseStaticWebAssets();
        }

        if (dashboardHttpsPort is not null)
        {
            // Explicitly configure the HTTPS redirect port as we're possibly listening on multiple HTTPS addresses
            // if the dashboard OTLP URL is configured to use HTTPS too
            builder.Services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = dashboardHttpsPort);
        }

        // Add services to the container.
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        // OTLP services.
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<TelemetryRepository>();
        builder.Services.AddTransient<StructuredLogsViewModel>();
        builder.Services.AddTransient<TracesViewModel>();

        builder.Services.AddFluentUIComponents(options =>
        {
            options.HostingModel = BlazorHostingModel.Server;
        });

        configureServices(builder.Services);

        builder.Services.AddScoped<EnvironmentVariablesDialogService>();

        _app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!_app.Environment.IsDevelopment())
        {
            _app.UseExceptionHandler("/Error");
        }

        if (dashboardHttpsPort is not null)
        {
            _app.UseHttpsRedirection();
        }

        _app.UseStaticFiles(new StaticFileOptions()
        {
            OnPrepareResponse = (context) =>
            {
                // If Cache-Control isn't already set to something, set it to 'no-cache' so that the 
                // ETag and Last-Modified headers will be respected by the browser.
                // This may be able to be removed if https://github.com/dotnet/aspnetcore/issues/44153
                // is fixed to make this the default
                if (context.Context.Response.Headers.CacheControl.Count == 0)
                {
                    context.Context.Response.Headers.CacheControl = "no-cache";
                }
            }
        });

        _app.UseAuthorization();

        _app.UseAntiforgery();

        _app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        // OTLP gRPC services.
        _app.MapGrpcService<OtlpMetricsService>();
        _app.MapGrpcService<OtlpTraceService>();
        _app.MapGrpcService<OtlpLogsService>();
    }

    private static Uri[] GetAddressUris(string variableName, string defaultValue)
    {
        var urls = Environment.GetEnvironmentVariable(variableName) ?? defaultValue;
        try
        {
            return urls.Split(';').Select(url => new Uri(url)).ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error parsing URIs from environment variable '{variableName}'.", ex);
        }
    }

    private static void ConfigureListenAddresses(KestrelServerOptions kestrelOptions, Uri[] uris, HttpProtocols? httpProtocols = null)
    {
        foreach (var uri in uris)
        {
            if (uri.IsLoopback)
            {
                kestrelOptions.ListenLocalhost(uri.Port, options =>
                {
                    ConfigureListenOptions(options, uri, httpProtocols);
                });
            }
            else
            {
                kestrelOptions.Listen(IPAddress.Parse(uri.Host), uri.Port, options =>
                {
                    ConfigureListenOptions(options, uri, httpProtocols);
                });
            }
        }

        static void ConfigureListenOptions(ListenOptions options, Uri uri, HttpProtocols? httpProtocols)
        {
            if (IsHttps(uri))
            {
                options.UseHttps();
            }
            if (httpProtocols is not null)
            {
                options.Protocols = httpProtocols.Value;
            }
        }
    }

    private static bool IsHttps(Uri uri) => string.Equals(uri.Scheme, "https", StringComparison.Ordinal);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _app.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _app.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
