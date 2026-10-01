using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ncm.Core;

namespace Ncm.App;

public sealed partial class ConversionRow : INotifyPropertyChanged
{
    private string _targetPath = string.Empty;
    private string _outputFileName = string.Empty;
    private string _statusText = "读取中";

    public ConversionRow(string inputPath)
    {
        InputPath = inputPath;
        FileName = Path.GetFileName(inputPath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string InputPath { get; }

    public string FileName { get; }

    public override string ToString() => FileName;

    public OutputPlanItem? SourceItem { get; set; }

    public PlannedOutputItem? PlannedItem { get; set; }

    public string? ErrorMessage { get; set; }

    public string OutputFileName
    {
        get => _outputFileName;
        set
        {
            if (_outputFileName != value)
            {
                _outputFileName = value;
                OnPropertyChanged();
            }
        }
    }

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (_targetPath != value)
            {
                _targetPath = value;
                OnPropertyChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText != value)
            {
                _statusText = value;
                OnPropertyChanged();
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
