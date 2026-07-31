using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripPin.Application.People.Ports;
using TripPin.Infrastructure.Caching;
using TripPin.Infrastructure.Configuration;
using TripPin.Infrastructure.OData;
using TripPin.Infrastructure.Resilience;
using TripPin.Infrastructure.Session;

namespace TripPin.Infrastructure;

/// <summary>
/// Composition root for all I/O concerns.
/// </summary>
/// <remarks>
/// Handler order on the OData client is deliberate: resilience sits outside
/// the session handler, so a retried request re-resolves the session instead
/// of replaying a dead one.
/// <para>
/// The caching decorator is applied here too, which is what keeps IMemoryCache
/// out of the Application layer entirely. Removing caching is a matter of
/// dropping one registration.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(TripPinOptions.SectionName);

        services.AddOptions<TripPinOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Read once here as well, because the resilience pipeline and the
        // cache size limit are needed at registration time rather than on
        // first resolve.
        var settings = section.Get<TripPinOptions>() ?? new TripPinOptions();

        services.AddMemoryCache(cache => cache.SizeLimit = settings.Cache.SizeLimit);

        services.AddSingleton<ODataFilterTranslator>();
        services.AddSingleton<PersonMapper>();
        services.AddSingleton<ODataStatusInterpreter>();
        services.AddSingleton<PeopleCacheKeys>();

        AddSessionResolution(services, settings);
        AddODataClient(services, settings);
        AddCachingDecorator(services);

        return services;
    }

    /// <summary>
    /// The resolver client is separate from the OData client and follows
    /// redirects, which is how the session address is discovered at all.
    /// </summary>
    private static void AddSessionResolution(IServiceCollection services, TripPinOptions settings)
    {
        services.AddHttpClient(SessionUriProvider.ResolverClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = true,
            });

        // Singleton: a scoped provider would resolve a new session per scope,
        // and every extra session starts from pristine data, silently
        // discarding earlier writes.
        services.AddSingleton<ISessionUriProvider, SessionUriProvider>();
        services.AddTransient<SessionUriHandler>();
    }

    private static void AddODataClient(IServiceCollection services, TripPinOptions settings)
    {
        services.AddHttpClient<ODataPeopleRepository>(client =>
            {
                // The pre-redirect address. SessionUriHandler swaps this prefix
                // for the resolved session base on every request.
                client.BaseAddress = EnsureTrailingSlash(settings.BaseAddress);
                client.Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds);
                client.DefaultRequestHeaders.Accept.ParseAdd(
                    "application/json;odata.metadata=minimal");
            })

            // Added first, so it is the outermost handler. A retry therefore
            // re-enters SessionUriHandler and re-resolves the session rather
            // than replaying a request aimed at a dead one.
            .AddTripPinResilience(settings.Resilience)
            .AddHttpMessageHandler<SessionUriHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Redirects are resolved once by the session provider; letting
                // data requests follow them would mint sessions unnoticed.
                AllowAutoRedirect = false,
            });
    }

    /// <summary>
    /// Binds the port to the decorator, leaving both the use cases and
    /// ODataPeopleRepository unaware that caching exists.
    /// </summary>
    private static void AddCachingDecorator(IServiceCollection services) =>
        services.AddScoped<IPeopleRepository>(provider => new CachedPeopleRepository(
            provider.GetRequiredService<ODataPeopleRepository>(),
            provider.GetRequiredService<IMemoryCache>(),
            provider.GetRequiredService<PeopleCacheKeys>(),
            provider.GetRequiredService<IOptions<TripPinOptions>>(),
            provider.GetRequiredService<ILogger<CachedPeopleRepository>>()));

    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri($"{uri.AbsoluteUri}/");
}
