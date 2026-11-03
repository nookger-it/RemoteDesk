using RemoteDesk.Domain.Entities;

namespace RemoteDesk.UnitTests;

public class ArchitectureFoundationTests
{
    [Fact]
    public void MaxConcurrentSessions_ShouldBeStrictlyFive()
    {
        Assert.Equal(5, RemoteSession.MaxConcurrentSessions);
    }

    [Fact]
    public void Device_ShouldGenerateUniqueUuidIdentity()
    {
        var deviceA = new Device
        {
            OrganizationId = Guid.NewGuid(),
            SiteId = Guid.NewGuid(),
            Hostname = "PC-A",
            AgentVersion = "1.0.0",
            PublicKey = "pub-key-a"
        };

        var deviceB = new Device
        {
            OrganizationId = Guid.NewGuid(),
            SiteId = Guid.NewGuid(),
            Hostname = "PC-B",
            AgentVersion = "1.0.0",
            PublicKey = "pub-key-b"
        };

        Assert.NotEqual(Guid.Empty, deviceA.Id);
        Assert.NotEqual(deviceA.Id, deviceB.Id);
    }
}

