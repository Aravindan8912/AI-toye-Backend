namespace JarvisBackend.Utils;

public static class ProcessHelper
{
    public static async Task<string> RunProcess(string command, string? workingDirectory = null)
    {
        var isWindows = OperatingSystem.IsWindows();
        var (fileName, arguments) = isWindows
            ? ("cmd.exe", $"/c {command}")
            : ("sh", $"-c \"{command.Replace("\"", "\\\"")}\"");

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
        };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            throw new InvalidOperationException($"Process failed: {stderr}");

        return stdout;
    }

    /// <summary>Same as <see cref="RunProcess"/> but returns stderr and exit code (for diagnostics, e.g. Whisper).</summary>
    public static async Task<(string StdOut, string StdErr, int ExitCode)> RunProcessWithDetailsAsync(string command, string? workingDirectory = null)
    {
        var isWindows = OperatingSystem.IsWindows();
        var (fileName, arguments) = isWindows
            ? ("cmd.exe", $"/c {command}")
            : ("sh", $"-c \"{command.Replace("\"", "\\\"")}\"");

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
        };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (stdout, stderr, process.ExitCode);
    }

    /// <summary>Runs an executable with arguments and stdin (e.g. Piper TTS). Use this when the process must receive stdin.</summary>
    public static async Task RunWithStdinAsync(string executablePath, string arguments, string stdinInput, string? workingDirectory = null)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
        };
        process.Start();

        await process.StandardInput.WriteAsync(stdinInput);
        process.StandardInput.Close();

        await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            throw new InvalidOperationException($"Process failed: {stderr}");
    }
}
