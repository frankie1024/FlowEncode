using FlowEncode.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.ViewModels;

public sealed record TemplateLibraryItemViewModel(
    string Key,
    string Name,
    string SourceLabel,
    string EncoderAndQualityText,
    string MetaText,
    string TemplateId,
    bool IsPinned,
    string PinActionLabel,
    SavedTemplate? UserTemplate)
{
    public bool CanDelete => UserTemplate is not null && !IsPinned;

    public Visibility DeleteVisibility => CanDelete ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PinVisibility => UserTemplate is not null ? Visibility.Visible : Visibility.Collapsed;

    public Symbol PinSymbol => IsPinned ? Symbol.UnPin : Symbol.Pin;

    public Visibility MetaVisibility => string.IsNullOrWhiteSpace(MetaText) ? Visibility.Collapsed : Visibility.Visible;
}
