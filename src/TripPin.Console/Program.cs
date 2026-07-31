using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using TripPin.Application;
using TripPin.Console.Input;
using TripPin.Console.Menus;
using TripPin.Console.Rendering;
using TripPin.Infrastructure;
using SystemConsole = System.Console;

namespace TripPin.Console;

/// <summary>
/// Host bootstrap. Composition only: no business logic lives here.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        IHost host;

        // Startup is guarded separately from the run: bad configuration and an
        // unsatisfiable DI graph both fail here, before there is a logger to
        // record them, and the raw exception must not reach the screen.
        try
        {
            host = Build(args);
        }
        catch (Exception exception)
        {
            SystemConsole.WriteLine($"  {ResultPresenter.UnexpectedFailure}");
            SystemConsole.Error.WriteLine(exception);

            return 1;
        }

        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var logger = host.Services.GetRequiredService<ILogger<MainMenuLoop>>();

        // Ctrl+C is handled rather than allowed to kill the process: e.Cancel
        // stops the runtime tearing us down, and StopApplication trips
        // ApplicationStopping, which OperationScope links into every
        // per-operation token. The in-flight request unwinds and the menu exits
        // cleanly.
        SystemConsole.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            SystemConsole.WriteLine();
            SystemConsole.WriteLine("  Cancelling...");
            lifetime.StopApplication();
        };

        await host.StartAsync().ConfigureAwait(false);

        var exitCode = 0;

        try
        {
            // One scope for the session, so the scoped repository and screens
            // are shared across menu actions.
            using var scope = host.Services.CreateScope();

            await scope.ServiceProvider
                .GetRequiredService<MainMenuLoop>()
                .RunAsync(lifetime.ApplicationStopping)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested while a prompt or request was in flight.
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "The application failed to start or run.");

            SystemConsole.WriteLine();
            SystemConsole.WriteLine($"  {ResultPresenter.UnexpectedFailure}");

            exitCode = 1;
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
            await Log.CloseAndFlushAsync().ConfigureAwait(false);

            host.Dispose();
        }

        return exitCode;
    }

    private static IHost Build(string[] args)
    {
        // Rooted at the binary rather than the working directory, which is
        // where appsettings.json actually sits. The default is
        // Directory.GetCurrentDirectory(), so `dotnet run --project src/...`
        // from the repository root would silently find no configuration and
        // fall back to defaults, with nothing on screen to say so.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.AddSerilog((services, configuration) => configuration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .WriteTo.Console());

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);

        builder.Services.AddSingleton<PersonRenderer>();
        builder.Services.AddSingleton<ResultPresenter>();
        builder.Services.AddSingleton<ConsoleReader>();
        builder.Services.AddScoped<PeopleListScreen>();
        builder.Services.AddScoped<PersonDetailScreen>();
        builder.Services.AddScoped<PersonEditScreen>();
        builder.Services.AddScoped<MainMenuLoop>();

        return builder.Build();
    }
}
