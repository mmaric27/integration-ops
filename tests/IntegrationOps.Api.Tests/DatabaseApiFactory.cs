using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IntegrationOps.Api.Processing;

namespace IntegrationOps.Api.Tests;

public sealed class DatabaseApiFactory(string connectionString, bool? processingEnabled = false, IIntegrationRunProcessor? processor = null, string environment = "Development") : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Minimal-host registration reads the flag before Build; supply it as bootstrap configuration.
        if (processingEnabled.HasValue)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Processing:Enabled"] = processingEnabled.Value.ToString() }));
        }
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?> { ["ConnectionStrings:IntegrationOps"] = connectionString };
            if (processingEnabled.HasValue) { settings["Processing:Enabled"] = processingEnabled.Value.ToString(); }
            configuration.AddInMemoryCollection(settings);
        });
        if (processor is not null)
        {
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton(processor)));
        }
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });
}
