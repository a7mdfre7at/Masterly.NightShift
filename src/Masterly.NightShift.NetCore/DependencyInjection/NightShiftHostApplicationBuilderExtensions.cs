using Microsoft.Extensions.Hosting;

namespace Masterly.NightShift;

/// <summary>
/// Adds Masterly.NightShift to an application builder, such as the ones
/// <c>Host.CreateApplicationBuilder</c> and <c>WebApplication.CreateBuilder</c> return.
/// </summary>
public static class NightShiftHostApplicationBuilderExtensions
{
    /// <param name="builder">The application builder.</param>
    extension([NotNull] IHostApplicationBuilder builder)
    {
        /// <summary>Registers Masterly.NightShift, as <c>AddNightShift</c> on the service collection does.</summary>
        /// <param name="configuration">Configures the storage and the other global settings.</param>
        /// <returns>The builder, to chain calls.</returns>
        public IHostApplicationBuilder AddNightShift([NotNull] Action<IGlobalConfiguration> configuration)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Services.AddNightShift(configuration);
            return builder;
        }

        /// <summary>Registers Masterly.NightShift, as <c>AddNightShift</c> on the service collection does.</summary>
        /// <param name="configuration">Configures the storage and the other global settings, with the services.</param>
        /// <returns>The builder, to chain calls.</returns>
        public IHostApplicationBuilder AddNightShift([NotNull] Action<IServiceProvider, IGlobalConfiguration> configuration)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Services.AddNightShift(configuration);
            return builder;
        }

        /// <summary>Adds a job server that runs with the application, with the default options.</summary>
        /// <returns>The builder, to chain calls.</returns>
        public IHostApplicationBuilder AddNightShiftServer()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Services.AddNightShiftServer();
            return builder;
        }

        /// <summary>Adds a job server that runs with the application.</summary>
        /// <param name="optionsAction">Configures the server.</param>
        /// <returns>The builder, to chain calls.</returns>
        public IHostApplicationBuilder AddNightShiftServer([NotNull] Action<BackgroundJobServerOptions> optionsAction)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Services.AddNightShiftServer(optionsAction);
            return builder;
        }
    }
}
