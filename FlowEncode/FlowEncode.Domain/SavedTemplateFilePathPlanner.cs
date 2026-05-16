namespace FlowEncode.Domain;

public static class SavedTemplateFilePathPlanner
{
    public const string TemplateFileExtension = ".profile";
    public const string DefaultTemplateFileName = "template";

    public static string BuildAvailableFilePath(
        string templatesRootPath,
        string templateName,
        ISet<string> occupiedPaths)
    {
        var sanitizedName = SanitizeFileName(templateName);
        var candidate = Path.Combine(templatesRootPath, $"{sanitizedName}{TemplateFileExtension}");
        if (!occupiedPaths.Contains(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; suffix < short.MaxValue; suffix++)
        {
            candidate = Path.Combine(templatesRootPath, $"{sanitizedName}-{suffix}{TemplateFileExtension}");
            if (!occupiedPaths.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a template file name.");
    }

    public static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(sanitized) ? DefaultTemplateFileName : sanitized;
    }
}
