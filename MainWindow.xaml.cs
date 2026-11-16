using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace RnxPdfRaw2Ocr;

public sealed partial class MainWindow : Window
{
    private string? _sourcePath;
    private string? _resultPath;
    private CancellationTokenSource? _conversion;
    private bool _closeAfterConversion;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Closing += OnClosing;
    }

    private async void SelectPdf_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                FileTypeFilter = { ".pdf" }
            };
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            _sourcePath = file.Path;
            SourceText.Text = Path.GetFileName(_sourcePath);
            ToolTipService.SetToolTip(SourceText, _sourcePath);
            _resultPath = null;
            StatusBar.IsOpen = false;
            StartButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Не удалось выбрать PDF", ex.Message);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is not { } sourcePath || _conversion is not null)
            return;

        string language = (string)((ComboBoxItem)LanguageBox.SelectedItem).Tag;

        try
        {
            var picker = new FileSavePicker(AppWindow.Id)
            {
                SuggestedFolder = Path.GetDirectoryName(sourcePath),
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}_OCR.pdf",
                FileTypeChoices = { { "PDF", new List<string> { ".pdf" } } }
            };
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            string resultPath = file.Path;
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(resultPath), StringComparison.OrdinalIgnoreCase))
            {
                ShowStatus(InfoBarSeverity.Warning, "Укажите другой файл", "Выберите отдельный путь для PDF с распознанным текстом.");
                return;
            }

            _resultPath = null;
            StatusBar.IsOpen = false;
            using var conversion = new CancellationTokenSource();
            _conversion = conversion;
            SetBusy(true);

            var progress = new Progress<OcrProgress>(value =>
            {
                if (_conversion != conversion || conversion.IsCancellationRequested)
                    return;

                ProgressBar.IsIndeterminate = false;
                ProgressBar.Maximum = value.TotalPages;
                ProgressBar.Value = value.CompletedPages;
                ProgressText.Text = value.CompletedPages == value.TotalPages
                    ? "Подготовка результата…"
                    : $"Распознавание страницы {value.CompletedPages + 1} из {value.TotalPages}…";
            });

            int recognized = await Task.Run(() => PdfOcrProcessor.Convert(
                sourcePath, resultPath, language, progress, conversion.Token), conversion.Token);

            if (recognized == 0)
            {
                ShowStatus(InfoBarSeverity.Warning, "Текст не найден", "PDF с текстовым слоем не создан. Проверьте язык текста и качество скана.");
            }
            else
            {
                _resultPath = resultPath;
                ShowStatus(InfoBarSeverity.Success, "PDF с текстовым слоем сохранён", Path.GetFileName(resultPath));
                ToolTipService.SetToolTip(StatusBar, resultPath);
            }
        }
        catch (OperationCanceledException)
        {
            ShowStatus(InfoBarSeverity.Informational, "Распознавание отменено");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Не удалось обработать PDF", ex.Message);
        }
        finally
        {
            _conversion = null;
            SetBusy(false);
            if (_closeAfterConversion)
                Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => RequestCancellation();

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_conversion is null)
            return;

        e.Cancel = true;
        _closeAfterConversion = true;
        RequestCancellation();
    }

    private void RequestCancellation()
    {
        _conversion?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Остановка…";
    }

    private void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (_resultPath is not { } resultPath)
            return;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(resultPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Не удалось открыть PDF", ex.Message);
        }
    }

    private void SetBusy(bool busy)
    {
        SelectButton.IsEnabled = !busy;
        LanguageBox.IsEnabled = !busy;
        StartButton.IsEnabled = !busy && _sourcePath is not null;
        CancelButton.IsEnabled = busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            ProgressBar.Value = 0;
            ProgressBar.IsIndeterminate = true;
            ProgressText.Text = "Открытие PDF…";
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message = "")
    {
        ToolTipService.SetToolTip(StatusBar, null);
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        OpenButton.Visibility = _resultPath is null ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.IsOpen = true;
    }
}
