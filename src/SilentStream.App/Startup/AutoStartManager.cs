using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using SilentStream.Core.Contracts;

namespace SilentStream.App;

/// <summary>
/// Auto-start registration (plan §3.1): either the registry Run key (after login) or
/// a Task Scheduler logon task with highest privileges (before login UI settles).
/// Switching methods removes the other registration; uninstall calls DisableAll.
/// </summary>
public sealed class AutoStartManager(ILogService log)
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SilentStream";
    private const string TaskName = "SilentStream AutoStart";

    /// <summary>Applies the configured method ("startup" | "scheduler").</summary>
    public void Apply(string method)
    {
        try
        {
            if (string.Equals(method, "scheduler", StringComparison.OrdinalIgnoreCase))
            {
                RemoveRunKey();
                RegisterScheduledTask();
            }
            else
            {
                RemoveScheduledTask();
                RegisterRunKey();
            }
            log.Info($"자동 시작 등록 완료: {method}");
        }
        catch (Exception ex)
        {
            log.Error($"자동 시작 등록 실패({method})", ex);
        }
    }

    public void DisableAll()
    {
        try
        {
            RemoveRunKey();
            RemoveScheduledTask();
            log.Info("자동 시작 등록 해제 완료");
        }
        catch (Exception ex)
        {
            log.Error("자동 시작 해제 실패", ex);
        }
    }

    private static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SilentStream.exe");

    private static void RegisterRunKey()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(AppName, $"\"{ExecutablePath}\"");
    }

    private static void RemoveRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private static void RegisterScheduledTask()
    {
        using var service = new TaskService();
        var definition = service.NewTask();
        definition.RegistrationInfo.Description = "SilentStream 자동 시작 (로그인 시)";
        definition.Triggers.Add(new LogonTrigger { Delay = TimeSpan.Zero }); // 앱이 자체 30초 대기
        definition.Actions.Add(new ExecAction(ExecutablePath));
        definition.Principal.RunLevel = TaskRunLevel.Highest;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero; // 무제한 실행
        service.RootFolder.RegisterTaskDefinition(TaskName, definition);
    }

    private static void RemoveScheduledTask()
    {
        using var service = new TaskService();
        service.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
    }
}
