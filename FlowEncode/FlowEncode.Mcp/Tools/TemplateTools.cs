using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FlowEncode.Mcp.Tools;

[McpServerToolType]
public static class TemplateTools
{
    [McpServerTool(Name = "list_templates", ReadOnly = true, Idempotent = true)]
    [Description("Lists the user's saved FlowEncode encoding templates (.profile) with their full parameters. "
        + "Pass a template's id or name as 'templateId' to the encoding tools.")]
    public static async Task<IReadOnlyList<TemplateView>> ListTemplates(
        IProfileLibraryService profileLibrary,
        [Description("Only return templates for this encoder: x264, x265 or svt-av1.")] string? encoder = null,
        CancellationToken cancellationToken = default)
    {
        EncoderKind? kind = string.IsNullOrWhiteSpace(encoder) ? null : EncodingProfileResolver.ParseEncoder(encoder);
        var templates = await McpToolGuard.RunAsync(() => profileLibrary.GetUserTemplatesAsync(cancellationToken));

        return templates
            .Where(template => kind is null || template.Profile.Kind == kind)
            .OrderByDescending(static template => template.IsPinned)
            .ThenByDescending(static template => template.UpdatedAt)
            .Select(TemplateView.From)
            .ToList();
    }

    [McpServerTool(Name = "get_default_profile", ReadOnly = true, Idempotent = true)]
    [Description("Returns FlowEncode's built-in default parameters for an encoder, which start_encoding_job uses "
        + "when no template is given.")]
    public static ProfileView GetDefaultProfile(
        [Description("x264, x265 or svt-av1.")] string encoder)
    {
        return ProfileView.From(DefaultEncodingProfiles.GetDefault(EncodingProfileResolver.ParseEncoder(encoder)));
    }

    [McpServerTool(Name = "save_template", Destructive = false)]
    [Description("Saves encoding parameters as a FlowEncode template so they appear in the desktop app's template library. "
        + "Pass an existing templateId to overwrite that template.")]
    public static async Task<TemplateView> SaveTemplate(
        IProfileLibraryService profileLibrary,
        [Description("Template name shown in the template library.")] string name,
        [Description("Encoder: x264, x265 or svt-av1. Optional when overwriting an existing template.")] string? encoder = null,
        [Description("Parameters applied on top of the encoder default (or the existing template when overwriting).")] EncodingProfileOptions? overrides = null,
        [Description("Free-form notes.")] string? notes = null,
        [Description("Id of an existing template to overwrite.")] string? templateId = null,
        [Description("Pin the template to the top of the library.")] bool isPinned = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new McpException("'name' is required.");
        }

        string? existingId = null;
        if (!string.IsNullOrWhiteSpace(templateId))
        {
            var existing = await EncodingProfileResolver.FindTemplateAsync(profileLibrary, templateId, cancellationToken)
                ?? throw new McpException($"Template '{templateId}' was not found.");
            existingId = existing.Id;
        }

        var profile = await EncodingProfileResolver.ResolveAsync(profileLibrary, existingId, encoder, overrides, cancellationToken);
        profile = profile with { Name = name.Trim(), Description = notes?.Trim() ?? profile.Description };

        var saved = await McpToolGuard.RunAsync(() => profileLibrary.SaveTemplateAsync(
            name.Trim(),
            notes?.Trim() ?? string.Empty,
            profile,
            existingId,
            isPinned,
            cancellationToken));
        return TemplateView.From(saved);
    }
}

public sealed record TemplateView(
    string Id,
    string Name,
    string Notes,
    bool IsPinned,
    DateTimeOffset UpdatedAt,
    ProfileView Profile)
{
    public static TemplateView From(SavedTemplate template) => new(
        template.Id,
        template.Name,
        template.Notes,
        template.IsPinned,
        template.UpdatedAt,
        ProfileView.From(template.Profile));
}
