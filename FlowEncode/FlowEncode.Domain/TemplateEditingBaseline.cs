namespace FlowEncode.Domain;

public sealed record TemplateEditingBaseline(
    string Name,
    string Notes,
    EncodingProfile? Profile)
{
    public static TemplateEditingBaseline Empty { get; } = new(string.Empty, string.Empty, null);

    public static TemplateEditingBaseline Capture(
        string? templateName,
        string? templateNotes,
        EncodingProfile? profile)
    {
        return new TemplateEditingBaseline(
            NormalizeText(templateName),
            NormalizeText(templateNotes),
            NormalizeProfile(profile));
    }

    public bool Matches(
        string? currentName,
        string? currentNotes,
        EncodingProfile? currentProfile)
    {
        return string.Equals(NormalizeText(currentName), Name, StringComparison.Ordinal)
            && string.Equals(NormalizeText(currentNotes), Notes, StringComparison.Ordinal)
            && EqualityComparer<EncodingProfile?>.Default.Equals(NormalizeProfile(currentProfile), Profile);
    }

    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    public static EncodingProfile? NormalizeProfile(EncodingProfile? profile)
    {
        if (profile is null)
        {
            return null;
        }

        return profile with
        {
            Name = NormalizeText(profile.Name),
            Description = NormalizeText(profile.Description),
            AdditionalArguments = NormalizeText(profile.AdditionalArguments),
            UhdParameters = NormalizeText(profile.UhdParameters)
        };
    }
}
