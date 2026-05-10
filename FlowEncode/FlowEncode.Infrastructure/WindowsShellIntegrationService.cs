using System.Runtime.Versioning;
using System.Text;
using FlowEncode.Application;
using FlowEncode.Domain;

namespace FlowEncode.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class WindowsShellIntegrationService : IVapourSynthShellIntegrationService
{
    private const string ShellNewKey = @"Software\Classes\.vpy\ShellNew";
    private const string ExtensionKey = @"Software\Classes\.vpy";
    private const string ProgIdKey = @"Software\Classes\VapourSynthScript";
    private const string ChineseMenuText = "VapourSynth 视频脚本";
    private const string EnglishMenuText = "VapourSynth Script";
    private const string TemplateFileName = "VapourSynthScript.vpy";

    private static readonly string TemplateContent = string.Empty;

    private readonly LocalAppPaths _appPaths;
    private readonly IAppSettingsService _settingsService;

    public WindowsShellIntegrationService(LocalAppPaths appPaths, IAppSettingsService settingsService)
    {
        _appPaths = appPaths;
        _settingsService = settingsService;
    }

    public void RegisterNewVpyFileMenu()
    {
        try
        {
            // Write template file to app data directory
            var templateDir = Path.Combine(_appPaths.DataRootPath, "Templates");
            Directory.CreateDirectory(templateDir);
            var templatePath = Path.Combine(templateDir, TemplateFileName);
            File.WriteAllText(templatePath, TemplateContent, new UTF8Encoding(false));

            using (var progIdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ProgIdKey))
            {
                progIdKey.SetValue("", GetMenuText());
            }

            using var shellNewKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ShellNewKey);
            shellNewKey.SetValue("", string.Empty);
            shellNewKey.SetValue("FileName", templatePath, Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Write(
                _appPaths,
                nameof(WindowsShellIntegrationService),
                $"Failed to register .vpy ShellNew. {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void UnregisterNewVpyFileMenu()
    {
        try
        {
            using (var extKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ExtensionKey, writable: true))
            {
                extKey?.DeleteSubKey("ShellNew", throwOnMissingSubKey: false);
            }
        }
        catch (Exception ex)
        {
            AppDiagnosticsLog.Write(
                _appPaths,
                nameof(WindowsShellIntegrationService),
                $"Failed to unregister .vpy ShellNew. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string GetMenuText()
    {
        try
        {
            return _settingsService.Load().Language == AppLanguage.English
                ? EnglishMenuText
                : ChineseMenuText;
        }
        catch
        {
            return ChineseMenuText;
        }
    }
}
