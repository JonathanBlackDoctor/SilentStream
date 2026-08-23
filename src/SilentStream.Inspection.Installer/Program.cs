using System.Diagnostics;

Application.EnableVisualStyles();
var root = AppContext.BaseDirectory;
var script = Path.Combine(root, "Install-InspectionServices.ps1");
var source = Path.Combine(root, "bin");
var publicKey = Directory.EnumerateFiles(root, "*.public-key.json", SearchOption.TopDirectoryOnly).FirstOrDefault()
    ?? Path.Combine(root, "public-key.json");
if (!File.Exists(script) || !Directory.Exists(source) || !File.Exists(publicKey))
{
    MessageBox.Show("설치 파일 또는 공개키(*.public-key.json)가 패키지 폴더에 없습니다.", "SilentStream 원격 점검", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return;
}
var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
var start = new ProcessStartInfo(powershell)
{
    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
    RedirectStandardOutput = true, RedirectStandardError = true
};
start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("RemoteSigned"); start.ArgumentList.Add("-WindowStyle"); start.ArgumentList.Add("Hidden");
start.ArgumentList.Add("-File"); start.ArgumentList.Add(script); start.ArgumentList.Add("-PublicKeyFile"); start.ArgumentList.Add(publicKey); start.ArgumentList.Add("-SourceRoot"); start.ArgumentList.Add(source);
var preseed = Path.Combine(root, "deployment-preseed.json");
if (File.Exists(preseed)) { start.ArgumentList.Add("-PreseedFile"); start.ArgumentList.Add(preseed); }
var setup = Directory.EnumerateFiles(root, "*Setup*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault(path => !Path.GetFileName(path).Equals("SilentStreamInspectionInstaller.exe", StringComparison.OrdinalIgnoreCase));
if (setup is not null) { start.ArgumentList.Add("-CachedInstallerPath"); start.ArgumentList.Add(setup); }
try
{
    using var process = Process.Start(start) ?? throw new InvalidOperationException("설치 프로세스를 시작하지 못했습니다.");
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = (await stdout).Trim(); var error = (await stderr).Trim();
    if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
    MessageBox.Show($"원격 점검 설치가 완료되었습니다.\n\n{output}", "SilentStream 원격 점검", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
catch (Exception ex)
{
    MessageBox.Show($"설치하지 못했습니다.\n\n{ex.Message}", "SilentStream 원격 점검", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
