using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

public sealed class LocalProfileLibraryService : IProfileLibraryService
{
    private const string TemplateExchangeFormat = "flowencode/template/v1";
    private const string TemplateFileExtension = ".profile";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true
    };

    private readonly LocalAppPaths _paths;

    public LocalProfileLibraryService(LocalAppPaths paths)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<SavedTemplate>> GetUserTemplatesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await LoadTemplateEntriesAsync(cancellationToken);
        return entries.Select(static entry => entry.Template).ToList();
    }

    public async Task<SavedTemplate> ReadTemplateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Template file path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The selected template file does not exist.", filePath);
        }

        if (!string.Equals(Path.GetExtension(filePath), TemplateFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected file is not a valid FlowEncode template.");
        }

        return await ReadTemplateFileAsync(filePath, cancellationToken);
    }

    public async Task ExportTemplateAsync(SavedTemplate template, string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(template);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Template export path is required.", nameof(filePath));
        }

        var targetDirectory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var normalizedTemplate = NormalizeTemplate(template);
        await WriteTemplateFileAsync(normalizedTemplate, filePath, cancellationToken);
    }

    public async Task<SavedTemplate> SaveTemplateAsync(
        string name,
        string notes,
        EncodingProfile profile,
        string? templateId = null,
        bool isPinned = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Template name is required.", nameof(name));
        }

        var normalizedName = name.Trim();
        var normalizedNotes = notes?.Trim() ?? string.Empty;
        var entries = await LoadTemplateEntriesAsync(cancellationToken);
        var currentEntry = FindEntryById(entries, templateId);
        var sameNameEntry = FindEntryByName(entries, normalizedName);

        if (currentEntry is not null && currentEntry.Template.IsPinned)
        {
            throw new InvalidOperationException("Pinned templates must be unpinned before they can be modified.");
        }

        if (sameNameEntry is not null
            && sameNameEntry.Template.IsPinned
            && !string.Equals(sameNameEntry.Template.Id, templateId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pinned templates must be unpinned before they can be modified.");
        }

        var resolvedTemplateId = currentEntry?.Template.Id
            ?? sameNameEntry?.Template.Id
            ?? Guid.NewGuid().ToString("N");
        var pathsToDelete = entries
            .Where(entry =>
                string.Equals(entry.Template.Id, resolvedTemplateId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Template.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
            .Select(static entry => entry.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var occupiedPaths = entries
            .Select(static entry => entry.FilePath)
            .Except(pathsToDelete, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetPath = BuildAvailableTemplateFilePath(normalizedName, occupiedPaths);
        var template = NormalizeTemplate(
            new SavedTemplate(
                resolvedTemplateId,
                normalizedName,
                normalizedNotes,
                profile,
                DateTimeOffset.Now,
                isPinned));

        await WriteTemplateFileAsync(template, targetPath, cancellationToken);

        foreach (var path in pathsToDelete.Where(path => !AreSamePath(path, targetPath)))
        {
            DeleteFileIfExists(path);
        }

        return template;
    }

    public async Task DeleteTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        var entries = await LoadTemplateEntriesAsync(cancellationToken);
        var entriesToDelete = entries
            .Where(entry => string.Equals(entry.Template.Id, templateId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (entriesToDelete.Count == 0)
        {
            return;
        }

        if (entriesToDelete.Any(static entry => entry.Template.IsPinned))
        {
            throw new InvalidOperationException("Pinned templates must be unpinned before they can be deleted.");
        }

        foreach (var filePath in entriesToDelete
                     .Select(static entry => entry.FilePath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeleteFileIfExists(filePath);
        }
    }

    public async Task<SavedTemplate> SetTemplatePinnedAsync(
        string templateId,
        bool isPinned,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new ArgumentException("Template id is required.", nameof(templateId));
        }

        var entries = await LoadTemplateEntriesAsync(cancellationToken);
        var entry = FindEntryById(entries, templateId)
            ?? throw new InvalidOperationException("The selected template no longer exists.");

        if (entry.Template.IsPinned == isPinned)
        {
            return entry.Template;
        }

        var updatedTemplate = NormalizeTemplate(entry.Template with
        {
            UpdatedAt = DateTimeOffset.Now,
            IsPinned = isPinned
        });

        await WriteTemplateFileAsync(updatedTemplate, entry.FilePath, cancellationToken);
        return updatedTemplate;
    }

    public Task<CommandPreview> BuildPreviewAsync(EncodingProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var preview = profile.Kind switch
        {
            EncoderKind.X264 => new CommandPreview(
                $"{profile.Name} · x264 管线预览",
                BuildX264Preview(profile),
                "命令预览使用 VapourSynth 管线占位符。后续接入作业队列时，可以直接替换输入脚本与输出路径。"),
            EncoderKind.X265 => new CommandPreview(
                $"{profile.Name} · x265 管线预览",
                BuildX265Preview(profile),
                "x265 使用 y4m 管道输入模型，便于复用旧项目对 Avisynth / VapourSynth 的统一抽象。"),
            EncoderKind.SvtAv1 => new CommandPreview(
                $"{profile.Name} · SVT-AV1 管线预览",
                BuildSvtAv1Preview(profile),
                "当前默认输出 IVF。后续可以在 mux 阶段再接 MKV/MP4 封装。"),
            _ => new CommandPreview(profile.Name, string.Empty, string.Empty)
        };

        return Task.FromResult(preview);
    }

    private async Task<List<TemplateFileEntry>> LoadTemplateEntriesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.WorkspaceTemplatesRootPath);

        var entries = new List<TemplateFileEntry>();
        foreach (var filePath in Directory.EnumerateFiles(_paths.WorkspaceTemplatesRootPath, $"*{TemplateFileExtension}", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var template = await ReadTemplateFileAsync(filePath, cancellationToken);
                entries.Add(new TemplateFileEntry(template, filePath));
            }
            catch (Exception ex)
            {
                AppDiagnosticsLog.Write(
                    _paths,
                    nameof(LocalProfileLibraryService),
                    $"Skipped template file '{filePath}' because it could not be loaded. {ex.GetType().Name}: {ex.Message}",
                    AppDiagnosticSeverity.Warning);
            }
        }

        return entries
            .OrderByDescending(static entry => entry.Template.IsPinned)
            .ThenByDescending(static entry => entry.Template.UpdatedAt)
            .ThenBy(static entry => entry.Template.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<SavedTemplate> ReadTemplateFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(filePath, cancellationToken);

        try
        {
            var document = JsonSerializer.Deserialize<TemplateExchangeDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("The selected file is not a valid FlowEncode template.");
            return CreateTemplateFromDocument(document, filePath);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The selected file is not a valid FlowEncode template.");
        }
    }

    private async Task WriteTemplateFileAsync(SavedTemplate template, string filePath, CancellationToken cancellationToken)
    {
        var normalizedTemplate = NormalizeTemplate(template);
        var document = new TemplateExchangeDocument(
            TemplateExchangeFormat,
            normalizedTemplate.Id,
            normalizedTemplate.Name,
            normalizedTemplate.Notes,
            normalizedTemplate.Profile,
            normalizedTemplate.UpdatedAt,
            normalizedTemplate.UpdatedAt,
            normalizedTemplate.IsPinned);

        var targetPath = Path.GetFullPath(filePath);
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var tempPath = Path.Combine(
            targetDirectory ?? _paths.WorkspaceTemplatesRootPath,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = File.Open(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        finally
        {
            DeleteFileIfExists(tempPath);
        }
    }

    private TemplateFileEntry? FindEntryById(IEnumerable<TemplateFileEntry> entries, string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return null;
        }

        return entries.FirstOrDefault(entry =>
            string.Equals(entry.Template.Id, templateId, StringComparison.OrdinalIgnoreCase));
    }

    private TemplateFileEntry? FindEntryByName(IEnumerable<TemplateFileEntry> entries, string templateName)
    {
        return entries.FirstOrDefault(entry =>
            string.Equals(entry.Template.Name, templateName, StringComparison.OrdinalIgnoreCase));
    }

    private SavedTemplate NormalizeTemplate(SavedTemplate template)
    {
        var normalizedName = template.Name.Trim();
        var normalizedNotes = template.Notes?.Trim() ?? string.Empty;
        var normalizedUpdatedAt = template.UpdatedAt == default ? DateTimeOffset.Now : template.UpdatedAt;

        return template with
        {
            Name = normalizedName,
            Notes = normalizedNotes,
            UpdatedAt = normalizedUpdatedAt,
            Profile = template.Profile with
            {
                Name = normalizedName,
                Description = normalizedNotes
            }
        };
    }

    private SavedTemplate CreateTemplateFromDocument(TemplateExchangeDocument document, string filePath)
    {
        if (!string.IsNullOrWhiteSpace(document.Format)
            && !string.Equals(document.Format, TemplateExchangeFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected template file uses an unsupported format.");
        }

        if (string.IsNullOrWhiteSpace(document.Name))
        {
            throw new InvalidDataException("The selected template file does not contain a template name.");
        }

        if (document.Profile is null)
        {
            throw new InvalidDataException("The selected template file does not contain a valid encoding profile.");
        }

        return NormalizeTemplate(
            new SavedTemplate(
                string.IsNullOrWhiteSpace(document.Id) ? BuildStableTemplateId(filePath) : document.Id.Trim(),
                document.Name.Trim(),
                document.Notes?.Trim() ?? string.Empty,
                document.Profile,
                ResolveUpdatedAt(document),
                document.IsPinned ?? false));
    }

    private static DateTimeOffset ResolveUpdatedAt(TemplateExchangeDocument document)
    {
        var updatedAt = document.UpdatedAt ?? document.ExportedAt ?? DateTimeOffset.Now;
        return updatedAt == default ? DateTimeOffset.Now : updatedAt;
    }

    private string BuildAvailableTemplateFilePath(string templateName, ISet<string> occupiedPaths)
    {
        var sanitizedName = SanitizeFileName(templateName);
        var candidate = Path.Combine(_paths.WorkspaceTemplatesRootPath, $"{sanitizedName}{TemplateFileExtension}");
        if (!occupiedPaths.Contains(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; suffix < short.MaxValue; suffix++)
        {
            candidate = Path.Combine(_paths.WorkspaceTemplatesRootPath, $"{sanitizedName}-{suffix}{TemplateFileExtension}");
            if (!occupiedPaths.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a template file name.");
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? "template" : sanitized;
    }

    private static void DeleteFileIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            var attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(filePath, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(filePath);
        }
    }

    private static string BuildStableTemplateId(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static bool AreSamePath(string leftPath, string rightPath)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(leftPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(rightPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private sealed record TemplateFileEntry(SavedTemplate Template, string FilePath);

    private sealed record TemplateExchangeDocument(
        string? Format,
        string? Id,
        string? Name,
        string? Notes,
        EncodingProfile? Profile,
        DateTimeOffset? UpdatedAt,
        DateTimeOffset? ExportedAt,
        bool? IsPinned);

    private static string BuildX264Preview(EncodingProfile profile)
    {
        var preset = EncoderArgumentValueNormalizer.NormalizePresetForCli(profile.Kind, profile.Preset);
        var tune = EncoderArgumentValueNormalizer.NormalizeTuneForCli(profile.Kind, profile.Tune);
        var profileValue = EncoderArgumentValueNormalizer.NormalizeProfileForCli(profile.Kind, profile.Profile);
        var statsFile = "\"{output}.x264_2pass.log\"";

        if (profile.RateControl == RateControlMode.TwoPass)
        {
            var pass1 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "x264",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 1, passCount: 2),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--demuxer y4m --stdin y4m",
                BuildOptionalSegment(profile.AdditionalArguments),
                "-o \"NUL\" -");

            var pass2 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "x264",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 2, passCount: 2),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--demuxer y4m --stdin y4m",
                BuildOptionalSegment(profile.AdditionalArguments),
                $"-o \"{{output}}.{profile.OutputContainer}\" -");

            return JoinStagePreview(pass1, pass2);
        }

        return JoinArguments(
            @"vspipe -c y4m ""{input}.vpy"" - |",
            "x264",
            $"--preset {preset}",
            BuildRateControlArguments(profile.Kind, profile),
            BuildOptionalArgument("--tune", tune),
            BuildOptionalArgument("--profile", profileValue),
            "--demuxer y4m --stdin y4m",
            BuildOptionalSegment(profile.AdditionalArguments),
            $"-o \"{{output}}.{profile.OutputContainer}\" -");
    }

    private static string BuildX265Preview(EncodingProfile profile)
    {
        var preset = EncoderArgumentValueNormalizer.NormalizePresetForCli(profile.Kind, profile.Preset);
        var tune = EncoderArgumentValueNormalizer.NormalizeTuneForCli(profile.Kind, profile.Tune);
        var profileValue = EncoderArgumentValueNormalizer.NormalizeProfileForCli(profile.Kind, profile.Profile);
        var statsFile = "\"{output}.x265_2pass.log\"";

        if (profile.RateControl == RateControlMode.TwoPass)
        {
            var pass1 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "x265",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 1, passCount: 2),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--y4m --input -",
                BuildOptionalSegment(profile.AdditionalArguments),
                BuildOptionalSegment(profile.UhdParameters),
                "-o \"NUL\"");

            var pass2 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "x265",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 2, passCount: 2),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--y4m --input -",
                BuildOptionalSegment(profile.AdditionalArguments),
                BuildOptionalSegment(profile.UhdParameters),
                $"-o \"{{output}}.{profile.OutputContainer}\"");

            return JoinStagePreview(pass1, pass2);
        }

        return JoinArguments(
            @"vspipe -c y4m ""{input}.vpy"" - |",
            "x265",
            $"--preset {preset}",
            BuildRateControlArguments(profile.Kind, profile),
            BuildOptionalArgument("--tune", tune),
            BuildOptionalArgument("--profile", profileValue),
            "--y4m --input -",
            BuildOptionalSegment(profile.AdditionalArguments),
            BuildOptionalSegment(profile.UhdParameters),
            $"-o \"{{output}}.{profile.OutputContainer}\"");
    }

    private static string BuildSvtAv1Preview(EncodingProfile profile)
    {
        var preset = EncoderArgumentValueNormalizer.NormalizePresetForCli(profile.Kind, profile.Preset);
        var tune = EncoderArgumentValueNormalizer.NormalizeTuneForCli(profile.Kind, profile.Tune);
        var profileValue = EncoderArgumentValueNormalizer.NormalizeProfileForCli(profile.Kind, profile.Profile);
        var statsFile = "\"{output}.svt-av1_2pass.log\"";

        if (profile.RateControl == RateControlMode.TwoPass)
        {
            var pass1 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "SvtAv1EncApp",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 1, passCount: 2),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--progress 2",
                "--width {width}",
                "--height {height}",
                "--frames {frames}",
                "--input-depth 10",
                "--input -",
                BuildOptionalSegment(profile.AdditionalArguments),
                "-b \"NUL\"");

            var pass2 = JoinArguments(
                @"vspipe -c y4m ""{input}.vpy"" - |",
                "SvtAv1EncApp",
                $"--preset {preset}",
                BuildRateControlArguments(profile.Kind, profile, statsFile, passIndex: 2, passCount: 2),
                BuildOptionalArgument("--tune", tune),
                BuildOptionalArgument("--profile", profileValue),
                "--progress 2",
                "--width {width}",
                "--height {height}",
                "--frames {frames}",
                "--input-depth 10",
                "--input -",
                BuildOptionalSegment(profile.AdditionalArguments),
                $"-b \"{{output}}.{profile.OutputContainer}\"");

            return JoinStagePreview(pass1, pass2);
        }

        return JoinArguments(
            @"vspipe -c y4m ""{input}.vpy"" - |",
            "SvtAv1EncApp",
            $"--preset {preset}",
            BuildRateControlArguments(profile.Kind, profile),
            BuildOptionalArgument("--tune", tune),
            BuildOptionalArgument("--profile", profileValue),
            "--progress 2",
            "--width {width}",
            "--height {height}",
            "--frames {frames}",
            "--input-depth 10",
            "--input -",
            BuildOptionalSegment(profile.AdditionalArguments),
            $"-b \"{{output}}.{profile.OutputContainer}\"");
    }

    private static string BuildRateControlArguments(EncoderKind kind, EncodingProfile profile)
    {
        return BuildRateControlArguments(kind, profile, null, null, null);
    }

    private static string BuildRateControlArguments(
        EncoderKind kind,
        EncodingProfile profile,
        string? statsFile,
        int? passIndex,
        int? passCount)
    {
        return profile.RateControl switch
        {
            RateControlMode.Crf => kind == EncoderKind.SvtAv1
                ? $"--rc 0 --crf {FormatNumber(profile.Quality)}"
                : $"--crf {FormatNumber(profile.Quality)}",
            RateControlMode.Cq or RateControlMode.Qp => kind == EncoderKind.SvtAv1
                ? $"--rc 0 --qp {FormatNumber(profile.Quality)}"
                : $"--qp {FormatNumber(profile.Quality)}",
            RateControlMode.Abr or RateControlMode.Vbr => kind == EncoderKind.SvtAv1
                ? $"--rc 1 --tbr {profile.Bitrate ?? 3500}"
                : $"--bitrate {profile.Bitrate ?? 3500}",
            RateControlMode.TwoPass => kind switch
            {
                EncoderKind.X264 or EncoderKind.X265 => JoinArguments(
                    $"--bitrate {profile.Bitrate ?? 3500}",
                    passIndex.HasValue ? $"--pass {passIndex.Value}" : "--pass 1",
                    BuildOptionalArgument("--stats", statsFile ?? string.Empty)),
                EncoderKind.SvtAv1 => JoinArguments(
                    $"--rc 1 --tbr {profile.Bitrate ?? 3500}",
                    passIndex.HasValue ? $"--pass {passIndex.Value}" : "--pass 1",
                    BuildOptionalArgument("--stats", statsFile ?? string.Empty)),
                _ => string.Empty
            },
            _ => string.Empty
        };
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string BuildOptionalArgument(string option, string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : $"{option} {value}";
    }

    private static string BuildOptionalSegment(string segment)
    {
        return string.IsNullOrWhiteSpace(segment) ? string.Empty : segment.Trim();
    }

    private static string JoinArguments(params string[] arguments)
    {
        return string.Join(" ", arguments.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string JoinStagePreview(string pass1, string pass2)
    {
        return $"[Pass 1/2]{Environment.NewLine}{pass1}{Environment.NewLine}{Environment.NewLine}[Pass 2/2]{Environment.NewLine}{pass2}";
    }
}
