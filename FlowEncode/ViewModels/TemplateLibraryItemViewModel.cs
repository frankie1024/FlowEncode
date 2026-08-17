using CommunityToolkit.Mvvm.ComponentModel;
using FlowEncode.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowEncode.ViewModels;

public sealed class TemplateLibraryItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isModified;

    public TemplateLibraryItemViewModel(
        string key,
        string name,
        string sourceLabel,
        string encoderAndQualityText,
        string metaText,
        string templateId,
        bool isPinned,
        string pinActionLabel,
        string deleteActionLabel,
        SavedTemplate? userTemplate)
    {
        Key = key;
        Name = name;
        SourceLabel = sourceLabel;
        EncoderAndQualityText = encoderAndQualityText;
        MetaText = metaText;
        TemplateId = templateId;
        IsPinned = isPinned;
        PinActionLabel = pinActionLabel;
        DeleteActionLabel = deleteActionLabel;
        UserTemplate = userTemplate;
    }

    public string Key { get; }

    public string Name { get; }

    public string SourceLabel { get; }

    public string EncoderAndQualityText { get; }

    public string MetaText { get; }

    public string TemplateId { get; }

    public bool IsPinned { get; }

    public string PinActionLabel { get; }

    public string DeleteActionLabel { get; }

    public SavedTemplate? UserTemplate { get; }

    public bool CanDelete => UserTemplate is not null && !IsPinned;

    public Visibility DeleteVisibility => CanDelete ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PinVisibility => UserTemplate is not null ? Visibility.Visible : Visibility.Collapsed;

    public Symbol PinSymbol => IsPinned ? Symbol.UnPin : Symbol.Pin;

    public Visibility MetaVisibility => string.IsNullOrWhiteSpace(MetaText) ? Visibility.Collapsed : Visibility.Visible;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            SetProperty(ref _isSelected, value);
        }
    }

    public bool IsModified
    {
        get => _isModified;
        set
        {
            if (SetProperty(ref _isModified, value))
            {
                OnPropertyChanged(nameof(ModifiedVisibility));
            }
        }
    }

    public Visibility ModifiedVisibility => IsModified ? Visibility.Visible : Visibility.Collapsed;
}
