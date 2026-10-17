using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Interoper.E2E;

/// <summary>Commands run through the shell, for the tests that build, pack or publish (QuickstartTests, SizeBudgetTests).</summary>
internal static class Shell
{
    // Runs a command to its end and returns its output; it fails the test when the command does. With an isolated home,
    // see Start.
    internal static async Task<string> RunAsync(string command, string directory, string? isolatedHome = null)
    {
        using var process = Start(command, directory, isolatedHome);
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

    // Runs through the shell, as a reader would type it, without the test run's MSBuild settings. With an isolated
    // home, NuGet packages and the .NET CLI's state (DOTNET_CLI_HOME) live there, so an earlier run's copies can't be used.
    internal static Process Start(string command, string directory, string? isolatedHome)
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
        if (isolatedHome is not null)
        {
            start.Environment["NUGET_PACKAGES"] = NuGetPackages(isolatedHome);
            start.Environment["DOTNET_CLI_HOME"] = CliHome(isolatedHome);
        }
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet)
        {
            start.Environment["PATH"] = Path.GetDirectoryName(dotnet) + Path.PathSeparator + start.Environment["PATH"];
        }
        return Process.Start(start)!;
    }

    // Where packages are restored to under an isolated home, and where the .NET CLI keeps its state.
    internal static string NuGetPackages(string isolatedHome) => Path.Combine(isolatedHome, ".nuget-packages");

    internal static string CliHome(string isolatedHome) => Path.Combine(isolatedHome, ".dotnet-home");

    internal static string Repo(params string[] parts) => Path.GetFullPath(Path.Combine([RepoRoot(), .. parts]));

    private static string RepoRoot([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "..", "..");
}
