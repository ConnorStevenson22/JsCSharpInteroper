using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Interoper.E2E;

/// <summary>Commands run through the shell, for the tests that build, pack or publish (QuickstartTests, SizeBudgetTests).</summary>
internal static class Shell
{
    internal static async Task<string> RunAsync(string command, string directory, string? nugetPackages = null)
    {
        using var process = Start(command, directory, nugetPackages);
        var output = new StringBuilder();
        void OnLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null) lock (output) output.AppendLine(e.Data);
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (TestContext.Current.CancellationToken.IsCancellationRequested) throw;
            lock (output) Assert.Fail($"`{command}` didn't finish within 5 minutes:\n{output}");
        }
        process.WaitForExit(); // the output's last lines
        lock (output)
        {
            Assert.True(process.ExitCode == 0, $"`{command}` exited with code {process.ExitCode}:\n{output}");
            return output.ToString();
        }
    }

    // Through the shell, as a reader would type it. The child doesn't inherit the test run's MSBuild settings, which
    // would point a nested build at this repo's.
    internal static Process Start(string command, string directory, string? nugetPackages)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd") { Arguments = $"/c {command}" } // cmd parses its own command line
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        start.WorkingDirectory = directory;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(name);
        }
        if (nugetPackages is not null)
        {
            start.Environment["NUGET_PACKAGES"] = nugetPackages;
        }
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet)
        {
            start.Environment["PATH"] = Path.GetDirectoryName(dotnet) + Path.PathSeparator + start.Environment["PATH"];
        }
        return Process.Start(start)!;
    }

    internal static string Repo(params string[] parts) => Path.GetFullPath(Path.Combine([RepoRoot(), .. parts]));

    private static string RepoRoot([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "..", "..");
}
