using BobFx.Core.Services;
using Microsoft.Extensions.Options;

namespace BobFx.Core;

public static class ServiceRegistration
{
    /// <summary>Registers a background service that other services also talk to: one instance that both runs
    /// in the background and can be injected.</summary>
    public static IServiceCollection AddHostedSingleton<T>(this IServiceCollection services) where T : class, IHostedService =>
        services.AddSingleton<T>().AddHostedService(provider => provider.GetRequiredService<T>());

    /// <summary>An HTTP client for a device on the LAN: a short timeout, small answers, no redirects and no proxy,
    /// so that an address the operator typed cannot be used to reach anything else.</summary>
    public static IServiceCollection AddDeviceHttpClient(this IServiceCollection services, string name, TimeSpan timeout)
    {
        services.AddHttpClient(name, client => { client.Timeout = timeout; client.MaxResponseContentBufferSize = 64 * 1024; })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        return services;
    }
}

/// <summary>Lets a request through only from an allowed network and from the application's own pages. There is no
/// sign-in: who may operate the installation is decided by where a request comes from.</summary>
public sealed class ClientPolicyMiddleware(RequestDelegate next, IOptions<SecurityOptions> security, IHostEnvironment environment)
{
    // Development has no network list; the pages are served by the Vite server there.
    private readonly bool anyNetwork = environment.IsDevelopment();

    public Task InvokeAsync(HttpContext context)
    {
        if ((!anyNetwork && !security.Value.Allows(context.Connection.RemoteIpAddress)) || !SecurityOptions.IsSameOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        return next(context);
    }
}
