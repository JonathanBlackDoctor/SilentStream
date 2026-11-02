using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using SilentStream.App.Updates;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Media;
using SilentStream.Core.Models;
using SilentStream.Core.SingleInstance;
using Velopack;
using Velopack.Locators;
using Xunit;

namespace SilentStream.Inspection.Tests;

public sealed class LifecycleRegressionTests
{
    private static readonly string Root = Environment.GetEnvironmentVariable("SSTREAM_REGRESSION_OUTPUT")
        ?? Directory.CreateTempSubdirectory("lifecycle-regression-").FullName;
    private static string NewDir(string name) => Directory.CreateDirectory(Path.Combine(Root,"evidence",name+"-"+Guid.NewGuid().ToString("N"))).FullName;
    private static VelopackAsset Asset => new() { Version=new SemanticVersion(0,4,32), FileName="Audit-0.4.32-full.nupkg", Type=VelopackAssetType.Full };

    [Fact]
    public void Duplicate_launch_must_not_apply_before_instance_guard()
    {
        var dir=NewDir("duplicate-launch");
        var update=Path.Combine(dir,"Update.exe"); File.WriteAllBytes(update,[]);
        var process=new FakeProcess();
        var locator=new Locator(dir,update,process);
        var name="audit-"+Guid.NewGuid().ToString("N");
        using var primary=new SingleInstanceGuard(name);
        Assert.True(primary.IsPrimaryInstance);
        // Actual Velopack 1.2.0 library, with ALL process launch/exit calls intercepted.
        // Program.Main calls Run before AcquireProcessGuard in this order.
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"SilentStream.sln")))root=root.Parent;
        Assert.NotNull(root);
        var source=File.ReadAllText(Path.Combine(root!.FullName,"src","SilentStream.App","Program.cs"));
        Assert.Contains(".SetAutoApplyOnStartup(false)",source);
        Assert.True(source.IndexOf(".SetAutoApplyOnStartup(false)")<source.IndexOf(".Run();"));
        var builder=VelopackApp.Build().SetLocator(locator).SetArgs([]).SetAutoApplyOnStartup(false);
        builder.Run();
        using var secondary=new SingleInstanceGuard(name);
        Assert.False(secondary.IsPrimaryInstance);
        File.WriteAllText(Path.Combine(dir,"result.json"),JsonSerializer.Serialize(new {secondary=secondary.IsPrimaryInstance,process.Starts,process.Exits}));
        Assert.Empty(process.Starts);
    }

    [Fact]
    public async Task Cancelled_startup_must_not_apply_already_pending_update()
    {
        var client=new Client();
        using var manager=new AppUpdateManager(new Log(),()=>client,()=>false);
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>manager.PrepareStartupUpdateAsync(stop.Token));
        Assert.Equal(0,client.Applies);
    }

    [Fact]
    public async Task Cancellation_at_final_apply_boundary_is_not_swallowed()
    {
        var client=new Client();using var stop=new CancellationTokenSource();
        using var manager=new AppUpdateManager(new Log(),()=>client,()=>false);
        manager.UpdateStaged+=_=>stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>manager.PrepareStartupUpdateAsync(stop.Token));
        Assert.Equal(0,client.Applies);
    }

    [FfmpegFact]
    public async Task Eof_clip_must_not_certify_whole_requested_period()
    {
        var dir=NewDir("eof-receipt");var source=Path.Combine(dir,"source.mp4");
        var runner=new RealFfmpeg();
        var gen=await runner.RunAsync("",$"-hide_banner -y -f lavfi -i testsrc2=size=160x120:rate=10 -t 2 -c:v libx264 -preset ultrafast -g 10 -bf 0 \"{source}\"",TimeSpan.FromSeconds(15),default);
        Assert.Equal(0,gen.ExitCode);
        var start=new DateTime(2026,11,2,8,25,0);var end=start.AddHours(1);
        var service=new VodSegmentService(new Session(source,start),runner,new Log(),Path.Combine(dir,"clips"));
        var boundary=new PeriodBoundary(DateOnly.FromDateTime(start),1,start,end);
        var clips=await service.ExtractPeriodPartsAsync(boundary,new HashSet<string>(),default);
        Assert.Empty(clips);
        Assert.True(File.Exists(source));
        // A later request for the actual completed 2-second overlap is independently verifiable.
        boundary=boundary with {EndLocal=start.AddSeconds(2)};
        var clip=Assert.Single(await service.ExtractPeriodPartsAsync(boundary,new HashSet<string>(),default));var size=new FileInfo(clip.FilePath).Length;
        var uploadable=Mp4MediaSamples.HasUploadableVideo(clip.FilePath);
        var info=await runner.RunAsync("",$"-hide_banner -i \"{clip.FilePath}\"",TimeSpan.FromSeconds(10),default);
        var catalog=new PeriodAssetCatalog(Path.Combine(dir,"catalog.json"));
        catalog.Upsert(new PeriodAsset("audit",DateOnly.FromDateTime(start),1,"audit",VideoId:"fake-id",SourceKey:clip.SourceKey,UploadedFileSizeBytes:size,UploadValidationVersion:2,VerifiedCoverage:clip.VerifiedCoverage));
        var guard=new RecordingRetentionGuard(catalog,new Queue(),new PeriodScheduleStore());
        var activeDelete=guard.CanDelete(new RecordingSegment(source,start,null));
        var finalDelete=guard.CanDelete(new RecordingSegment(source,start,end));
        Assert.True(guard.CanDelete(new RecordingSegment(source,start,start.AddSeconds(2))));
        var recut=await service.ExtractPeriodPartsAsync(boundary,new HashSet<string>{clip.SourceKey!},default);
        File.WriteAllText(Path.Combine(dir,"result.json"),JsonSerializer.Serialize(new {source,clip,size,uploadable,activeDelete,finalDelete,recutCount=recut.Count,mediaInfo=info.Output}));
        Assert.False(finalDelete);
    }

    [FfmpegFact]
    public async Task Growing_fragmented_original_must_remain_protected_after_short_clip_receipt()
    {
        var dir=NewDir("growing-receipt");var source=Path.Combine(dir,"growing.mp4");
        using var producer=new Process {StartInfo=new ProcessStartInfo(RealFfmpeg.Path,$"-hide_banner -y -re -f lavfi -i testsrc2=size=640x360:rate=30 -t 12 -c:v libx264 -preset ultrafast -g 60 -bf 0 -movflags +frag_keyframe+empty_moov \"{source}\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true}};
        producer.Start();var generationLog=producer.StandardError.ReadToEndAsync();
        try
        {
            await Task.Delay(5600);
            var runner=new RealFfmpeg();var start=new DateTime(2026,11,2,8,25,0);
            var service=new VodSegmentService(new Session(source,start),runner,new Log(),Path.Combine(dir,"clips"));
            var boundary=new PeriodBoundary(DateOnly.FromDateTime(start),1,start,start.AddSeconds(12));
            Assert.Empty(await service.ExtractPeriodPartsAsync(boundary,new HashSet<string>(),default));
            await producer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));Assert.Equal(0,producer.ExitCode);
            var clip=Assert.Single(await service.ExtractPeriodPartsAsync(boundary,new HashSet<string>(),default));
            var clipInfo=await runner.RunAsync("",$"-hide_banner -i \"{clip.FilePath}\"",TimeSpan.FromSeconds(10),default);
            var decode=await runner.RunAsync("",$"-v error -i \"{clip.FilePath}\" -f null -",TimeSpan.FromSeconds(10),default);
            var uploadable=Mp4MediaSamples.HasUploadableVideo(clip.FilePath);var size=new FileInfo(clip.FilePath).Length;
            var originalInfo=await runner.RunAsync("",$"-hide_banner -i \"{source}\"",TimeSpan.FromSeconds(10),default);
            var catalog=new PeriodAssetCatalog(Path.Combine(dir,"catalog.json"));
            catalog.Upsert(new PeriodAsset("audit",DateOnly.FromDateTime(start),1,"audit",VideoId:"fake-id",SourceKey:clip.SourceKey,UploadedFileSizeBytes:size,UploadValidationVersion:1));
            var guard=new RecordingRetentionGuard(catalog,new Queue(),new PeriodScheduleStore());
            var finalDelete=guard.CanDelete(new RecordingSegment(source,start,start.AddSeconds(12)));
            var recut=await service.ExtractPeriodPartsAsync(boundary,new HashSet<string>{clip.SourceKey!},default);
            File.WriteAllText(Path.Combine(dir,"result.json"),JsonSerializer.Serialize(new {clip,size,uploadable,finalDelete,recutCount=recut.Count,clipInfo=clipInfo.Output,decodeExit=decode.ExitCode,decodeLog=decode.Output,originalInfo=originalInfo.Output,generationLog=await generationLog}));
            Assert.False(finalDelete); // v1 acknowledgement remains duplicate evidence, not delete permission
            Assert.NotNull(clip.VerifiedCoverage);
            Assert.True(clip.VerifiedCoverage!.MatchesFile(clip.FilePath));
            Assert.Equal(start.AddSeconds(12),clip.VerifiedCoverage.EndLocal);
        }
        finally {if(!producer.HasExited){producer.Kill(entireProcessTree:true);await producer.WaitForExitAsync();}}
    }

    [FfmpegTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Video_coverage_not_audio_duration_or_shifted_keyframe_controls_acceptance(bool longAudio)
    {
        var dir = NewDir("coverage-boundary");
        var source = Path.Combine(dir, "source.mp4");
        var runner = new RealFfmpeg();
        var args = longAudio
            ? $"-hide_banner -y -f lavfi -i testsrc2=size=160x120:rate=10:duration=2 -f lavfi -i sine=frequency=440:duration=4 -c:v libx264 -preset ultrafast -g 10 -bf 0 -c:a aac \"{source}\""
            : $"-hide_banner -y -f lavfi -i testsrc2=size=160x120:rate=10:duration=2 -c:v libx264 -preset ultrafast -g 10 -bf 0 \"{source}\"";
        Assert.Equal(0, (await runner.RunAsync("", args, TimeSpan.FromSeconds(15), default)).ExitCode);
        Assert.True(Mp4MediaSamples.HasUploadableVideo(source));
        var start = new DateTime(2026, 11, 2, 10, 0, 0);
        var service = new VodSegmentService(new Session(source, start), runner, new Log(), Path.Combine(dir, "clips"));
        var boundary = new PeriodBoundary(DateOnly.FromDateTime(start), 1,
            longAudio ? start : start.AddMilliseconds(100),
            longAudio ? start.AddSeconds(4) : start.AddMilliseconds(1100));
        Assert.Empty(await service.ExtractPeriodPartsAsync(boundary, new HashSet<string>(), default));
        Assert.True(File.Exists(source));
    }

    [FfmpegFact]
    public async Task Mid_period_recording_uses_actual_intersection_and_corrupt_video_is_rejected()
    {
        var dir = NewDir("intersection-corruption"); var source = Path.Combine(dir, "source.mp4");
        var runner = new RealFfmpeg();
        Assert.Equal(0, (await runner.RunAsync("", $"-hide_banner -y -f lavfi -i testsrc2=size=160x120:rate=10:duration=2 -c:v libx264 -preset ultrafast -g 10 -bf 0 \"{source}\"", TimeSpan.FromSeconds(15), default)).ExitCode);
        var start = new DateTime(2026, 11, 2, 10, 15, 0);
        var service = new VodSegmentService(new CompletedSession(source, start, start.AddSeconds(2)), runner, new Log(), Path.Combine(dir, "clips"));
        var boundary = new PeriodBoundary(DateOnly.FromDateTime(start), 1, start.AddMinutes(-15), start.AddMinutes(45));
        var clip = Assert.Single(await service.ExtractPeriodPartsAsync(boundary, new HashSet<string>(), default));
        Assert.Equal(start, clip.VerifiedCoverage!.StartLocal);
        Assert.Equal(start.AddSeconds(2), clip.VerifiedCoverage.EndLocal);

        // Keep MP4 sample tables but corrupt only this synthetic output's media payload.
        var bytes = File.ReadAllBytes(clip.FilePath);
        var damaged = false;
        for (var offset = 0; offset + 8 <= bytes.Length;)
        {
            var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            Assert.True(size >= 8 && offset + size <= bytes.Length);
            if (System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4) == "mdat")
            { Array.Clear(bytes, offset + 8, size - 8); damaged = true; break; }
            offset += size;
        }
        Assert.True(damaged);
        File.WriteAllBytes(clip.FilePath, bytes);
        Assert.True(Mp4MediaSamples.HasUploadableVideo(clip.FilePath));
        Assert.Null(await new VodMediaValidator(runner).VerifyAsync(new RecordingSession(source, start),
            start, start.AddSeconds(2), clip.FilePath, default));
        Assert.True(File.Exists(source));
    }

    [FfmpegTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Encoder_stop_must_flush_both_inputs_before_wait(bool closeAudioFirst)
    {
        var dir=NewDir("encoder-eof");var pipeName="sstream-audit-"+Guid.NewGuid().ToString("N");
        using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        var args=$"-hide_banner -y -f rawvideo -pixel_format bgra -video_size 64x64 -framerate 10 -i pipe:0 -f s16le -ar 8000 -ac 1 -probesize 32 -analyzeduration 0 -i \\\\.\\pipe\\{pipeName} -map 0:v -map 1:a -c:v libx264 -preset ultrafast -tune zerolatency -c:a aac -movflags +frag_keyframe+empty_moov \"{Path.Combine(dir,"recording.mp4")}\"";
        using var child=new Process { StartInfo=new ProcessStartInfo(RealFfmpeg.Path,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardError=true}};
        Assert.True(child.Start());var stderr=child.StandardError.ReadToEndAsync();
        try
        {
            var video=Task.Run(async()=>{for(var i=0;i<20;i++)await child.StandardInput.BaseStream.WriteAsync(new byte[64*64*4]);await child.StandardInput.BaseStream.FlushAsync();});
            var audio=Task.Run(async()=>{await pipe.WaitForConnectionAsync();await pipe.WriteAsync(new byte[32000]);await pipe.FlushAsync();});
            await Task.WhenAll(video,audio).WaitAsync(TimeSpan.FromSeconds(15));
            var log=new Log();
            using var encoder=new EncoderPipeline(DispatchProxy.Create<IScreenCaptureSource,NoopProxy>(),DispatchProxy.Create<IAudioMixer,NoopProxy>(),null!,log,new ProcessRunner());
            typeof(EncoderPipeline).GetField("_process",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(encoder,child);
            typeof(EncoderPipeline).GetField("_audioPipe",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(encoder,pipe);
            if(closeAudioFirst)pipe.Dispose();
            var watch=Stopwatch.StartNew();await encoder.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));watch.Stop();
            var output=await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            var forced=log.Messages.Any(x=>x.Contains("강제 종료"));
            File.WriteAllText(Path.Combine(dir,"result.json"),JsonSerializer.Serialize(new {closeAudioFirst,elapsedMs=watch.ElapsedMilliseconds,forced,log.Messages,output}));
            Assert.False(forced);
        }
        finally {try{if(!child.HasExited)child.Kill(entireProcessTree:true);}catch(InvalidOperationException){} }
    }

    [Fact]
    public async Task Writer_fault_still_waits_for_other_writer_before_disposing_pipe()
    {
        using var pipe = new NamedPipeServerStream("writer-drain-" + Guid.NewGuid().ToString("N"), PipeDirection.Out);
        using var encoder = new EncoderPipeline(DispatchProxy.Create<IScreenCaptureSource, NoopProxy>(),
            DispatchProxy.Create<IAudioMixer, NoopProxy>(), null!, new Log(), new ProcessRunner());
        var audioDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Set(string name, object value) => typeof(EncoderPipeline).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(encoder, value);
        Set("_audioPipe", pipe);
        Set("_videoWriterTask", Task.FromException(new IOException("synthetic writer failure")));
        Set("_audioWriterTask", audioDrained.Task);
        var handle = pipe.SafePipeHandle;
        var stop = encoder.StopAsync();
        try
        {
            Assert.False(stop.IsCompleted);
            Assert.False(handle.IsClosed);
        }
        finally { audioDrained.SetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.True(handle.IsClosed);
    }

    public class NoopProxy:DispatchProxy {protected override object? Invoke(MethodInfo? target,object?[]? args)=>null;}
    private sealed class Locator(string dir,string update,FakeProcess process):TestVelopackLocator("audit","0.4.31",dir,dir,dir,update,localPackage:Asset)
    {public override IProcessImpl Process=>process;public override List<VelopackAsset> GetLocalPackages()=>[];}
    private sealed class FakeProcess:IProcessImpl
    {public List<string> Starts {get;}=[];public List<int> Exits{get;}=[];public string GetCurrentProcessPath()=>"audit.exe";public uint GetCurrentProcessId()=>12345;public void StartProcess(string exePath,IEnumerable<string> args,string workDir,bool showWindow)=>Starts.Add(string.Join(" ",args));public void Exit(int code)=>Exits.Add(code);}
    private sealed class Client:IAppUpdateClient
    {public bool IsInstalled=>true;public VelopackAsset? UpdatePendingRestart=>Asset;public int Applies;public Task<UpdateInfo?> CheckForUpdatesAsync()=>throw new Exception("No network permitted");public Task DownloadUpdatesAsync(UpdateInfo u,CancellationToken c)=>throw new Exception("No download permitted");public void WaitExitThenApplyUpdates(VelopackAsset a,bool silent,bool restart)=>Applies++;}
    private sealed class Log:ILogService
    {public List<string> Messages{get;}=[];public void Debug(string s)=>Messages.Add(s);public void Info(string s)=>Messages.Add(s);public void Warn(string s)=>Messages.Add(s);public void Error(string s,Exception? e=null)=>Messages.Add(s);}
    private sealed class RealFfmpeg:IProcessRunner
    {public static string Path=>Environment.GetEnvironmentVariable("SSTREAM_AUDIT_FFMPEG")??throw new Exception("Explicit audit ffmpeg required");public Task<(int ExitCode,string Output)> RunAsync(string file,string args,TimeSpan timeout,CancellationToken ct)=>new ProcessRunner().RunAsync(Path,args,timeout,ct);}
    private sealed class Session(string path,DateTime start):IRecordingSessionInfo {public RecordingSession? Current=>new(path,start);}
    private sealed class CompletedSession(string path, DateTime start, DateTime end):IRecordingSessionInfo
    { public RecordingSession? Current => null; public IReadOnlyList<RecordingSegment> GetSegments(DateTime from, DateTime to) => [new(path, start, end)]; }
    private sealed class Queue:IUploadQueue {public void Enqueue(UploadJob job)=>throw new Exception("No upload permitted");public IReadOnlyList<UploadJob> Snapshot()=>[];public void Start(CancellationToken token)=>throw new Exception("No upload permitted");}
}

public sealed class FfmpegFactAttribute : FactAttribute
{public FfmpegFactAttribute(){if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SSTREAM_AUDIT_FFMPEG")))Skip="Set SSTREAM_AUDIT_FFMPEG to an isolated-test FFmpeg executable.";}}
public sealed class FfmpegTheoryAttribute : TheoryAttribute
{public FfmpegTheoryAttribute(){if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SSTREAM_AUDIT_FFMPEG")))Skip="Set SSTREAM_AUDIT_FFMPEG to an isolated-test FFmpeg executable.";}}
