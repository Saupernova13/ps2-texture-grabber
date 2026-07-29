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

        // On Linux the usual caller is `ssh deck 'dlps2tex ...'`, and a plain child does not
        // outlive that session. See SpawnDetachedLinux for why setsid alone is not enough.
        if (!OperatingSystem.IsWindows() && TrySpawnViaSystemdRun(exePath, jobFile, logFile, state.Id, out var unitPid))
        {
            _log.Success($"Spawned worker (PID {unitPid}) for job {state.Id}");
            return new SpawnResult(state.Id, jobFile, logFile, unitPid);
        }

        var psi = new ProcessStartInfo
        {
            FileName               = exePath,
            Arguments              = $"worker --job-file \"{jobFile}\"",
            CreateNoWindow         = true,
            UseShellExecute        = false,
            WindowStyle            = ProcessWindowStyle.Hidden,
            // Redirect stdio so the worker does not inherit the parent console
            // handles — otherwise the cmd launcher (~\.openclaw\dlps2tex.cmd)
            // stays attached to the background worker and the terminal hangs.
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start worker process");

        // Drop our ends of the pipes — the worker logs to a file, not stdout.
        proc.StandardInput.Close();
        proc.StandardOutput.Close();
        proc.StandardError.Close();

        _log.Success($"Spawned worker (PID {proc.Id}) for job {state.Id}");
        return new SpawnResult(state.Id, jobFile, logFile, proc.Id);
    }

    /// <summary>
    /// Start the worker as a transient systemd user unit, so it survives the SSH session
    /// that started it. Returns false if systemd-run or a user bus is unavailable, leaving
    /// the caller to spawn an ordinary child.
    ///
    /// A plain background child is not enough on a systemd host configured with
    /// KillUserProcesses=yes (SteamOS ships exactly that): logind kills the whole session
    /// CGROUP on logout, and neither a new session id from setsid nor closed stdio takes a
    /// process out of that cgroup. The worker dies the moment `ssh deck '...'` returns,
    /// leaving the job at "pending" with an empty log and nothing explaining why.
    ///
    /// A --user unit runs under the user manager (user@UID.service), a different cgroup
    /// that session teardown does not touch. To survive the *last* session ending as well,
    /// the user manager must persist: `loginctl enable-linger &lt;user&gt;`.
    /// </summary>
    private bool TrySpawnViaSystemdRun(string exePath, string jobFile, string logFile, string jobId, out int pid)
    {
        pid = 0;
        try
        {
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(runtimeDir) || !File.Exists(Path.Combine(runtimeDir, "bus")))
                return false;   // no user bus to talk to

            var unit = $"ps2tex-{jobId}";
            var psi = new ProcessStartInfo
            {
                FileName               = "systemd-run",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            foreach (var arg in new[]
                     {
                         "--user", "--collect", "--quiet", $"--unit={unit}",
                         $"--property=StandardOutput=append:{logFile}",
                         $"--property=StandardError=append:{logFile}",
                         "--property=StandardInput=null",
                         exePath, "worker", "--job-file", jobFile,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(10_000);
            if (!proc.HasExited || proc.ExitCode != 0) return false;

            pid = ReadUnitMainPid(unit);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;   // systemd-run absent or unusable; caller falls back
        }
    }

    /// <summary>
    /// MainPID of a transient user unit. Best-effort: the worker is a grandchild of systemd
    /// rather than of this process, and the pid is only ever displayed, so 0 is survivable.
    /// </summary>
    private static int ReadUnitMainPid(string unit)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "systemctl",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            foreach (var arg in new[] { "--user", "show", "-p", "MainPID", "--value", $"{unit}.service" })
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null) return 0;
            var text = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5_000);
            return int.TryParse(text, out var value) ? value : 0;
        }
        catch
        {
            return 0;
        }
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
