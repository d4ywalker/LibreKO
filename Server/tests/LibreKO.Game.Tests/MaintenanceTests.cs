using FluentAssertions;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Enums;
using LibreKO.Game.Protocol;
using LibreKO.Game.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibreKO.Game.Tests;

public class MaintenanceTests : GameTestBase
{
    private const string NormalLogin = "player_one";
    private const string GmLogin = "admin_zeus";

    private static ServiceProvider Provider() => CreateProvider(SeedAccounts);

    private static void SeedAccounts(Common.Infrastructure.Persistence.AppDbContext db)
    {
        db.Accounts.AddRange(
            new Account { Login = NormalLogin, Password = "pw", Nation = AccountNation.Karus, Authority = AccountAuthority.Normal },
            new Account { Login = GmLogin, Password = "pw", Nation = AccountNation.Karus, Authority = AccountAuthority.GameMaster }
        );
        db.SaveChanges();
    }

    [Fact]
    public async Task NormalPlayerLoginIsRefusedWhenMaintenanceIsActive()
    {
        using var provider = Provider();
        var maintenanceService = provider.GetRequiredService<IMaintenanceService>();
        maintenanceService.SetMaintenance(true, "Scheduled Patch Update");

        using var scope = provider.CreateScope();
        var preGame = scope.ServiceProvider.GetRequiredService<IPreGameService>();

        var result = await preGame.LoginAsync(NormalLogin, "pw");

        result.Success.Should().BeFalse();
        result.Maintenance.Should().BeTrue();
        result.MaintenanceMessage.Should().Contain("Scheduled Patch Update");
    }

    [Fact]
    public async Task GameMasterCanLoginEvenWhenMaintenanceIsActive()
    {
        using var provider = Provider();
        var maintenanceService = provider.GetRequiredService<IMaintenanceService>();
        maintenanceService.SetMaintenance(true, "Emergency Hotfix");

        using var scope = provider.CreateScope();
        var preGame = scope.ServiceProvider.GetRequiredService<IPreGameService>();

        var result = await preGame.LoginAsync(GmLogin, "pw");

        result.Success.Should().BeTrue();
        result.Maintenance.Should().BeFalse();
    }

    [Fact]
    public async Task NormalPlayerLoginSucceedsWhenMaintenanceIsInactive()
    {
        using var provider = Provider();
        var maintenanceService = provider.GetRequiredService<IMaintenanceService>();
        maintenanceService.SetMaintenance(false);

        using var scope = provider.CreateScope();
        var preGame = scope.ServiceProvider.GetRequiredService<IPreGameService>();

        var result = await preGame.LoginAsync(NormalLogin, "pw");

        result.Success.Should().BeTrue();
        result.Maintenance.Should().BeFalse();
    }

    [Fact]
    public void MaintenanceStatePersistsToDiskAndLoads()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"maintenance_test_{Guid.NewGuid():N}.json");
        try
        {
            var logger = NullLogger<MaintenanceService>.Instance;
            var service1 = new MaintenanceService(logger, tempFile);

            service1.SetMaintenance(true, "Database Migration", 60);
            File.Exists(tempFile).Should().BeTrue();

            var service2 = new MaintenanceService(logger, tempFile);
            service2.IsMaintenanceActive.Should().BeTrue();
            service2.MaintenanceReason.Should().Be("Database Migration");
            service2.ScheduledDurationMinutes.Should().Be(60);

            service2.SetMaintenance(false);
            File.Exists(tempFile).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task MaintenanceCountdownCanBeStartedAndCancelled()
    {
        var logger = NullLogger<MaintenanceService>.Instance;
        var service = new MaintenanceService(logger);

        var broadcastCount = 0;
        var finished = false;

        await service.StartCountdownAsync(
            countdownSeconds: 60,
            reason: "Upcoming Restart",
            durationMinutes: 15,
            broadcastNotice: _ => { broadcastCount++; return Task.CompletedTask; },
            onCountdownFinished: () => { finished = true; return Task.CompletedTask; }
        );

        service.IsCountdownRunning.Should().BeTrue();
        service.RemainingCountdownSeconds.Should().Be(60);
        broadcastCount.Should().BeGreaterThanOrEqualTo(1);

        service.CancelCountdown();

        service.IsCountdownRunning.Should().BeFalse();
        service.RemainingCountdownSeconds.Should().Be(0);
        finished.Should().BeFalse();
    }
}
