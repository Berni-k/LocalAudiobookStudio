using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace LocalAudiobookStudio.App.Services;

public sealed record FileFilter(string Name, params string[] Patterns);

/// <summary>
/// Message boxes, prompts and file pickers. Built in code (not XAML) because they are tiny.
/// File pickers use Avalonia's StorageProvider, which maps to the native dialogs of each OS.
/// </summary>
public static class Dialogs
{
    public static Window? Owner
    {
        get
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime d) return null;
            return d.Windows.LastOrDefault(w => w.IsActive) ?? d.MainWindow;
        }
    }

    public static Task ShowError(string title, string message) => Choose(title, message, "OK");
    public static Task ShowInfo(string title, string message) => Choose(title, message, "OK");

    public static async Task<bool> Confirm(string title, string message, string ok = "OK", string cancel = "Cancel") =>
        await Choose(title, message, ok, cancel) == ok;

    /// <summary>Shows a message with buttons; returns the clicked label (null if closed). The first button is the default.</summary>
    public static async Task<string?> Choose(string title, string message, params string[] buttons)
    {
        var owner = Owner;
        string? result = null;
        var window = NewDialog(title, 480);

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            var label = buttons[i];
            var b = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = i == 0 };
            if (i == 0) b.Classes.Add("primary");
            if (i == buttons.Length - 1 && buttons.Length > 1) b.IsCancel = true;
            b.Click += (_, _) => { result = label; window.Close(); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        window.Content = panel;
        await ShowAsync(window, owner);
        return result;
    }

    /// <summary>Single-line text prompt; returns null when cancelled.</summary>
    public static async Task<string?> Prompt(string title, string label, string initial = "")
    {
        var owner = Owner;
        string? result = null;
        var window = NewDialog(title, 420);
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { result = box.Text; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await ShowAsync(window, owner);
        return result;
    }

    private static Window NewDialog(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
    };

    private static async Task ShowAsync(Window window, Window? owner)
    {
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };
        if (owner != null) await window.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            window.Closed += (_, _) => tcs.TrySetResult();
            window.Show();
            await tcs.Task;
        }
    }

    // ------------------------------------------------------------------ files

    private static IStorageProvider? Storage => (Owner as TopLevel)?.StorageProvider;

    private static List<FilePickerFileType> ToTypes(IEnumerable<FileFilter> filters) =>
        filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList();

    public static async Task<string?> OpenFile(string title, params FileFilter[] filters) =>
        (await OpenFiles(title, false, filters)).FirstOrDefault();

    public static async Task<string[]> OpenFiles(string title, bool multiple, params FileFilter[] filters)
    {
        var sp = Storage;
        if (sp == null) return [];
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = filters.Length > 0 ? ToTypes(filters.Append(new FileFilter("All files", "*"))) : null,
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToArray();
    }

    public static async Task<string?> SaveFile(string title, string suggestedName, string defaultExtension, params FileFilter[] filters)
    {
        var sp = Storage;
        if (sp == null) return null;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExtension,
            FileTypeChoices = ToTypes(filters),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public static async Task<string?> PickFolder(string title)
    {
        var sp = Storage;
        if (sp == null) return null;
        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Opens a folder (or selects a file) in the system file manager.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                else Process.Start("explorer.exe", $"\"{path}\"");
            }
            else
            {
                var target = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
                Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false });
            }
        }
        catch
        {
            // No file manager available.
        }
    }
}
