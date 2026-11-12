using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace FlowEncode.Infrastructure;

internal static class SystemPathIntegration
{
    private const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    internal static SystemPathPlan BuildPlan(
        string currentPath,
        IReadOnlyList<string> previouslyOwnedDirectories,
        IReadOnlyList<string> desiredDirectories)
    {
        if (desiredDirectories.Any(static directory => string.IsNullOrWhiteSpace(directory) || directory.Contains(';')))
        {
            throw new InvalidOperationException("A tool directory is empty or contains a semicolon and cannot be represented in the Windows PATH. Move that tool to another folder first.");
        }

        var desired = desiredDirectories.DistinctBy(NormalizePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var owned = previouslyOwnedDirectories.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredSet = desired.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Preserve other applications' entries, including their order, spelling and unexpanded variables.
        var segments = currentPath.Split(';').Where(segment =>
            !owned.Contains(NormalizePath(segment)) || desiredSet.Contains(NormalizePath(segment))).ToList();
        var present = segments.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextOwned = new List<string>();
        var nextPath = string.Join(';', segments);
        foreach (var directory in desired)
        {
            if (present.Add(NormalizePath(directory)))
            {
                nextPath += nextPath.Length == 0 || nextPath.EndsWith(';') ? directory : ";" + directory;
                nextOwned.Add(directory);
            }
            else if (owned.Contains(NormalizePath(directory)))
            {
                nextOwned.Add(directory);
            }
        }

        if (nextPath.Length >= 32767)
        {
            throw new InvalidOperationException("The updated system PATH would exceed the Windows environment variable length limit.");
        }

        return new SystemPathPlan(nextPath, nextOwned);
    }

    [SupportedOSPlatform("windows")]
    internal static SystemPathPlan Apply(
        IReadOnlyList<string> previouslyOwnedDirectories,
        IReadOnlyList<string> desiredDirectories)
    {
        using var readKey = Registry.LocalMachine.OpenSubKey(EnvironmentKey)
            ?? throw new InvalidOperationException("The Windows system environment registry key was not found.");
        var currentPath = ReadPath(readKey);
        var plan = BuildPlan(currentPath, previouslyOwnedDirectories, desiredDirectories);
        if (string.Equals(currentPath, plan.Path, StringComparison.Ordinal))
        {
            return plan;
        }

        var valueKind = readKey.GetValueKind("Path");
        try
        {
            using var writeKey = Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: true)
                ?? throw new UnauthorizedAccessException("The system environment registry key is not writable.");
            if (!string.Equals(ReadPath(writeKey), currentPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The system PATH changed during this operation. Please try again.");
            }

            writeKey.SetValue("Path", plan.Path, valueKind);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            RunElevatedUpdate(currentPath, plan.Path);
        }

        using var verifyKey = Registry.LocalMachine.OpenSubKey(EnvironmentKey)
            ?? throw new InvalidOperationException("The Windows system environment registry key was not found.");
        if (!string.Equals(ReadPath(verifyKey), plan.Path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The system PATH update could not be verified. Please try again.");
        }

        CliEnvironmentIntegrationService.BroadcastEnvironmentChange();
        return plan;
    }

    [SupportedOSPlatform("windows")]
    private static string ReadPath(RegistryKey key)
        => key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;

    private static string NormalizePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            return Path.GetFullPath(expanded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static ProcessStartInfo BuildElevatedUpdateStartInfo(string expectedPath, string nextPath)
    {
        // Encode values as data. Paths containing quotes or PowerShell syntax must never become script code.
        var expectedBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(expectedPath));
        var nextBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(nextPath));
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            try {
                $expected = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{expectedBase64}}'))
                $next = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{nextBase64}}'))
                $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('{{EnvironmentKey}}', $true)
                if ($null -eq $key) { exit 1 }
                try {
                    $current = [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    if ($current -cne $expected) { exit 2 }
                    $kind = $key.GetValueKind('Path')
                    $key.SetValue('Path', $next, $kind)
                } finally { $key.Dispose() }
                exit 0
            } catch { exit 1 }
            """;
        var arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        if (arguments.Length > 30000)
        {
            throw new InvalidOperationException("The system PATH is too long for the administrator update command.");
        }

        return new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    [SupportedOSPlatform("windows")]
    private static void RunElevatedUpdate(string expectedPath, string nextPath)
    {
        try
        {
            using var process = Process.Start(BuildElevatedUpdateStartInfo(expectedPath, nextPath))
                ?? throw new InvalidOperationException("The administrator PATH update could not be started.");
            // Observe completion even after consent; do not report success while the elevated writer is running.
            process.WaitForExit();
            if (process.ExitCode == 2)
            {
                throw new InvalidOperationException("The system PATH changed during administrator authorization. Please try again.");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Windows could not update the system PATH with administrator privileges.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Administrator authorization was cancelled; the system PATH was not changed.", ex);
        }
    }
}

internal sealed record SystemPathPlan(string Path, IReadOnlyList<string> OwnedDirectories);

internal sealed record SystemPathRegistrationResult(bool Changed, IReadOnlyList<string> Directories);
