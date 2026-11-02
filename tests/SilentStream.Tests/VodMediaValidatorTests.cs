using SilentStream.Core.Contracts;
using SilentStream.Core.Media;
using Xunit;

namespace SilentStream.Tests;

public sealed class VodMediaValidatorTests : IDisposable
{
    private readonly string _dir=Directory.CreateTempSubdirectory("media-validation-").FullName;
    private readonly DateTime _start=new(2026,11,2,8,25,0);
    private static string Trace(params string[] frames)=>"#tb 0: 1/1\n"+string.Join("\n",frames);
    private static string Frame(int pts,string hash="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")=>$"0, {pts}, {pts}, 1, 100, {hash}";
    private async Task<bool> Verify(string source,string clip,int seconds=2)
    {
        var output=Path.Combine(_dir,"clip.mp4");File.WriteAllBytes(output,new byte[4096]);
        var runner=new Runner(source,clip);
        var result=await new VodMediaValidator(runner).VerifyAsync(new(Path.Combine(_dir,"source.mp4"),_start),_start,_start.AddSeconds(seconds),output,default);
        Assert.All(runner.Args,a=>{Assert.Contains("-map 0:v:0 -an",a);Assert.Contains("-xerror",a);});
        return result is not null;
    }
    [Fact] public async Task Matching_decoded_frames_prove_complete_range()=>Assert.True(await Verify(Trace(Frame(0),Frame(1)),Trace(Frame(0),Frame(1))));
    [Fact] public async Task Short_video_is_not_rescued_by_audio_or_container_duration()=>Assert.False(await Verify(Trace(Frame(0)),Trace(Frame(0))));
    [Fact] public async Task Decoder_error_with_exit_zero_is_rejected()=>Assert.False(await Verify(Trace(Frame(0),Frame(1)),Trace(Frame(0),Frame(1))+"\nInvalid NAL unit size"));
    [Fact] public async Task Same_duration_but_shifted_or_different_frames_are_rejected()=>Assert.False(await Verify(Trace(Frame(0),Frame(1)),Trace(Frame(0,"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),Frame(1))));
    [Fact] public async Task Missing_frame_or_timestamp_gap_is_rejected()=>Assert.False(await Verify(Trace(Frame(0),Frame(2)),Trace(Frame(0),Frame(2)),3));
    [Fact] public async Task A_later_source_start_is_not_relabelled_as_full_coverage()=>Assert.False(await Verify(Trace(Frame(1),Frame(2)),Trace(Frame(0),Frame(1))));
    [Fact] public async Task Extra_boundary_frames_do_not_invalidate_all_matched_requested_frames()
    {
        Assert.True(await Verify(Trace(Frame(0), Frame(1)), Trace(Frame(0), Frame(1), Frame(2))));
        Assert.True(await Verify(Trace(Frame(0), Frame(1)), Trace(
            Frame(-1, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), Frame(0), Frame(1), Frame(2))));
    }
    [Fact] public async Task An_extra_output_frame_does_not_certify_a_short_source()
        => Assert.False(await Verify(Trace(Frame(0)), Trace(Frame(0), Frame(1))));
    [Fact] public async Task A_missing_requested_frame_is_not_rescued_by_extra_frames_after_the_window()
        => Assert.False(await Verify(Trace(Frame(0), Frame(1, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")), Trace(Frame(0), Frame(1), Frame(2))));
    [Fact] public async Task Subframe_source_offset_is_recorded_not_waived_in_the_receipt()
    {
        var output = Path.Combine(_dir, "clip.mp4"); File.WriteAllBytes(output, new byte[4096]);
        var source = "#tb 0: 1/4\n0, 1, 1, 4, 100, aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n0, 5, 5, 4, 100, bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var clip = "#tb 0: 1/4\n0, 0, 0, 4, 100, aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n0, 4, 4, 4, 100, bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var proof = await new VodMediaValidator(new Runner(source, clip)).VerifyAsync(
            new(Path.Combine(_dir, "source.mp4"), _start), _start, _start.AddSeconds(2), output, default);
        Assert.NotNull(proof);
        Assert.Equal(_start.AddMilliseconds(250), proof!.StartLocal);
        Assert.Equal(_start.AddSeconds(2), proof.EndLocal);
    }
    [Fact] public async Task Equivalent_rational_timebases_are_compared_without_a_GOP_tolerance()
    {
        var source = "#tb 0: 1/3\n" + string.Join("\n", Enumerable.Range(0, 3).Select(i => Frame(i)));
        var output = "#tb 0: 1/90000\n" + string.Join("\n", Enumerable.Range(0, 3)
            .Select(i => $"0, {i * 30000}, {i * 30000}, 30000, 100, aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.True(await Verify(source, output, 1));
        var shortTrace = "#tb 0: 1/30\n" + string.Join("\n", Enumerable.Range(0, 29).Select(i => Frame(i)));
        Assert.False(await Verify(shortTrace, shortTrace, 1)); // even one missing frame is not waived
    }
    public void Dispose()=>Directory.Delete(_dir,true);
    private sealed class Runner(params string[] results):IProcessRunner
    {private int index;public List<string> Args{get;}=[];public Task<(int ExitCode,string Output)> RunAsync(string f,string a,TimeSpan t,CancellationToken c){Args.Add(a);return Task.FromResult((0,results[index++]));}}
}
