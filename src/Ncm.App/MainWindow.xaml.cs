using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Ncm.Core;
using Ncm.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace Ncm.App;

public sealed partial class MainWindow : Window
{
    private const double MinimumWindowWidth = 680;
    private const double MinimumWindowHeight = 680;
    private readonly NcmMediaService _media = new();
    private readonly Dictionary<string, ConversionRow> _rowsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ConversionRow> _rows = [];
    private readonly HashSet<Task> _activeWork = [];
    private readonly CancellationTokenSource _windowCancellation = new();
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _coverValidationCancellation;
    private OutputPlan? _currentPlan;
    private string? _batchCoverPath;
    private string? _planError;
    private bool _initialized;
    private bool _busy;
    private int _pendingAdds;
    private int _layoutCategory = -1;
    private bool _layoutAdvanced;
    private bool _layoutCompactHeight;
    private bool _updatingSelection;
    private bool _draggingRows;
    private string[]? _orderBeforeDrag;
    private bool _startPromptOpen;
    private bool _windowClosed;
    private bool _closingRequested;
    private bool _closeReady;
    private XamlRoot? _windowXamlRoot;

    public MainWindow()
    {
        InitializeComponent();
        Title = "NCM 转换器";
        ModeSelector.SelectedItem = SimpleModeItem;
        CompactViewSelector.SelectedItem = CompactFilesItem;
        WindowBackgroundGrid.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(Window_DragOver), true);
        WindowBackgroundGrid.AddHandler(UIElement.DragOverEvent, new DragEventHandler(Window_DragOver), true);
        WindowBackgroundGrid.AddHandler(UIElement.DropEvent, new DragEventHandler(Window_Drop), true);
        _initialized = true;
        UpdateMode();
        WindowBackgroundGrid.SizeChanged += (_, e) => RootGrid.Width = Math.Min(1280, e.NewSize.Width);
        WindowBackgroundGrid.Loaded += WindowBackgroundGrid_Loaded;
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
        SetInitialWindowBounds();
        UpdateListState();
        UpdateStartState();
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _windowCancellation.Cancel();
            _runCancellation?.Cancel();
            _previewCancellation?.Cancel();
            _coverValidationCancellation?.Cancel();
            if (_windowXamlRoot is not null)
            {
                _windowXamlRoot.Changed -= WindowXamlRoot_Changed;
            }

            AppWindow.Changed -= AppWindow_Changed;
            AppWindow.Closing -= AppWindow_Closing;
            _windowCancellation.Dispose();
        };
    }

    public ObservableCollection<ConversionRow> Rows => _rows;

    private bool IsAdvanced => ModeSelector.SelectedItem == AdvancedModeItem;

    private bool IsSelectionMode => FilesListView.SelectionMode == ListViewSelectionMode.Multiple;

    private async Task TrackWorkAsync(Task work)
    {
        _activeWork.Add(work);
        try
        {
            await work;
        }
        finally
        {
            _activeWork.Remove(work);
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeReady || _activeWork.Count == 0)
        {
            return;
        }

        args.Cancel = true;
        if (_closingRequested)
        {
            return;
        }

        _closingRequested = true;
        SetBusy(true);
        RootGrid.IsHitTestVisible = false;
        CompactViewSelector.IsEnabled = false;
        FilesListView.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ProgressTextBlock.Text = "正在取消并清理…";
        _windowCancellation.Cancel();
        _runCancellation?.Cancel();
        _previewCancellation?.Cancel();
        _coverValidationCancellation?.Cancel();

        await Task.Yield();
        try
        {
            await Task.WhenAll(_activeWork.ToArray());
        }
        catch (Exception)
        {
        }

        _closeReady = true;
        Close();
    }

    private void WindowBackgroundGrid_Loaded(object sender, RoutedEventArgs e)
    {
        var root = WindowBackgroundGrid.XamlRoot;
        if (!ReferenceEquals(_windowXamlRoot, root))
        {
            if (_windowXamlRoot is not null)
            {
                _windowXamlRoot.Changed -= WindowXamlRoot_Changed;
            }

            _windowXamlRoot = root;
            root.Changed += WindowXamlRoot_Changed;
        }

        UpdateMinimumWindowSize();
        UpdateMainLayout();
    }

    private void WindowXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        UpdateMinimumWindowSize();
        UpdateMainLayout();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange)
        {
            UpdateMinimumWindowSize();
        }
    }

    private void UpdateMinimumWindowSize()
    {
        if (_windowXamlRoot is null || AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        var scale = _windowXamlRoot.RasterizationScale;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Min((int)Math.Ceiling(MinimumWindowWidth * scale), Math.Max(1, workArea.Width - 24));
        var height = Math.Min((int)Math.Ceiling(MinimumWindowHeight * scale), Math.Max(1, workArea.Height - 24));
        if (presenter.PreferredMinimumWidth != width)
        {
            presenter.PreferredMinimumWidth = width;
        }

        if (presenter.PreferredMinimumHeight != height)
        {
            presenter.PreferredMinimumHeight = height;
        }

        var size = AppWindow.Size;
        if (presenter.State == OverlappedPresenterState.Restored && (size.Width < width || size.Height < height))
        {
            AppWindow.Resize(new SizeInt32(Math.Max(size.Width, width), Math.Max(size.Height, height)));
        }
    }

    private void SetInitialWindowBounds()
    {
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d;
        var width = Math.Min((int)Math.Ceiling(1120 * scale), Math.Max(1, workArea.Width - 48));
        var height = Math.Min((int)Math.Ceiling(780 * scale), Math.Max(1, workArea.Height - 48));
        var x = workArea.X + (workArea.Width - width) / 2;
        var y = workArea.Y + (workArea.Height - height) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".ncm");
            var files = await picker.PickMultipleFilesAsync();
            await AddPathsAsync(files.Select(file => file.Path));
        }
        catch (Exception exception)
        {
            ShowMessage("无法添加文件", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
            if (folder is not null)
            {
                await AddPathsAsync([folder.Path]);
            }
        }
        catch (Exception exception)
        {
            ShowMessage("无法添加文件夹", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
            if (folder is not null)
            {
                OutputDirectoryTextBox.Text = folder.Path;
                StatusInfoBar.IsOpen = false;
                RefreshPlan();
            }
        }
        catch (Exception exception)
        {
            ShowMessage("无法选择输出目录", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
        => RemoveRows(_rows.ToArray());

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        => RemoveRows(FilesListView.SelectedItems.OfType<ConversionRow>().ToArray());

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _pendingAdds > 0 || _rows.Count == 0)
        {
            return;
        }

        _updatingSelection = true;
        try
        {
            FilesListView.SelectionMode = IsSelectionMode ? ListViewSelectionMode.Single : ListViewSelectionMode.Multiple;
            FilesListView.SelectedItem = null;
        }
        finally
        {
            _updatingSelection = false;
        }
        UpdateSelectionActions();
        UpdateSelectedDetails();
    }

    private void RemoveRows(ConversionRow[] rows)
    {
        if (_busy || _pendingAdds > 0 || rows.Length == 0)
        {
            return;
        }

        _previewCancellation?.Cancel();
        _coverValidationCancellation?.Cancel();
        StatusInfoBar.IsOpen = false;
        _updatingSelection = true;
        try
        {
            if (rows.Length == _rows.Count)
            {
                _rows.Clear();
                _rowsByPath.Clear();
            }
            else
            {
                foreach (var row in rows)
                {
                    _rows.Remove(row);
                    _rowsByPath.Remove(row.InputPath);
                }

            }
        }
        finally
        {
            _updatingSelection = false;
        }

        _currentPlan = null;
        BatchProgressBar.Value = 0;
        ProgressTextBlock.Text = _rows.Count == 0
            ? "添加文件后选择输出目录"
            : $"已移除 {rows.Length} 个文件，剩余 {_rows.Count} 个";
        AudioAdjustmentTextBlock.Text = string.Empty;
        UpdateListState();
        RefreshPlan();
        UpdateSelectedDetails();
        _ = ValidateCoversAsync();
        _ = UpdateAudioPreviewAsync();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (_draggingRows)
        {
            return;
        }

        var canAdd = !_busy && _pendingAdds == 0 && e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = canAdd ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.Handled = true;
        if (canAdd)
        {
            e.DragUIOverride.Caption = "添加 NCM 文件或文件夹";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private void FilesListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_busy || _pendingAdds > 0 || IsSelectionMode)
        {
            e.Cancel = true;
            return;
        }

        _draggingRows = true;
        _orderBeforeDrag = _rows.Select(row => row.InputPath).ToArray();
    }

    private void FilesListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _draggingRows = false;
        var originalOrder = _orderBeforeDrag;
        _orderBeforeDrag = null;
        if (_windowClosed || _busy || _pendingAdds > 0)
        {
            return;
        }

        if (originalOrder is null || originalOrder.SequenceEqual(_rows.Select(row => row.InputPath), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsAdvanced && NumberingModeRadioButtons.SelectedIndex == 1)
        {
            NumberingModeRadioButtons.SelectedIndex = 2;
        }
        else
        {
            RefreshPlan();
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_draggingRows)
        {
            return;
        }

        e.Handled = true;
        if (_busy || _pendingAdds > 0 || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        string[]? paths = null;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = items.Select(item => item.Path).ToArray();
        }
        catch (Exception exception)
        {
            ShowMessage("无法读取拖入内容", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            deferral.Complete();
        }

        if (paths is not null)
        {
            try
            {
                await AddPathsAsync(paths);
            }
            catch (Exception exception)
            {
                ShowMessage("无法添加拖入文件", exception.Message, InfoBarSeverity.Error);
            }
        }
    }

    private Task AddPathsAsync(IEnumerable<string> paths) => TrackWorkAsync(AddPathsCoreAsync(paths));

    private async Task AddPathsCoreAsync(IEnumerable<string> paths)
    {
        if (_busy || _pendingAdds > 0 || _closingRequested || _windowClosed)
        {
            return;
        }

        _pendingAdds++;
        SetInputActionsEnabled(false);
        UpdateSelectionActions();
        UpdateStartState();
        try
        {
            StatusInfoBar.IsOpen = false;
            ProgressTextBlock.Text = "正在扫描文件…";
            var token = _windowCancellation.Token;
            var scan = await NcmQueueScanner.ScanAsync(paths, token);
            var added = 0;
            foreach (var path in scan.InputPaths)
            {
                token.ThrowIfCancellationRequested();
                if (_rowsByPath.ContainsKey(path))
                {
                    continue;
                }

                var row = new ConversionRow(path);
                _rowsByPath.Add(path, row);
                _rows.Add(row);
                added++;
                try
                {
                    row.SourceItem = await Task.Run(() => _media.InspectOutputItemAsync(path, cancellationToken: token), token);
                    row.StatusText = "待转换";
                    var extension = SelectedOutputFormat(row.SourceItem.OutputFormat) == NcmAudioFormat.Flac ? ".flac" : ".mp3";
                    row.OutputFileName = Path.GetFileNameWithoutExtension(row.InputPath) + extension;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    row.StatusText = "读取失败";
                    row.ErrorMessage = exception.Message;
                }
            }

            UpdateListState();
            ProgressTextBlock.Text = $"新增 {added} 个文件，可转换 {_rows.Count(row => row.SourceItem is not null)} 个";
            if (scan.Errors.Count > 0)
            {
                var first = scan.Errors[0];
                ShowMessage("部分路径无法扫描", $"已跳过 {scan.Errors.Count} 个路径。首个错误：{first.Message}", InfoBarSeverity.Warning);
            }

            RefreshPlan();
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _pendingAdds--;
            if (!_closingRequested)
            {
                SetInputActionsEnabled(!_busy && _pendingAdds == 0);
                UpdateSelectionActions();
                UpdateStartState();
                _ = ValidateCoversAsync();
                _ = UpdateAudioPreviewAsync();
            }
        }
    }

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_initialized)
        {
            StatusInfoBar.IsOpen = false;
            UpdateMode();
            RefreshPlan();
            _ = ValidateCoversAsync();
            _ = UpdateAudioPreviewAsync();
        }
    }

    private void UpdateMode()
    {
        UpdateMainLayout(force: true);
        UpdateSelectedDetails();
        UpdateAdvancedControlState();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateMainLayout();
    }

    private void UpdateMainLayout(bool force = false)
    {
        var width = RootGrid.ActualWidth;
        var category = width < 650 ? 0 : width < 1100 ? 1 : 2;
        var compactHeight = RootGrid.ActualHeight > 0 && RootGrid.ActualHeight < MinimumWindowHeight;
        if (!force && _layoutCategory == category && _layoutAdvanced == IsAdvanced && _layoutCompactHeight == compactHeight)
        {
            return;
        }

        _layoutCategory = category;
        _layoutAdvanced = IsAdvanced;
        _layoutCompactHeight = compactHeight;
        var padding = category == 0 || compactHeight ? 12 : 24;
        RootGrid.Padding = new Thickness(padding, 0, padding, padding);
        StatusMessageScrollViewer.MaxHeight = compactHeight ? 80 : 120;
        var sideBySide = IsAdvanced && category == 2;
        var compactAdvanced = IsAdvanced && !sideBySide;
        var compactImport = compactAdvanced || compactHeight;
        DropPromptTextBlock.Visibility = compactImport ? Visibility.Collapsed : Visibility.Visible;
        DropZoneBorder.Padding = compactImport ? new Thickness(8) : new Thickness(18);
        ImportActionsGrid.MinHeight = compactImport ? 36 : 88;
        SettingsColumn.Width = sideBySide ? new GridLength(360) : new GridLength(0);
        CompactViewSelector.Visibility = compactAdvanced ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(AdvancedSettingsPanel, sideBySide ? 0 : 1);
        AdvancedSettingsPanel.Visibility = sideBySide || compactAdvanced &&
            CompactViewSelector.SelectedItem == CompactSettingsItem
            ? Visibility.Visible
            : Visibility.Collapsed;
        Grid.SetColumn(FilesPanel, 1);
        FilesPanel.Visibility = !compactAdvanced || CompactViewSelector.SelectedItem == CompactFilesItem
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CompactViewSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_initialized)
        {
            UpdateMainLayout(force: true);
        }
    }

    private void AdvancedSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _busy)
        {
            return;
        }

        UpdateAdvancedControlState();
        StatusInfoBar.IsOpen = false;
        RefreshPlan();
        if (ReferenceEquals(sender, AudioFormatComboBox) || ReferenceEquals(sender, CoverComboBox))
        {
            _ = ValidateCoversAsync();
        }

        if (ReferenceEquals(sender, AudioFormatComboBox) || ReferenceEquals(sender, Mp3BitrateComboBox))
        {
            _ = UpdateAudioPreviewAsync();
        }
    }

    private void AdvancedNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initialized && !_busy)
        {
            StatusInfoBar.IsOpen = false;
            RefreshPlan();
            if (ReferenceEquals(sender, OriginalCoverEdgeNumberBox))
            {
                _ = ValidateCoversAsync();
            }
        }
    }

    private void UpdateAdvancedControlState()
    {
        if (!_initialized)
        {
            return;
        }

        Mp3BitrateComboBox.Visibility = AudioFormatComboBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        FlacLevelNumberBox.Visibility = AudioFormatComboBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        AudioAdjustmentTextBlock.Visibility = AudioFormatComboBox.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        var importedCover = CoverComboBox.SelectedIndex == 2;
        ImportedCoverPicker.Visibility = importedCover ? Visibility.Visible : Visibility.Collapsed;
        OriginalCoverEdgeNumberBox.Visibility = CoverComboBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private AudioExportFormat SelectedAudioFormat() => AudioFormatComboBox.SelectedIndex switch
    {
        1 => AudioExportFormat.Mp3,
        2 => AudioExportFormat.Flac,
        _ => AudioExportFormat.Original
    };

    private MediaExportOptions BuildMediaOptions(bool audioOnly = false)
    {
        if (!IsAdvanced)
        {
            return new MediaExportOptions();
        }

        var bitrate = Mp3BitrateComboBox.SelectedIndex switch
        {
            0 => 128,
            2 => 256,
            3 => 320,
            _ => 192
        };
        var level = double.IsNaN(FlacLevelNumberBox.Value) ? 5 : (int)FlacLevelNumberBox.Value;
        if (audioOnly)
        {
            return new MediaExportOptions(
                AudioFormat: SelectedAudioFormat(),
                Mp3BitrateKbps: bitrate,
                FlacCompressionLevel: level);
        }

        var selection = CoverComboBox.SelectedIndex switch
        {
            1 => CoverSelection.None,
            2 => CoverSelection.Imported,
            _ => CoverSelection.Original
        };
        var picturePath = _batchCoverPath;
        if (selection == CoverSelection.Imported && string.IsNullOrWhiteSpace(picturePath))
        {
            throw new InvalidOperationException("请先选择要导入的封面图片。");
        }

        var edge = double.IsNaN(OriginalCoverEdgeNumberBox.Value) || OriginalCoverEdgeNumberBox.Value <= 0
            ? null
            : (int?)OriginalCoverEdgeNumberBox.Value;
        return new MediaExportOptions(
            selection,
            selection == CoverSelection.Imported ? picturePath : null,
            edge,
            SelectedAudioFormat(),
            bitrate,
            level);
    }

    private OutputPlanOptions BuildPlanOptions() => !IsAdvanced
        ? new OutputPlanOptions()
        : new OutputPlanOptions(
            (OutputClassification)Math.Max(0, ClassificationComboBox.SelectedIndex),
            (OutputFileNaming)Math.Max(0, NamingComboBox.SelectedIndex),
            NumberingModeRadioButtons.SelectedIndex > 0,
            NumberingModeRadioButtons.SelectedIndex == 1 ? OutputTrackNumbering.AlbumOrder : OutputTrackNumbering.ListOrder);

    private NcmAudioFormat SelectedOutputFormat(NcmAudioFormat sourceFormat) =>
        !IsAdvanced ? sourceFormat : SelectedAudioFormat() switch
        {
            AudioExportFormat.Mp3 => NcmAudioFormat.Mp3,
            AudioExportFormat.Flac => NcmAudioFormat.Flac,
            _ => sourceFormat
        };

    private void RefreshPlan(bool markPending = true)
    {
        if (!_initialized || _busy)
        {
            return;
        }

        _currentPlan = null;
        _planError = null;
        var ready = _rows.Where(row => row.SourceItem is not null).ToArray();
        var items = ready.Select(row => row.SourceItem! with
        {
            OutputFormat = SelectedOutputFormat(row.SourceItem!.OutputFormat)
        }).ToArray();
        var options = BuildPlanOptions();
        var namesPrepared = false;
        try
        {
            var preview = OutputPlanner.PreviewFileNames(items, options);
            foreach (var item in preview)
            {
                _rowsByPath[item.InputPath].OutputFileName = item.FileName;
            }
            namesPrepared = true;

            if (IsAdvanced && NumberingModeRadioButtons.SelectedIndex == 1)
            {
                var previewPaths = preview.Select(item => item.InputPath).ToArray();
                var validPaths = previewPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
                ReorderRows(previewPaths.Concat(_rows.Where(row => !validPaths.Contains(row.InputPath)).Select(row => row.InputPath)).ToArray());
            }

            if (!string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text))
            {
                var plan = OutputPlanner.Create(OutputDirectoryTextBox.Text, items, options);
                foreach (var item in plan.Items)
                {
                    var row = _rowsByPath[item.Source.InputPath];
                    row.OutputFileName = item.FileName;
                }

                _currentPlan = plan;
            }
        }
        catch (Exception exception)
        {
            _planError = exception.Message;
            if (!namesPrepared)
            {
                try
                {
                    foreach (var item in OutputPlanner.PreviewFileNames(items, options with { NumberTracks = false }))
                    {
                        _rowsByPath[item.InputPath].OutputFileName = item.FileName;
                    }
                }
                catch
                {
                    foreach (var row in ready)
                    {
                        var extension = SelectedOutputFormat(row.SourceItem!.OutputFormat) == NcmAudioFormat.Flac ? ".flac" : ".mp3";
                        row.OutputFileName = Path.GetFileNameWithoutExtension(row.InputPath) + extension;
                    }
                }
            }

            ShowMessage("无法规划输出", exception.Message, InfoBarSeverity.Error);
        }

        if (markPending)
        {
            var changed = false;
            foreach (var row in ready)
            {
                if (row.StatusText is "已完成" or "失败" or "已取消")
                {
                    row.StatusText = "待转换";
                    row.ErrorMessage = null;
                    changed = true;
                }
            }

            if (changed)
            {
                ProgressTextBlock.Text = "设置已更新，等待转换";
                BatchProgressBar.Value = 0;
            }
        }

        UpdateSelectedDetails();
        UpdateStartState();
    }

    private void ReorderRows(IEnumerable<string> orderedPaths)
    {
        var selected = FilesListView.SelectedItems.OfType<ConversionRow>().ToArray();
        _updatingSelection = true;
        try
        {
            var index = 0;
            foreach (var path in orderedPaths)
            {
                var current = _rows.IndexOf(_rowsByPath[path]);
                if (current != index)
                {
                    _rows.Move(current, index);
                }

                index++;
            }
        }
        finally
        {
            try
            {
                if (FilesListView.SelectionMode == ListViewSelectionMode.Single)
                {
                    FilesListView.SelectedItem = selected.FirstOrDefault();
                }
                else
                {
                    FilesListView.SelectedItems.Clear();
                    foreach (var row in selected)
                    {
                        FilesListView.SelectedItems.Add(row);
                    }
                }
            }
            finally
            {
                _updatingSelection = false;
                UpdateSelectionActions();
            }
        }
    }

    private void UpdateListState()
    {
        if (_rows.Count == 0 && IsSelectionMode)
        {
            FilesListView.SelectionMode = ListViewSelectionMode.Single;
        }

        ItemsHeadingTextBlock.Text = $"文件（{_rows.Count}）";
        EmptyListMessage.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionActions();
    }

    private void UpdateSelectionActions()
    {
        var canModify = !_busy && _pendingAdds == 0;
        var selectionMode = IsSelectionMode;
        var selectedCount = FilesListView.SelectedItems.Count;
        ClearButton.IsEnabled = canModify && _rows.Count > 0;
        SelectButton.IsEnabled = canModify && _rows.Count > 0;
        SelectButton.Content = selectionMode ? "取消" : "选择";
        RemoveSelectedButton.Visibility = selectionMode ? Visibility.Visible : Visibility.Collapsed;
        RemoveSelectedButton.IsEnabled = canModify && selectionMode && selectedCount > 0;
        RemoveSelectedButton.Content = $"移除所选（{selectedCount}）";
        FilesListView.CanDragItems = canModify && !selectionMode;
        FilesListView.CanReorderItems = canModify && !selectionMode;
    }

    private void UpdateStartState()
    {
        StartButton.IsEnabled = !_busy && !_startPromptOpen;
        StartButton.Content = _currentPlan is { Items.Count: > 0 } && _currentPlan.Items.All(item =>
                _rowsByPath[item.Source.InputPath].StatusText == "已完成")
            ? "再次导出"
            : "开始转换";
    }

    private void UpdateSelectedDetails()
    {
        var selected = FilesListView.SelectedItems.OfType<ConversionRow>().ToArray();
        SelectedDetailTextBlock.Text = selected.Length switch
        {
            0 => string.Empty,
            1 => selected[0].ErrorMessage is { } error ? $"错误：{error}" : string.Empty,
            _ => $"已选择 {selected.Length} 个文件，可从队列中移除。源文件保持不变。"
        };
        SelectedDetailTextBlock.Visibility = string.IsNullOrEmpty(SelectedDetailTextBlock.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingSelection)
        {
            return;
        }

        UpdateSelectedDetails();
        UpdateSelectionActions();
    }

    private async void ChooseBatchCover_Click(object sender, RoutedEventArgs e)
    {
        var selected = await PickImageAsync();
        if (selected is not null)
        {
            _batchCoverPath = selected;
            BatchCoverPathRun.Text = selected;
            StatusInfoBar.IsOpen = false;
            RefreshPlan();
            await ValidateCoversAsync();
        }
    }

    private async Task<string?> PickImageAsync()
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            foreach (var extension in CoverImageFormats.SupportedFileExtensions())
            {
                picker.FileTypeFilter.Add(extension);
            }
            return (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception exception)
        {
            ShowMessage("无法选择封面", exception.Message, InfoBarSeverity.Error);
            return null;
        }
    }

    private Task UpdateAudioPreviewAsync() => TrackWorkAsync(UpdateAudioPreviewCoreAsync());

    private async Task UpdateAudioPreviewCoreAsync()
    {
        if (_closingRequested || _windowClosed)
        {
            return;
        }

        _previewCancellation?.Cancel();
        AudioAdjustmentTextBlock.Text = string.Empty;
        if (_busy || !IsAdvanced || SelectedAudioFormat() == AudioExportFormat.Original)
        {
            _previewCancellation = null;
            return;
        }

        var rows = _rows.Where(row => row.SourceItem is not null).ToArray();
        if (rows.Length == 0)
        {
            _previewCancellation = null;
            AudioAdjustmentTextBlock.Text = "添加文件后显示格式调整预览。";
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        _previewCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            AudioAdjustmentTextBlock.Text = $"正在预览 {rows.Length} 个文件的格式调整…";
            var options = BuildMediaOptions(audioOnly: true);
            var adjustments = new HashSet<string>(StringComparer.Ordinal);
            var failures = 0;
            string? firstError = null;
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var preview = await Task.Run(() => _media.PreviewAsync(row.InputPath, options, token), token);
                    adjustments.UnionWith(preview.Adjustments);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures++;
                    firstError ??= exception.Message;
                }
            }

            token.ThrowIfCancellationRequested();
            var summary = adjustments.Count == 0
                ? "已预览文件的采样率和声道可保持源设置。"
                : string.Join("；", adjustments);
            AudioAdjustmentTextBlock.Text = failures == 0
                ? summary
                : $"{rows.Length - failures} 个文件完成格式预览，{failures} 个无法预览：{firstError}。{(failures < rows.Length ? summary : string.Empty)}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                AudioAdjustmentTextBlock.Text = exception.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
            }
        }
    }

    private Task ValidateCoversAsync() => TrackWorkAsync(ValidateCoversCoreAsync());

    private async Task ValidateCoversCoreAsync()
    {
        if (_closingRequested || _windowClosed)
        {
            return;
        }

        _coverValidationCancellation?.Cancel();
        if (_busy || !IsAdvanced)
        {
            return;
        }

        var candidates = _rows.Where(row => row.SourceItem is not null).ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        _coverValidationCancellation = cancellation;
        var missing = 0;
        var invalid = 0;
        try
        {
            foreach (var row in candidates)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var options = BuildMediaOptions();
                    var available = await Task.Run(
                        () => _media.ValidateCoverCapacityAsync(row.InputPath, options, cancellation.Token),
                        cancellation.Token);
                    if (!available)
                    {
                        missing++;
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    invalid++;
                }
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (invalid == 0 && missing > 0)
            {
                ShowMessage("原封面不可用", $"{missing} 个文件没有可用的原封面，导出仍可继续。", InfoBarSeverity.Informational);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_coverValidationCancellation, cancellation))
            {
                _coverValidationCancellation = null;
            }
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _startPromptOpen || _closingRequested || _windowClosed)
        {
            return;
        }

        if (_pendingAdds > 0 || _draggingRows)
        {
            await ShowStartPromptAsync("文件尚未准备好", "请等文件读取或排序完成后再开始转换。");
            return;
        }

        if (!_rows.Any(row => row.SourceItem is not null))
        {
            await ShowStartPromptAsync("没有可转换文件", _rows.Count == 0
                ? "请先添加 NCM 文件或文件夹。"
                : "当前文件均未读取成功。选中文件可查看具体错误。");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text))
        {
            await ShowStartPromptAsync("请选择输出目录", "转换前请先选择文件的保存位置。");
            return;
        }

        RefreshPlan(markPending: false);
        if (_currentPlan is null || _currentPlan.Items.Count == 0)
        {
            await ShowStartPromptAsync("无法规划输出", _planError ?? "当前没有可转换的文件。");
            return;
        }

        var plan = _currentPlan;
        var plannedByPath = plan.Items.ToDictionary(item => item.Source.InputPath, StringComparer.OrdinalIgnoreCase);
        MediaExportOptions options;
        try
        {
            options = BuildMediaOptions();
        }
        catch (Exception exception)
        {
            await ShowStartPromptAsync("导出选项不完整", exception.Message);
            return;
        }

        await TrackWorkAsync(RunConversionAsync(plan, plannedByPath, options));
    }

    private async Task RunConversionAsync(
        OutputPlan plan,
        IReadOnlyDictionary<string, PlannedOutputItem> plannedByPath,
        MediaExportOptions options)
    {
        foreach (var item in plan.Items)
        {
            var row = _rowsByPath[item.Source.InputPath];
            row.StatusText = "待转换";
            row.ErrorMessage = null;
        }

        SetBusy(true);
        _coverValidationCancellation?.Cancel();
        _previewCancellation?.Cancel();
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        try
        {
            var progress = new Progress<NcmQueueProgress>(UpdateProgress);
            var result = await new NcmQueue(maxConcurrency: 2).RunAsync(
                plan.Items.Select(item => item.Source.InputPath).ToArray(),
                async (path, token) =>
                {
                    var exported = await Task.Run(
                        () => _media.ExportPlannedAsync(plan, plannedByPath[path], options, token),
                        token);
                    return exported.OutputPath;
                },
                progress,
                _runCancellation.Token);
            foreach (var item in result.Items)
            {
                if (_rowsByPath.TryGetValue(item.InputPath, out var row))
                {
                    row.StatusText = item.Status switch
                    {
                        NcmQueueItemStatus.Completed => "已完成",
                        NcmQueueItemStatus.Failed => "失败",
                        NcmQueueItemStatus.Cancelled => "已取消",
                        _ => row.StatusText
                    };
                    if (item.OutputPath is not null)
                    {
                        row.OutputFileName = Path.GetFileName(item.OutputPath);
                    }
                    else if (item.ErrorMessage is not null)
                    {
                        row.ErrorMessage = item.ErrorMessage;
                    }
                }
            }

            ProgressTextBlock.Text = $"完成 {result.Completed}，失败 {result.Failed}，取消 {result.Cancelled}";
            UpdateSelectedDetails();
            if (result.Failed > 0)
            {
                ShowMessage("部分文件转换失败", "选中失败的文件可查看错误。", InfoBarSeverity.Warning);
            }
        }
        catch (Exception exception)
        {
            ShowMessage("队列无法继续", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            if (!_closingRequested)
            {
                SetBusy(false);
            }
        }
    }

    private async Task ShowStartPromptAsync(string title, string message)
    {
        if (_startPromptOpen || _windowClosed)
        {
            return;
        }

        var root = WindowBackgroundGrid.XamlRoot;
        if (root is null)
        {
            ShowMessage(title, message, InfoBarSeverity.Warning);
            return;
        }

        _startPromptOpen = true;
        UpdateStartState();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                RequestedTheme = WindowBackgroundGrid.ActualTheme,
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            if (!_windowClosed)
            {
                ShowMessage(title, $"{message}（提示窗口无法显示：{exception.Message}）", InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _startPromptOpen = false;
            if (!_windowClosed)
            {
                UpdateStartState();
            }
        }
    }

    private void UpdateProgress(NcmQueueProgress progress)
    {
        if (!_busy || _closingRequested || _windowClosed)
        {
            return;
        }

        BatchProgressBar.Maximum = Math.Max(1, progress.Total);
        BatchProgressBar.Value = progress.Completed + progress.Failed + progress.Cancelled;
        ProgressTextBlock.Text = $"已处理 {progress.Completed + progress.Failed + progress.Cancelled}/{progress.Total} · 正在转换 {progress.Running}";
        if (progress.LastItem is { } item && _rowsByPath.TryGetValue(item.InputPath, out var row))
        {
            row.StatusText = item.Status switch
            {
                NcmQueueItemStatus.Running => "转换中",
                NcmQueueItemStatus.Completed => "已完成",
                NcmQueueItemStatus.Failed => "失败",
                NcmQueueItemStatus.Cancelled => "已取消",
                _ => row.StatusText
            };
            if (item.ErrorMessage is not null)
            {
                row.ErrorMessage = item.ErrorMessage;
            }

            if (FilesListView.SelectedItems.Contains(row))
            {
                UpdateSelectedDetails();
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressTextBlock.Text = "正在取消…";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SetInputActionsEnabled(!busy && _pendingAdds == 0);
        UpdateSelectionActions();
        OutputDirectoryTextBox.IsEnabled = !busy;
        ChooseOutputButton.IsEnabled = !busy;
        ModeSelector.IsEnabled = !busy;
        AdvancedSettingsPanel.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        UpdateStartState();
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        if (_closingRequested || _windowClosed)
        {
            return;
        }

        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;
    }

    private void SetInputActionsEnabled(bool enabled)
    {
        AddFilesButton.IsEnabled = enabled;
        AddFolderButton.IsEnabled = enabled;
    }
}
