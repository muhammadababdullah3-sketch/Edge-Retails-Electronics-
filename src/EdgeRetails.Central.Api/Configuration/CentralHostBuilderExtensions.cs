using EdgeRetails.Central.Api.Services;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.Api.Configuration;

internal static class CentralHostBuilderExtensions
{
    public static void AddCentralOutboxDispatch(this IServiceCollection services)
    {
        services.AddSingleton(_ => CentralOutboxOptions.FromEnvironment());
        services.AddSingleton<CentralOutboxWorkerState>();
        services.AddScoped<IOutboxDispatcher>(sp =>
        {
            var options = sp.GetRequiredService<CentralOutboxOptions>();
            var policy = options.Enabled
                ? options.Policy
                : OutboxDispatchPolicy.ContractHarness;
            return new OutboxDispatcher(
                sp.GetRequiredService<ICentralDbContext>(),
                sp.GetRequiredService<IOutboxConsumerSink>(),
                policy);
        });
        services.AddScoped<IOutboxConsumerSink, DurableOutboxConsumerSink>();
        services.AddHostedService<CentralOutboxHostedService>();
    }

    public static void BootstrapCentralLocalEnvironment(this WebApplicationBuilder builder)
    {
        CentralLocalEnvironmentLoader.TryApplyFromRepository(builder.Environment.ContentRootPath, builder.Environment.EnvironmentName);
        CentralOperationalEnvironment.EnsureMinimumHostConfiguration();
    }
}
