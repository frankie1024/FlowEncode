using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace FlowEncode.ViewModels;

public abstract class ModuleViewModelBase : CommunityToolkit.Mvvm.ComponentModel.ObservableObject, IDisposable
{
    private static readonly ConcurrentDictionary<Type, HashSet<string>> ForwardedPropertyNamesCache = new();
    private readonly HashSet<string> _forwardedPropertyNames;
    private readonly List<IDisposable> _ownedDisposables = [];
    private bool _isDisposed;

    protected ModuleViewModelBase(MainWindowViewModel owner)
    {
        Owner = owner;
        _forwardedPropertyNames = ForwardedPropertyNamesCache.GetOrAdd(
            GetType(),
            static type => type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.GetMethod is not null)
                .Select(static property => property.Name)
                .ToHashSet(StringComparer.Ordinal));

        owner.PropertyChanged += Owner_PropertyChanged;
    }

    protected MainWindowViewModel Owner { get; }

    protected static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();

        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    protected T TrackDisposable<T>(T value)
        where T : IDisposable
    {
        _ownedDisposables.Add(value);
        return value;
    }

    public virtual void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Owner.PropertyChanged -= Owner_PropertyChanged;

        foreach (var disposable in _ownedDisposables)
        {
            disposable.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void Owner_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (string.IsNullOrEmpty(e.PropertyName))
        {
            foreach (var propertyName in _forwardedPropertyNames)
            {
                OnPropertyChanged(propertyName);
            }

            return;
        }

        if (_forwardedPropertyNames.Contains(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }
}
