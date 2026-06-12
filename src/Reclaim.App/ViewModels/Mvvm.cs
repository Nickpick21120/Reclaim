using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Reclaim.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A command that receives a typed parameter — needed for context-menu
/// actions, which live inside Style setters where Click handlers can't resolve
/// against code-behind but command bindings work fine.</summary>
public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke((T?)parameter) ?? true;
    public void Execute(object? parameter) => execute((T?)parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>One clickable segment in the focus breadcrumb.</summary>
public sealed class BreadcrumbItem(Reclaim.Core.Scanning.FileSystemNode node, bool isLast)
{
    public Reclaim.Core.Scanning.FileSystemNode Node { get; } = node;
    public bool IsLast { get; } = isLast;

    /// <summary>Drive roots show as the full path ("C:\"); deeper nodes show
    /// just their folder name to keep the trail compact.</summary>
    public string Label => Node.Parent is null ? Node.FullPath : Node.Name;
}
