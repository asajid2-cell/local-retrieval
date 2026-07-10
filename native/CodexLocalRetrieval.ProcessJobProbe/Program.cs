using System.Diagnostics;
using System.Text;
using CodexLocalRetrieval.Core.Agents;

if (args.Length != 1 || args[0] != "owner-crash")
{
    Console.Error.WriteLine("usage: CodexLocalRetrieval.ProcessJobProbe owner-crash");
    return 2;
}

const string script = """
    $child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 120') -PassThru
    [Console]::Out.WriteLine($child.Id)
    [Console]::Out.Flush()
    Start-Sleep -Seconds 120
    """;
var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
var startInfo = new ProcessStartInfo
{
    FileName = "powershell.exe",
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    ArgumentList = { "-NoProfile", "-EncodedCommand", encoded },
};

using var job = WindowsProcessJob.CreateKillOnClose();
using var parent = job.StartContained(startInfo);
var childLine = await parent.StandardOutput.ReadLineAsync()
    .WaitAsync(TimeSpan.FromSeconds(10));
if (!int.TryParse(childLine, out var childPid))
{
    Console.Error.WriteLine("contained parent did not report its child pid");
    return 3;
}

Console.WriteLine($"{parent.Id}:{childPid}");
Console.Out.Flush();
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
