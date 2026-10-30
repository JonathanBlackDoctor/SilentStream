using SilentStream.UninstallHelper;
using Xunit;

namespace SilentStream.Tests;

public sealed class ApplicationRestartTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sstream-restart-").FullName;
    private string Executable { get { var path=Path.Combine(_directory,"MediaCaptureHelper.exe");File.WriteAllBytes(path,[]);return path; } }

    [Fact]
    public async Task Slow_exit_must_finish_before_exactly_one_replacement_is_started()
    {
        var parent=new Parent(); var starts=0;
        var restart=ApplicationRestart.RestartAfterExitAsync(12,123,Executable,_=>parent,_=>{starts++;return true;},TimeSpan.FromSeconds(5));
        await parent.WaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0,starts); Assert.False(restart.IsCompleted);
        parent.Exit.TrySetResult();
        Assert.Equal(0,await restart);Assert.Equal(1,starts);Assert.True(parent.Disposed);
    }

    [Fact]
    public async Task An_already_exited_parent_allows_the_replacement()
    {
        var starts=0;
        Assert.Equal(0,await ApplicationRestart.RestartAfterExitAsync(12,123,Executable,_=>null,_=>{starts++;return true;},TimeSpan.FromSeconds(1)));
        Assert.Equal(1,starts);
    }

    [Theory]
    [InlineData(true,11)]
    [InlineData(false,13)]
    public async Task Reused_pid_or_noncooperative_wait_cannot_force_a_second_app(bool reused,int expected)
    {
        var parent=new Parent {StartTimeUtcTicks=reused?456:123};var starts=0;
        Assert.Equal(expected,await ApplicationRestart.RestartAfterExitAsync(12,123,Executable,_=>parent,_=>{starts++;return true;},TimeSpan.FromMilliseconds(30)));
        Assert.Equal(0,starts);Assert.True(parent.Disposed);
        parent.Exit.TrySetResult();
    }

    [Fact]
    public async Task Invalid_destination_does_not_inspect_or_start_any_process()
    {
        Assert.Equal(10,await ApplicationRestart.RestartAfterExitAsync(12,123,Path.Combine(_directory,"other.exe"),
            _=>throw new Exception("must not inspect"),_=>throw new Exception("must not launch"),TimeSpan.FromSeconds(1)));
    }

    public void Dispose()=>Directory.Delete(_directory,true);
    private sealed class Parent : ApplicationRestart.IParentProcess
    {
        public long StartTimeUtcTicks {get;init;}=123;
        public TaskCompletionSource Exit {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WaitEntered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public Task WaitForExitAsync(CancellationToken token) {WaitEntered.TrySetResult();return Exit.Task;}
        public void Dispose()=>Disposed=true;
    }
}
