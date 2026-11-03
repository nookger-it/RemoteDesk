using Microsoft.EntityFrameworkCore;
using RemoteDesk.Domain.Entities;

namespace RemoteDesk.Infrastructure.Persistence;

public static class PilotDefaults
{
    public static readonly Guid OrganizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid HeadquartersSiteId = Guid.Parse("22222222-2222-2222-2222-222222222221");
    public static readonly Guid BranchSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid OperatorUserId = Guid.Parse("33333333-3333-3333-3333-333333333331");
    public static readonly Guid RestrictedOperatorUserId = Guid.Parse("33333333-3333-3333-3333-333333333332");
    public static readonly Guid AdminRoleId = Guid.Parse("44444444-4444-4444-4444-444444444441");
    public static readonly Guid StandardRoleId = Guid.Parse("44444444-4444-4444-4444-444444444442");
    public const string DefaultOperatorPassword = "PilotPassword!2026";
    public const string DefaultAdminMfaCode = "123456";
}

public sealed class RemoteDeskDbContext : DbContext
{
    public RemoteDeskDbContext(DbContextOptions<RemoteDeskDbContext> options)
        : base(options)
    {
    }

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceEndpoint> DeviceEndpoints => Set<DeviceEndpoint>();
    public DbSet<DevicePermission> DevicePermissions => Set<DevicePermission>();
    public DbSet<RemoteSession> Sessions => Set<RemoteSession>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>(b =>
        {
            b.ToTable("organizations");
            b.HasKey(x => x.Id);
        });

        modelBuilder.Entity<Site>(b =>
        {
            b.ToTable("sites");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.OrganizationId);
        });

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Role>(b =>
        {
            b.ToTable("roles");
            b.HasKey(x => x.Id);
        });

        modelBuilder.Entity<UserRole>(b =>
        {
            b.ToTable("user_roles");
            b.HasKey(x => new { x.UserId, x.RoleId });
        });

        modelBuilder.Entity<Device>(b =>
        {
            b.ToTable("devices");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.OrganizationId, x.SiteId });
        });

        modelBuilder.Entity<DeviceEndpoint>(b =>
        {
            b.ToTable("device_endpoints");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.DeviceId);
        });

        modelBuilder.Entity<DevicePermission>(b =>
        {
            b.ToTable("device_permissions");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.DeviceId);
        });

        modelBuilder.Entity<RemoteSession>(b =>
        {
            b.ToTable("sessions");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.DeviceId);
            b.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<AuditLog>(b =>
        {
            b.ToTable("audit_logs");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.CreatedAt);
        });
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        await Database.EnsureCreatedAsync(cancellationToken);

        if (!await Organizations.AnyAsync(cancellationToken))
        {
            Organizations.Add(new Organization
            {
                Id = PilotDefaults.OrganizationId,
                Name = "RemoteDesk Pilot Organization",
                Status = EntityStatus.Active
            });

            Sites.AddRange(
                new Site
                {
                    Id = PilotDefaults.HeadquartersSiteId,
                    OrganizationId = PilotDefaults.OrganizationId,
                    Name = "Headquarters LAN (HQ)",
                    Status = EntityStatus.Active
                },
                new Site
                {
                    Id = PilotDefaults.BranchSiteId,
                    OrganizationId = PilotDefaults.OrganizationId,
                    Name = "Branch Office A",
                    Status = EntityStatus.Active
                });

            Users.AddRange(
                new User
                {
                    Id = PilotDefaults.OperatorUserId,
                    OrganizationId = PilotDefaults.OrganizationId,
                    Email = "operator@remotedesk.local",
                    DisplayName = "Pilot Operator Admin",
                    Status = EntityStatus.Active
                },
                new User
                {
                    Id = PilotDefaults.RestrictedOperatorUserId,
                    OrganizationId = PilotDefaults.OrganizationId,
                    Email = "restricted@remotedesk.local",
                    DisplayName = "Restricted Helpdesk Operator",
                    Status = EntityStatus.Active
                });

            Roles.AddRange(
                new Role
                {
                    Id = PilotDefaults.AdminRoleId,
                    OrganizationId = PilotDefaults.OrganizationId,
                    Name = "OperatorAdmin"
                },
                new Role
                {
                    Id = PilotDefaults.StandardRoleId,
                    OrganizationId = PilotDefaults.OrganizationId,
                    Name = "StandardOperator"
                });

            UserRoles.AddRange(
                new UserRole
                {
                    UserId = PilotDefaults.OperatorUserId,
                    RoleId = PilotDefaults.AdminRoleId
                },
                new UserRole
                {
                    UserId = PilotDefaults.RestrictedOperatorUserId,
                    RoleId = PilotDefaults.StandardRoleId
                });

            await SaveChangesAsync(cancellationToken);
        }
    }
}

