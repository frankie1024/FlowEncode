using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace FlowEncode;

internal enum MainWindowShellSectionTransitionKind
{
    None,
    Entrance,
    DashboardForward,
    DashboardBackward
}

internal sealed class MainWindowShellSectionController
{
    private const double EntranceOffsetX = 24d;
    private const double DashboardForwardOffsetY = 32d;
    private const double DashboardBackwardOffsetY = -24d;

    private readonly Panel _host;
    private readonly Func<string, UserControl> _controlFactory;
    private readonly Action<string>? _sectionLoadedCallback;
    private readonly Dictionary<string, UserControl> _controls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<bool>> _loadedCompletionSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _materializedSections = new(StringComparer.Ordinal);

    public MainWindowShellSectionController(
        Panel host,
        Func<string, UserControl> controlFactory,
        Action<string>? sectionLoadedCallback = null)
    {
        _host = host;
        _controlFactory = controlFactory;
        _sectionLoadedCallback = sectionLoadedCallback;
    }

    public T? GetControl<T>(string tag) where T : UserControl
    {
        return _controls.TryGetValue(MainShellSections.Normalize(tag), out var control)
            ? control as T
            : null;
    }

    public UserControl? GetControl(string tag)
    {
        return _controls.TryGetValue(MainShellSections.Normalize(tag), out var control)
            ? control
            : null;
    }

    public UserControl EnsureControl(string tag)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        if (_controls.TryGetValue(normalizedTag, out var existingControl))
        {
            return existingControl;
        }

        var control = _controlFactory(normalizedTag);
        control.Visibility = Visibility.Collapsed;
        RoutedEventHandler? loadedHandler = null;
        loadedHandler = (_, _) =>
        {
            control.Loaded -= loadedHandler;
            OnControlLoaded(normalizedTag);
        };
        control.Loaded += loadedHandler;
        _controls[normalizedTag] = control;
        GetLoadedCompletionSource(normalizedTag);
        return control;
    }

    public bool IsMaterialized(string tag)
    {
        return _materializedSections.Contains(MainShellSections.Normalize(tag));
    }

    public async Task<bool> WaitForMaterializedAsync(string tag)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        EnsureControl(normalizedTag);
        if (_materializedSections.Contains(normalizedTag))
        {
            return true;
        }

        return await GetLoadedCompletionSource(normalizedTag).Task;
    }

    public void Show(string tag, MainWindowShellSectionTransitionKind transitionKind = MainWindowShellSectionTransitionKind.None)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        var activeControl = EnsureControl(normalizedTag);
        var previousControl = _host.Children.OfType<UserControl>().FirstOrDefault();
        var previousTag = _controls
            .FirstOrDefault(entry => ReferenceEquals(entry.Value, previousControl))
            .Key;

        foreach (var sectionEntry in _controls)
        {
            sectionEntry.Value.Visibility = string.Equals(sectionEntry.Key, normalizedTag, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (ReferenceEquals(previousControl, activeControl))
        {
            activeControl.Transitions = null;
            return;
        }

        if (previousControl is not null)
        {
            _host.Children.Remove(previousControl);
        }

        activeControl.Transitions = BuildTransitions(previousTag, normalizedTag, transitionKind);
        if (!_host.Children.Contains(activeControl))
        {
            _host.Children.Add(activeControl);
        }
    }

    public string[] GetSectionTagsSnapshot()
    {
        var tags = new string[_controls.Count];
        _controls.Keys.CopyTo(tags, 0);
        return tags;
    }

    public void Release(string tag)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        if (!_controls.Remove(normalizedTag, out var control))
        {
            return;
        }

        _materializedSections.Remove(normalizedTag);
        if (_loadedCompletionSources.Remove(normalizedTag, out var completionSource))
        {
            completionSource.TrySetResult(false);
        }

        if (_host.Children.Contains(control))
        {
            _host.Children.Remove(control);
        }

        if (control is IDisposable disposableControl)
        {
            disposableControl.Dispose();
        }
    }

    public void ReleaseAll()
    {
        foreach (var tag in _controls.Keys.ToArray())
        {
            Release(tag);
        }
    }

    private TaskCompletionSource<bool> GetLoadedCompletionSource(string tag)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        if (_loadedCompletionSources.TryGetValue(normalizedTag, out var completionSource))
        {
            return completionSource;
        }

        completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_materializedSections.Contains(normalizedTag))
        {
            completionSource.TrySetResult(true);
        }

        _loadedCompletionSources[normalizedTag] = completionSource;
        return completionSource;
    }

    private void OnControlLoaded(string tag)
    {
        var normalizedTag = MainShellSections.Normalize(tag);
        _materializedSections.Add(normalizedTag);
        GetLoadedCompletionSource(normalizedTag).TrySetResult(true);
        _sectionLoadedCallback?.Invoke(normalizedTag);
    }

    private static TransitionCollection? BuildTransitions(
        string? previousTag,
        string normalizedTag,
        MainWindowShellSectionTransitionKind transitionKind)
    {
        if (string.Equals(previousTag, normalizedTag, StringComparison.Ordinal))
        {
            return null;
        }

        var transitions = new TransitionCollection();
        var entranceTransition = new EntranceThemeTransition
        {
            IsStaggeringEnabled = false
        };

        switch (transitionKind)
        {
            case MainWindowShellSectionTransitionKind.Entrance:
                entranceTransition.FromHorizontalOffset = EntranceOffsetX;
                break;
            case MainWindowShellSectionTransitionKind.DashboardForward:
                entranceTransition.FromVerticalOffset = DashboardForwardOffsetY;
                break;
            case MainWindowShellSectionTransitionKind.DashboardBackward:
                entranceTransition.FromVerticalOffset = DashboardBackwardOffsetY;
                break;
            default:
                return null;
        }

        transitions.Add(entranceTransition);
        return transitions;
    }
}
