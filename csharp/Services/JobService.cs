using System.Diagnostics;
using System.Text.Json;
using Ps2TextureGrabber.Models;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Job lifecycle management:
///   • Persist a <see cref="JobState"/> to JSON
///   • Spawn the worker as a detached subprocess of the current exe
///   • Read job status for --status
/// </summary>
public sealed class JobService
{
    private readonly Logger _log;
    private readonly string _jobsDir;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public JobService(Logger log, string jobsDir)
    {
        _log     = log;
        _jobsDir = jobsDir;
        Directory.CreateDirectory(jobsDir);
    }

    // -------------------------------------------------------------------------
    // Create + spawn

    public sealed record SpawnResult(string JobId, string JobFile, string LogFile, int Pid);

    /// <summary>
    /// Persists the job state and launches a detached worker process.
    /// The worker is the same exe invoked with the "worker" subcommand.
    /// </summary>
    public SpawnResult Spawn(JobState state)
    {
        if (string.IsNullOrEmpty(state.Id))
            state.Id = NewJobId();

        var jobFile = Path.Combine(_jobsDir, $"{state.Id}.json");
        var logFile = Path.Combine(_jobsDir, $"{state.Id}.log");

        state.Status    = "pending";
        state.CreatedAt = DateTime.UtcNow.ToString("o");
        state.JobFile   = jobFile;
        state.LogFile   = logFile;

        Save(state, jobFile);
        _log.Debug($"Job file: {jobFile}");

        // Use the current exe path so the worker runs the same binary.
        var exePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine current exe path");

        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            Arguments        = $"worker --job-file \"{jobFile}\"",
            CreateNoWindow   = true,
            UseShellExecute  = false,
            WindowStyle      = ProcessWindowStyle.Hidden,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start worker process");

        _log.Success($"Spawned worker (PID {proc.Id}) for job {state.Id}");
        return new SpawnResult(state.Id, jobFile, logFile, proc.Id);
    }

    // -------------------------------------------------------------------------
    // Progress update (called by the worker in-process)

    public static void UpdateProgress(
        JobState state,
        string   jobFile,
        string?  step            = null,
        int?     progress        = null,
        string?  message         = null,
        long?    bytesDownloaded = null,
        long?    totalBytes      = null,
        string?  currentLink     = null)
    {
        if (step            is not null) state.Step             = step;
        if (progress        is not null) state.Progress         = progress.Value;
        if (message         is not null) state.Message          = message;
        if (bytesDownloaded is not null) state.BytesDownloaded  = bytesDownloaded.Value;
        if (totalBytes      is not null) state.TotalBytes       = totalBytes.Value;
        if (currentLink     is not null) state.CurrentLink      = currentLink;
        state.LastUpdate = DateTime.UtcNow.ToString("o");

        try { Save(state, jobFile); }
        catch { /* progress writes are best-effort */ }
    }

    // -------------------------------------------------------------------------
    // Status queries

    public string ReadStatusJson(string jobId)
    {
        var path = Path.Combine(_jobsDir, $"{jobId}.json");
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new { error = $"Unknown job: {jobId}", jobId });
        return File.ReadAllText(path);
    }

    public JobState? ReadState(string jobId)
    {
        var path = Path.Combine(_jobsDir, $"{jobId}.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<JobState>(File.ReadAllText(path));
    }

    public void PrintStatus(string jobId)
    {
        var state = ReadState(jobId);
        if (state is null)
        {
            _log.Error($"Unknown job: {jobId}");
            return;
        }

        var logFile = Path.Combine(_jobsDir, $"{jobId}.log");

        Console.WriteLine();
        Color($"Job {jobId}", ConsoleColor.Yellow);
        ColorLine($"  Status:     {state.Status}", ConsoleColor.Cyan);
        if (!string.IsNullOrEmpty(state.Step))
            ColorLine($"  Step:       {state.Step}", ConsoleColor.Cyan);
        if (state.Progress > 0)
        {
            var bar = new string('#', state.Progress / 5).PadRight(20, '.');
            ColorLine($"  Progress:   [{bar}] {state.Progress}%", ConsoleColor.Cyan);
        }
        Console.WriteLine($"  Query:      {state.Query}");
        Console.WriteLine($"  Serial:     {state.Serial}");
        Console.WriteLine($"  Game:       {state.GameName}");
        Console.WriteLine($"  Created:    {state.CreatedAt}");
        if (!string.IsNullOrEmpty(state.StartedAt))   Console.WriteLine($"  Started:    {state.StartedAt}");
        if (!string.IsNullOrEmpty(state.LastUpdate))  Console.WriteLine($"  Last upd:   {state.LastUpdate}");
        if (!string.IsNullOrEmpty(state.CompletedAt)) Console.WriteLine($"  Completed:  {state.CompletedAt}");
        if (!string.IsNullOrEmpty(state.CurrentLink)) Console.WriteLine($"  Link:       {state.CurrentLink}");
        if (!string.IsNullOrEmpty(state.ServedBy))    Console.WriteLine($"  Served by:  {state.ServedBy}");
        if (state.TotalBytes > 0)
        {
            Console.WriteLine(
                $"  Bytes:      {state.BytesDownloaded / 1_048_576.0:F2} / {state.TotalBytes / 1_048_576.0:F2} MB");
        }
        if (!string.IsNullOrEmpty(state.Message)) Console.WriteLine($"  Message:    {state.Message}");
        Console.WriteLine();

        if (File.Exists(logFile))
        {
            ColorLine($"--- tail {logFile} ---", ConsoleColor.DarkGray);
            var tailLines = File.ReadLines(logFile).TakeLast(20);
            foreach (var line in tailLines)
                ColorLine(line, ConsoleColor.DarkGray);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers

    private static string NewJobId()
        => Guid.NewGuid().ToString("N")[..12];

    private static void Save(JobState state, string path)
    {
        var json = JsonSerializer.Serialize(state, _jsonOpts);
        File.WriteAllText(path, json);
    }

    private static void Color(string text, ConsoleColor c)
    {
        Console.ForegroundColor = c;
        Console.Write(text);
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void ColorLine(string text, ConsoleColor c)
    {
        Console.ForegroundColor = c;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
