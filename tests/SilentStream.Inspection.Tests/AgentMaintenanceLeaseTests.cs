using SilentStream.Inspection;
using Xunit;

namespace SilentStream.Inspection.Tests;

public sealed class AgentMaintenanceLeaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sstream-maintenance-").FullName;

    [Fact]
    public void Helper_maintenance_and_app_start_cannot_overlap()
    {
        using (var app = AgentUpdateSafety.AcquireApplicationLease(_root, false))
        {
            Assert.NotNull(app);
            Assert.Null(AgentUpdateSafety.AcquireApplicationLease(_root, true));
        }
        using var maintenance = AgentUpdateSafety.AcquireApplicationLease(_root, true);
        Assert.NotNull(maintenance);
        Assert.Null(AgentUpdateSafety.AcquireApplicationLease(_root, false));
    }

    [Fact]
    public void Preservation_hold_and_missing_coordinator_mapping_block_maintenance()
    {
        Assert.Null(AgentUpdateSafety.AcquireMaintenanceFromCoordinator(_root));
        File.WriteAllText(Path.Combine(_root, "coordinator.json"), System.Text.Json.JsonSerializer.Serialize(
            new { appConfigPath = Path.Combine(_root, "config.json") }));
        using (var lease = AgentUpdateSafety.AcquireMaintenanceFromCoordinator(_root)) Assert.NotNull(lease);
        File.WriteAllText(Path.Combine(_root, "original-preservation.hold.json"), "{broken");
        Assert.Null(AgentUpdateSafety.AcquireMaintenanceFromCoordinator(_root));
        Assert.Null(AgentUpdateSafety.AcquireApplicationLease(_root, false));
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
