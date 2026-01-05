using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Mostlylucid.ImageSummarizer.Desktop.ViewModels;

namespace Mostlylucid.ImageSummarizer.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // Handle drag-drop
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Only allow file drops
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;

        var files = e.Data.GetFiles();
        if (files == null) return;

        var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
        var imagePaths = new System.Collections.Generic.List<string>();

        foreach (var file in files)
        {
            try
            {
                // Try to get local path - works for local files
                var path = file.Path.LocalPath;
                if (!string.IsNullOrEmpty(path) &&
                    System.IO.File.Exists(path) &&
                    imageExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    imagePaths.Add(path);
                }
            }
            catch
            {
                // Skip files we can't access
            }
        }

        if (imagePaths.Count > 0)
        {
            await _viewModel.HandleDropAsync(imagePaths.ToArray());
        }
    }

    // Handle command-line argument for shell integration
    public async void LoadFromArgs(string[] args)
    {
        if (args.Length > 0 && System.IO.File.Exists(args[0]))
        {
            await _viewModel.LoadImageAsync(args[0]);
        }
    }
}
