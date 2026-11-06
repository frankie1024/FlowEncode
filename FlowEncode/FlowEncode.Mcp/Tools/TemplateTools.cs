using System.ComponentModel;
using FlowEncode.Application;
using FlowEncode.Domain;
using FlowEncode.Infrastructure.Automation;
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
        DesktopBridge desktop,
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
            if (existing.IsPinned)
            {
                throw new McpException($"Template '{existing.Name}' is pinned. Call set_template_pinned with isPinned=false before changing it.");
            }
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
        await desktop.NotifyAsync(DesktopAutomationMethods.TemplatesReload, null, cancellationToken);
        return TemplateView.From(saved);
    }

    [McpServerTool(Name = "set_template_pinned", Destructive = false, Idempotent = true)]
    [Description("Pins or unpins a template. Pinned templates stay at the top of the library and are locked against "
        + "changes and deletion until unpinned.")]
    public static async Task<TemplateView> SetTemplatePinned(
        IProfileLibraryService profileLibrary,
        DesktopBridge desktop,
        [Description("Template id or name.")] string templateId,
        [Description("true to pin, false to unpin.")] bool isPinned,
        CancellationToken cancellationToken = default)
    {
        var template = await RequireTemplateAsync(profileLibrary, templateId, cancellationToken);
        var updated = await McpToolGuard.RunAsync(() => profileLibrary.SetTemplatePinnedAsync(template.Id, isPinned, cancellationToken));
        await desktop.NotifyAsync(DesktopAutomationMethods.TemplatesReload, null, cancellationToken);
        return TemplateView.From(updated);
    }

    [McpServerTool(Name = "delete_template", Destructive = true, Idempotent = true)]
    [Description("Deletes a saved template from the library. Pinned templates must be unpinned first.")]
    public static async Task<IReadOnlyList<TemplateView>> DeleteTemplate(
        IProfileLibraryService profileLibrary,
        DesktopBridge desktop,
        [Description("Template id or name.")] string templateId,
        CancellationToken cancellationToken = default)
    {
        var template = await RequireTemplateAsync(profileLibrary, templateId, cancellationToken);
        if (template.IsPinned)
        {
            throw new McpException($"Template '{template.Name}' is pinned. Call set_template_pinned with isPinned=false first.");
        }

        await McpToolGuard.RunAsync(async () =>
        {
            await profileLibrary.DeleteTemplateAsync(template.Id, cancellationToken);
            return true;
        });
        await desktop.NotifyAsync(DesktopAutomationMethods.TemplatesReload, null, cancellationToken);
        return await ListTemplates(profileLibrary, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "export_template", Destructive = false)]
    [Description("Exports a template to a .profile file that can be shared and imported elsewhere.")]
    public static async Task<TemplateExportResult> ExportTemplate(
        IProfileLibraryService profileLibrary,
        [Description("Template id or name.")] string templateId,
        [Description("Absolute path of the .profile file to write.")] string filePath,
        [Description("Replace an existing file. Defaults to false.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var template = await RequireTemplateAsync(profileLibrary, templateId, cancellationToken);
        var fullPath = McpPaths.RequireAbsolute(filePath, nameof(filePath));
        if (!string.Equals(Path.GetExtension(fullPath), SavedTemplateFilePathPlanner.TemplateFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            fullPath += SavedTemplateFilePathPlanner.TemplateFileExtension;
        }

        if (!overwrite && File.Exists(fullPath))
        {
            throw new McpException($"{fullPath} already exists. Pass overwrite=true to replace it.");
        }

        await McpToolGuard.RunAsync(async () =>
        {
            await profileLibrary.ExportTemplateAsync(template, fullPath, cancellationToken);
            return true;
        });
        return new TemplateExportResult(fullPath, TemplateView.From(template));
    }

    [McpServerTool(Name = "import_template", Destructive = false)]
    [Description("Imports a .profile template file into the library. A template with the same name is replaced unless it is pinned.")]
    public static async Task<TemplateView> ImportTemplate(
        IProfileLibraryService profileLibrary,
        DesktopBridge desktop,
        [Description("Absolute path of the .profile file.")] string filePath,
        [Description("Name to save it under. Defaults to the name stored in the file.")] string? name = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = McpPaths.RequireExistingFile(filePath, nameof(filePath));
        var imported = await McpToolGuard.RunAsync(() => profileLibrary.ReadTemplateAsync(fullPath, cancellationToken));
        var templateName = string.IsNullOrWhiteSpace(name) ? imported.Name?.Trim() : name.Trim();
        if (string.IsNullOrWhiteSpace(templateName))
        {
            throw new McpException("The template file has no name; pass 'name'.");
        }

        var notes = imported.Notes?.Trim() ?? string.Empty;
        var saved = await McpToolGuard.RunAsync(() => profileLibrary.SaveTemplateAsync(
            templateName,
            notes,
            imported.Profile with { Name = templateName, Description = notes },
            templateId: null,
            isPinned: imported.IsPinned,
            cancellationToken));
        await desktop.NotifyAsync(DesktopAutomationMethods.TemplatesReload, null, cancellationToken);
        return TemplateView.From(saved);
    }

    private static async Task<SavedTemplate> RequireTemplateAsync(
        IProfileLibraryService profileLibrary,
        string templateId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new McpException("'templateId' is required.");
        }

        return await EncodingProfileResolver.FindTemplateAsync(profileLibrary, templateId, cancellationToken)
            ?? throw new McpException($"Template '{templateId}' was not found. Call list_templates to see the available ids.");
    }
}

public sealed record TemplateExportResult(string FilePath, TemplateView Template);

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
