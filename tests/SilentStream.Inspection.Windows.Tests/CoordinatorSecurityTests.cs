using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using SilentStream.Inspection;
using SilentStream.Inspection.Coordinator;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SilentStream.Inspection.Windows.Tests;

public sealed class CoordinatorSecurityTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"SilentStreamInspectionWindowsTests",Guid.NewGuid().ToString("N"));
    private readonly CoordinatorOptionsStore _options;
    private readonly InspectionStore _store;
    public CoordinatorSecurityTests(){Directory.CreateDirectory(_root);Environment.SetEnvironmentVariable("SILENTSTREAM_INSPECTION_ROOT",_root);_options=new CoordinatorOptionsStore();_store=new InspectionStore(_options);}
    public void Dispose(){Environment.SetEnvironmentVariable("SILENTSTREAM_INSPECTION_ROOT",null);SqliteConnection.ClearAllPools();try{Directory.Delete(_root,true);}catch{}}

    [Fact]
    public void SingleControllerLeaseRejectsSecondOwner()
    {
        var leases=new ControlLeaseManager(_store);var first=leases.Acquire("one");Assert.NotNull(first);Assert.Null(leases.Acquire("two"));Assert.True(leases.Release(first!.LeaseId,"one"));Assert.NotNull(leases.Acquire("two"));
    }

    [Fact]
    public void AuditHashChainDetectsTampering()
    {
        _store.Audit("token-a","run.create","ok","one");_store.Audit("token-a","step.execute","pass","two");Assert.True(_store.VerifyAuditChain());
        using var connection=new SqliteConnection($"Data Source={_options.DatabasePath}");connection.Open();using var command=connection.CreateCommand();command.CommandText="UPDATE audit SET details='tampered' WHERE sequence=1";command.ExecuteNonQuery();Assert.False(_store.VerifyAuditChain());
    }

    [Fact]
    public void RevokedTokenCannotAuthenticateOrKeepExistingPrincipal()
    {
        var raw=InspectionToken.Create();_options.Update(o=>o.Tokens.Add(new InspectionTokenEntry(InspectionToken.Hash(raw),InspectionRole.Admin,"test",DateTimeOffset.UtcNow)));
        var auth=new InspectionAuthenticator(_options);var context=new DefaultHttpContext();context.Request.Headers.Authorization=$"Bearer {raw}";var principal=auth.Authenticate(context);Assert.NotNull(principal);Assert.True(auth.IsActive(principal!));
        Assert.True(_options.RevokeToken(InspectionToken.Hash(raw)[..12]));Assert.False(auth.IsActive(principal!));Assert.Null(auth.Authenticate(context));
    }

    [Fact]
    public void ConfirmationIsShortLivedAndSingleUse()
    {
        var manager=new ConfirmationManager();var grant=manager.Create("admin",new DestructiveConfirmationRequest("run","bundle"));Assert.True(manager.Consume(grant.ConfirmationId,"admin","run","bundle"));Assert.False(manager.Consume(grant.ConfirmationId,"admin","run","bundle"));
    }

    [Fact]
    public void EventSequenceIsStrictlyMonotonic()
    {
        var a=_store.AppendEvent(null,"one",new{});var b=_store.AppendEvent(null,"two",new{});Assert.True(b>a);var events=_store.GetEvents(null,0);Assert.Equal(new[]{a,b},events.Select(ReadSequence));
    }

    [Fact]
    public void DestructiveApprovalIsBoundToTheRunningStepIdentifier()
    {
        var input = JsonSerializer.Deserialize<JsonElement>("""{"action":"reboot","payload":{}}""");
        var manifest = new InspectionBundleManifest
        {
            BundleId = "guarded",
            SigningKeyId = "test",
            Plan = new InspectionPlan
            {
                Steps = [new InspectionStep { Id = "custom-restart-boundary", Type = InspectionStepType.Privileged, Input = input }]
            }
        };
        _store.SaveBundle(new VerifiedInspectionBundle(manifest, _root));
        var runId = _store.CreateRun(new StoredBundle(manifest, _root), "admin", true);
        _store.UpdateStep(runId, "custom-restart-boundary", InspectionRunStatus.Running, 1, "waiting");
        var snapshot = JsonSerializer.SerializeToElement(_store.GetRunSnapshot(runId), InspectionProtocol.Json);
        Assert.True(snapshot.GetProperty("steps")[0].GetProperty("approvalRequired").GetBoolean());
        Assert.False(_store.HasAck(runId, "destructive-custom-restart-boundary"));
        _store.SaveAck(runId, "destructive-custom-restart-boundary", "admin");
        Assert.True(_store.HasAck(runId, "destructive-custom-restart-boundary"));
        Assert.False(_store.HasAck(runId, "destructive-another-step"));
    }

    private static long ReadSequence(object value)=>long.Parse(value.GetType().GetProperty("sequence")!.GetValue(value)!.ToString()!);
}
