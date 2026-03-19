using System.Diagnostics;

namespace Aporia.Git;

/// <summary>
/// Shallow-clones a repo to a temp directory. Disposes by deleting the clone.
/// Shared infrastructure for strategies that operate on a local working directory (Copilot, ClaudeCode).
/// </summary>
public sealed class RepoClone : IAsyncDisposable
{
    public string Path { get; }

    private RepoClone(string path)
    {
        Path = path;
    }

    public static async Task<RepoClone> CreateAsync(
        string cloneUrl, string branch, string token, CancellationToken ct = default)
    {
        // git clone --branch expects short name, not full ref
        if (branch.StartsWith("refs/heads/", StringComparison.Ordinal))
            branch = branch["refs/heads/".Length..];

        var tempDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"aporia_{Guid.NewGuid():N}");

        // GIT_ASKPASS: token never appears in process arguments or /proc/PID/cmdline.
        // The script reads the token from an env var and prints it to stdout.
        var askPassScript = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"aporia_askpass_{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(askPassScript, "#!/bin/sh\nprintf '%s' \"$APORIA_GIT_TOKEN\"", ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(askPassScript, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList =
                {
                    // Repo content safety
                    "-c", "core.symlinks=false",
                    "-c", "core.hooksPath=/dev/null",
                    "-c", "core.fsmonitor=false",
                    "-c", "filter.lfs.process=",
                    "-c", "filter.clean=",
                    "-c", "filter.smudge=",
                    "-c", "protocol.file.allow=never",
                    // Clone flags
                    "clone",
                    "--depth", "1",
                    "--branch", branch,
                    "--single-branch",
                    "--no-recurse-submodules",
                    "--no-tags",
                    cloneUrl,
                    tempDir
                },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.Environment["GIT_ASKPASS"] = askPassScript;
            startInfo.Environment["APORIA_GIT_TOKEN"] = token;
            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
            startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start git process");

            // Kill the process tree if cancellation is requested to avoid orphans.
            await using var killOnCancel = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            });

            // Read stderr concurrently with WaitForExit to avoid pipe-buffer deadlocks.
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                TryDelete(tempDir);
                throw new InvalidOperationException($"git clone failed (exit {process.ExitCode}): {stderr}");
            }

            return new RepoClone(tempDir);
        }
        finally
        {
            TryDelete(askPassScript);
        }
    }

    public ValueTask DisposeAsync()
    {
        TryDelete(Path);
        return ValueTask.CompletedTask;
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { /* best effort — container recycle handles orphans */ }
    }
}
