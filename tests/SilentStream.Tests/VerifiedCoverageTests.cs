using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public sealed class VerifiedCoverageTests : IDisposable
{
    private readonly string _dir=Directory.CreateTempSubdirectory("coverage-tests-").FullName;
    private readonly DateTime _start=new(2026,11,2,10,0,0);
    private string Original=>Path.Combine(_dir,"original.mp4");
    private PeriodAssetCatalog Catalog=>new(Path.Combine(_dir,"catalog.json"));
    private RecordingSegment Segment=>new(Original,_start,_start.AddHours(1));
    private bool Allowed()=>new RecordingRetentionGuard(Catalog,new Queue(),new PeriodScheduleStore()).CanDelete(Segment);
    private PeriodAsset Receipt(string id,DateTime start,DateTime end,string? path=null,DateTime? sourceStart=null,int version=2)=>
        new(id,DateOnly.FromDateTime(_start),1,id,VideoId:"fake-id",SourceKey:id,UploadedFileSizeBytes:4096,
            UploadValidationVersion:version,VerifiedCoverage:new(path??Original,sourceStart??_start,start,end,4096,new string('a',64)));

    [Theory]
    [InlineData(0,true)] [InlineData(-1,true)] [InlineData(1,false)]
    public void Exact_join_and_overlap_cover_but_one_second_gap_does_not(int gap,bool expected)
    {
        Catalog.Upsert(Receipt("a",_start,_start.AddMinutes(30)));
        Catalog.Upsert(Receipt("b",_start.AddMinutes(30).AddSeconds(gap),_start.AddHours(1)));
        Assert.Equal(expected,Allowed()); // fresh catalogue each time also tests durable restore
    }
    [Fact] public void Duplicate_front_half_is_not_full_coverage()
    {Catalog.Upsert(Receipt("a",_start,_start.AddMinutes(30)));Catalog.Upsert(Receipt("b",_start,_start.AddMinutes(30)));Assert.False(Allowed());}
    [Theory] [InlineData(true)] [InlineData(false)]
    public void Other_file_or_other_generation_cannot_release_original(bool otherFile)
    {Catalog.Upsert(Receipt("a",_start,_start.AddHours(1),otherFile?Path.Combine(_dir,"other.mp4"):Original,otherFile?_start:_start.AddSeconds(-1)));Assert.False(Allowed());}
    [Theory] [InlineData(0)] [InlineData(1)]
    public void Legacy_receipts_remain_acknowledged_without_delete_permission(int version)
    {
        var saved=Catalog.Upsert(Receipt("old",_start,_start.AddHours(1),version:version));
        Assert.True(saved.RequiresCoverageRevalidation);Assert.False(Allowed());
        Assert.Equal("fake-id",Catalog.Find("old")!.VideoId);
        // Re-extraction/upsert must not silently promote the old upload.
        Catalog.Upsert(Receipt("old",_start,_start.AddHours(1)) with {VideoId=null});
        Assert.Equal(version,Catalog.Find("old")!.UploadValidationVersion);Assert.False(Allowed());
    }
    [Fact] public void Version_two_without_range_or_with_wrong_size_is_not_a_valid_receipt()
    {Catalog.Upsert(Receipt("a",_start,_start.AddHours(1)) with {VerifiedCoverage=null});Assert.False(Allowed());Catalog.Upsert(Receipt("b",_start,_start.AddHours(1)) with {UploadedFileSizeBytes=9999});Assert.False(Allowed());}
    [Fact] public void Old_mark_uploaded_call_cannot_promote_new_staged_coverage()
    {Catalog.Upsert(Receipt("a",_start,_start.AddHours(1)) with {VideoId=null});Catalog.MarkUploaded("a","old-video",4096,1);Assert.Null(Catalog.Find("a")!.VerifiedCoverage);Assert.False(Allowed());}
    [Fact] public void Receipt_range_survives_restart_and_requires_exact_source_identity()
    {var proof=Receipt("a",_start,_start.AddHours(1));Catalog.Upsert(proof with {VideoId=null});Catalog.MarkUploaded("a","new-video",4096,2,proof.VerifiedCoverage);Assert.Equal(proof.VerifiedCoverage,Catalog.Find("a")!.VerifiedCoverage);Assert.True(Allowed());}
    public void Dispose()=>Directory.Delete(_dir,true);
    private sealed class Queue:IUploadQueue{public void Enqueue(UploadJob j)=>throw new NotSupportedException();public IReadOnlyList<UploadJob> Snapshot()=>[];public void Start(CancellationToken ct)=>throw new NotSupportedException();}
}
