using System.Diagnostics;
using JarvisBackend.Services.Interfaces;

namespace JarvisBackend.Services;

public class PiperTtsService : ITtsService
{
    private readonly string _piperPath;
    private readonly string _modelPath;

    public PiperTtsService(IConfiguration config)
    {
        var baseDir = Directory.GetCurrentDirectory();
        var piperRel = config["PiperPath"] ?? "../AI/piper";
        _piperPath = Path.GetFullPath(Path.Combine(baseDir, piperRel));
        var modelFile = config["Piper:Model"] ?? "en_US-lessac-medium.onnx";
        _modelPath = Path.Combine(_piperPath, "models", modelFile);
    }

    public async Task<byte[]> GenerateSpeechAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<byte>();

        var ttsDir = Path.Combine(Directory.GetCurrentDirectory(), "audio", "tts");
        Directory.CreateDirectory(ttsDir);
        var outputFile = Path.Combine(ttsDir, $"{Guid.NewGuid()}.wav");

        var exePath = Path.Combine(_piperPath, "piper.exe");

        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Piper not found: {exePath}");

        if (!File.Exists(_modelPath))
            throw new FileNotFoundException($"Piper model not found: {_modelPath}");

        // Use relative model path so Piper runs like: piper.exe --model models\en_US-lessac-medium.onnx ...
        var modelFile = Path.GetFileName(_modelPath);
        var modelArg = Path.Combine("models", modelFile);
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--model \"{modelArg}\" --output_file \"{outputFile}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _piperPath
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        await process.StandardInput.WriteAsync(text);
        process.StandardInput.Close();

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 || !File.Exists(outputFile))
            throw new InvalidOperationException(PiperErrorMessage(process.ExitCode, exePath, psi.Arguments, _piperPath, output, error, outputFile));

        byte[] bytes = await File.ReadAllBytesAsync(outputFile);
        try { File.Delete(outputFile); } catch { /* ignore cleanup */ }
        return bytes;
    }

    private static string PiperErrorMessage(int exitCode, string exePath, string args, string workingDir, string stdout, string stderr, string expectedOutputFile)
    {
        var hex = unchecked((uint)exitCode).ToString("X8");
        var hint = exitCode switch
        {
            -1073741515 => " (0xC0000135 = DLL not found: run Piper from its folder or install Visual C++ Redistributable)",
            -1073741502 => " (0xC0000142 = initialization error)",
            _ => ""
        };
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Piper TTS failed.");
        sb.AppendLine($"  Exit code: {exitCode} (0x{hex}){hint}");
        sb.AppendLine($"  Command: {exePath}");
        sb.AppendLine($"  Args: {args}");
        sb.AppendLine($"  WorkingDirectory: {workingDir}");
        if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine($"  Stderr: {stderr.Trim()}");
        if (!string.IsNullOrWhiteSpace(stdout)) sb.AppendLine($"  Stdout: {stdout.Trim()}");
        if (exitCode == 0 && !File.Exists(expectedOutputFile))
            sb.AppendLine($"  Output file was not created: {expectedOutputFile}");
        return sb.ToString().TrimEnd();
    }
}