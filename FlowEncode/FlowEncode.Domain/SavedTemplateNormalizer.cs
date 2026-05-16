namespace FlowEncode.Domain;

public static class SavedTemplateNormalizer
{
    public static SavedTemplate Normalize(SavedTemplate template, DateTimeOffset fallbackUpdatedAt)
    {
        ArgumentNullException.ThrowIfNull(template);

        var normalizedName = template.Name.Trim();
        var normalizedNotes = template.Notes?.Trim() ?? string.Empty;
        var normalizedUpdatedAt = template.UpdatedAt == default
            ? fallbackUpdatedAt
            : template.UpdatedAt;

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
}
