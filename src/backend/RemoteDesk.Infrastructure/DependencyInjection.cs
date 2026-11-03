using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RemoteDesk.Application.Abstractions;
using RemoteDesk.Application.Security;
using RemoteDesk.Infrastructure.Persistence;

namespace RemoteDesk.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRemoteDeskInfrastructure(
        this IServiceCollection services)
    {
        services.AddDbContext<RemoteDeskDbContext>((sp, options) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var connStr = config.GetConnectionString("DefaultConnection")
                ?? "Data Source=remotedesk-pilot.db";
            options.UseSqlite(connStr);
        });

        services.AddSingleton<ISessionTicketService>(_ => new SessionTicketService());
        services.AddScoped<IRemoteDeskManagementService, RemoteDeskManagementService>();

        return services;
    }
}
