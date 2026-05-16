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

        if (!string.Equals(Path.GetExtension(filePath), SavedTemplateFilePathPlanner.TemplateFileExtension, StringComparison.OrdinalIgnoreCase))
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

        var normalizedTemplate = SavedTemplateNormalizer.Normalize(template, DateTimeOffset.Now);
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
        var targetPath = SavedTemplateFilePathPlanner.BuildAvailableFilePath(
            _paths.WorkspaceTemplatesRootPath,
            normalizedName,
            occupiedPaths);
        var template = SavedTemplateNormalizer.Normalize(
            new SavedTemplate(
                resolvedTemplateId,
                normalizedName,
                normalizedNotes,
                profile,
                DateTimeOffset.Now,
                isPinned),
            DateTimeOffset.Now);

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

        var updatedTemplate = SavedTemplateNormalizer.Normalize(entry.Template with
        {
            UpdatedAt = DateTimeOffset.Now,
            IsPinned = isPinned
        }, DateTimeOffset.Now);

        await WriteTemplateFileAsync(updatedTemplate, entry.FilePath, cancellationToken);
        return updatedTemplate;
    }

    public Task<CommandPreview> BuildPreviewAsync(EncodingProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EncodingCommandPreviewBuilder.Build(profile));
    }

    private async Task<List<TemplateFileEntry>> LoadTemplateEntriesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.WorkspaceTemplatesRootPath);

        var entries = new List<TemplateFileEntry>();
        foreach (var filePath in Directory.EnumerateFiles(_paths.WorkspaceTemplatesRootPath, $"*{SavedTemplateFilePathPlanner.TemplateFileExtension}", SearchOption.TopDirectoryOnly)
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
        var normalizedTemplate = SavedTemplateNormalizer.Normalize(template, DateTimeOffset.Now);
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

        return SavedTemplateNormalizer.Normalize(
            new SavedTemplate(
                string.IsNullOrWhiteSpace(document.Id) ? BuildStableTemplateId(filePath) : document.Id.Trim(),
                document.Name.Trim(),
                document.Notes?.Trim() ?? string.Empty,
                document.Profile,
                ResolveUpdatedAt(document),
                document.IsPinned ?? false),
            DateTimeOffset.Now);
    }

    private static DateTimeOffset ResolveUpdatedAt(TemplateExchangeDocument document)
    {
        var updatedAt = document.UpdatedAt ?? document.ExportedAt ?? DateTimeOffset.Now;
        return updatedAt == default ? DateTimeOffset.Now : updatedAt;
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

}
