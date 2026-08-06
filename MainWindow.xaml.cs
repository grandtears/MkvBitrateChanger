using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MkvBitrateChanger
{
    public partial class MainWindow : Window
    {
        private const int MaxLogLines = 300;
        private readonly Queue<string> logLines = new Queue<string>();
        private readonly ObservableCollection<string> inputFiles = new ObservableCollection<string>();
        private DateTime lastProgressUpdateUtc = DateTime.MinValue;
        private double? inputDurationSeconds;
        private string lastProgressSpeed = string.Empty;
        private string currentQueuePosition = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            InputFilesListBox.ItemsSource = inputFiles;
        }

        private async void SelectInput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "MKV Files (*.mkv)|*.mkv",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                AddInputFiles(dialog.FileNames);
                await UpdateSelectedInputMetadata();
            }
        }

        private async void RemoveInput_Click(object sender, RoutedEventArgs e)
        {
            var selectedFiles = new List<string>();
            foreach (string file in InputFilesListBox.SelectedItems)
            {
                selectedFiles.Add(file);
            }

            foreach (string file in selectedFiles)
            {
                inputFiles.Remove(file);
            }

            await UpdateSelectedInputMetadata();
        }

        private void ClearInput_Click(object sender, RoutedEventArgs e)
        {
            inputFiles.Clear();
            ResetMetadata();
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = GetDroppedMkvPaths(e).Count == 0 ? DragDropEffects.None : DragDropEffects.Copy;
            e.Handled = true;
        }

        private async void Window_PreviewDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;

            IReadOnlyList<string> paths = GetDroppedMkvPaths(e);
            if (paths.Count == 0)
            {
                MessageBox.Show("MKVファイルをドロップしてください。");
                return;
            }

            AddInputFiles(paths);
            await UpdateSelectedInputMetadata();
        }

        private IReadOnlyList<string> GetDroppedMkvPaths(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return Array.Empty<string>();
            }

            var mkvFiles = new List<string>();
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            foreach (string file in files)
            {
                if (File.Exists(file) &&
                    string.Equals(Path.GetExtension(file), ".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    mkvFiles.Add(file);
                }
            }

            return mkvFiles;
        }

        private void AddInputFiles(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                bool alreadyAdded = false;
                foreach (string existingPath in inputFiles)
                {
                    if (string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyAdded = true;
                        break;
                    }
                }

                if (!alreadyAdded)
                {
                    inputFiles.Add(path);
                }
            }

            if (string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text) && inputFiles.Count > 0)
            {
                OutputDirectoryTextBox.Text = Path.GetDirectoryName(inputFiles[0]) ?? string.Empty;
            }
        }

        private void SelectOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "出力フォルダーを選択してください",
                InitialDirectory = Directory.Exists(OutputDirectoryTextBox.Text)
                    ? OutputDirectoryTextBox.Text
                    : null
            };

            if (dialog.ShowDialog() == true)
            {
                OutputDirectoryTextBox.Text = dialog.FolderName;
            }
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            LogTextBox.Clear();
            logLines.Clear();
            ResetMetadata();
            StatusTextBlock.Text = "待機中";
            ResetCurrentProgress();

            try
            {
                if (inputFiles.Count == 0)
                {
                    MessageBox.Show("入力MKVファイルを1つ以上選択してください。");
                    return;
                }

                foreach (string inputPath in inputFiles)
                {
                    if (!File.Exists(inputPath))
                    {
                        MessageBox.Show($"入力ファイルが見つかりません。\n{inputPath}");
                        return;
                    }
                }

                if (string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text))
                {
                    MessageBox.Show("出力フォルダーを指定してください。");
                    return;
                }

                if (!ValidateEncodingSettings())
                {
                    return;
                }

                Directory.CreateDirectory(OutputDirectoryTextBox.Text);

                if (!await IsCommandAvailable("ffmpeg"))
                {
                    var result = MessageBox.Show(
                        "FFmpegが見つかりません。wingetでインストールしますか？",
                        "FFmpeg未検出",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }

                    await InstallFfmpegByWinget();
                    if (!await IsCommandAvailable("ffmpeg"))
                    {
                        MessageBox.Show("FFmpegを検出できませんでした。インストール後、アプリを再起動してください。");
                        return;
                    }
                }

                var queuedFiles = new List<string>(inputFiles);
                IReadOnlyList<string> outputPaths = BuildOutputPaths(queuedFiles);
                int completedCount = 0;
                int failedCount = 0;
                long totalOutputBytes = 0;

                SetConfigurationEnabled(false);

                for (int index = 0; index < queuedFiles.Count; index++)
                {
                    string inputPath = queuedFiles[index];
                    string outputPath = outputPaths[index];
                    currentQueuePosition = $"{index + 1}/{queuedFiles.Count}";
                    await UpdateInputMetadata(inputPath);
                    ResetCurrentProgress();

                    AppendLog($"[{currentQueuePosition}] {Path.GetFileName(inputPath)}");
                    string args = BuildFfmpegArgs(inputPath, outputPath);
                    AppendLog("ffmpeg " + args);
                    AppendLog(string.Empty);

                    ProgressBar.IsIndeterminate = inputDurationSeconds == null;
                    StatusTextBlock.Text = $"{currentQueuePosition} 変換中: {Path.GetFileName(inputPath)}";

                    int exitCode = await RunProcess(
                        "ffmpeg",
                        args,
                        lowPriority: LowLoadCheckBox.IsChecked == true);

                    ProgressBar.IsIndeterminate = false;
                    if (exitCode == 0 && File.Exists(outputPath))
                    {
                        long outputBytes = new FileInfo(outputPath).Length;
                        totalOutputBytes += outputBytes;
                        completedCount++;
                        OutputSizeTextBlock.Text = FormatFileSize(outputBytes);
                        AppendLog($"完了: {outputPath}");
                    }
                    else
                    {
                        failedCount++;
                        AppendLog($"失敗: {inputPath}");
                    }
                }

                OutputSizeTextBlock.Text = completedCount == 0 ? "-" : FormatFileSize(totalOutputBytes);
                ProgressBar.Value = 100;
                StatusTextBlock.Text = failedCount == 0
                    ? $"完了（{completedCount}/{queuedFiles.Count}）"
                    : $"完了 {completedCount}件 / 失敗 {failedCount}件";

                MessageBox.Show(failedCount == 0
                    ? $"{completedCount}件の変換が完了しました。"
                    : $"{completedCount}件完了、{failedCount}件失敗しました。ログを確認してください。");
            }
            catch (Exception ex)
            {
                AppendLog("エラー: " + ex.Message);
                StatusTextBlock.Text = "失敗";
                MessageBox.Show("処理中にエラーが発生しました。ログを確認してください。");
            }
            finally
            {
                ProgressBar.IsIndeterminate = false;
                StartButton.IsEnabled = true;
                SetConfigurationEnabled(true);
            }
        }

        private IReadOnlyList<string> BuildOutputPaths(IReadOnlyList<string> inputs)
        {
            var paths = new List<string>();
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string inputPath in inputs)
            {
                string baseName = Path.GetFileNameWithoutExtension(inputPath) + "_converted";
                string outputPath = Path.Combine(OutputDirectoryTextBox.Text, baseName + ".mkv");
                int suffix = 2;

                while (!usedPaths.Add(outputPath))
                {
                    outputPath = Path.Combine(OutputDirectoryTextBox.Text, $"{baseName}_{suffix}.mkv");
                    suffix++;
                }

                paths.Add(outputPath);
            }

            return paths;
        }

        private bool ValidateEncodingSettings()
        {
            if (CrfRadio.IsChecked == true)
            {
                if (!int.TryParse(CrfTextBox.Text.Trim(), out int crf) || crf < 0 || crf > 51)
                {
                    MessageBox.Show("CRFは0～51の整数で指定してください。");
                    return false;
                }
            }
            else if (!int.TryParse(BitrateTextBox.Text.Trim(), out int bitrate) || bitrate <= 0)
            {
                MessageBox.Show("映像ビットレートは1以上の整数で指定してください。");
                return false;
            }

            return true;
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double length = bytes;
            int order = 0;

            while (length >= 1024 && order < sizes.Length - 1)
            {
                order++;
                length /= 1024;
            }

            return $"{length:F2} {sizes[order]}";
        }

        private async Task UpdateSelectedInputMetadata()
        {
            if (inputFiles.Count == 0)
            {
                ResetMetadata();
                return;
            }

            await UpdateInputMetadata(inputFiles[0]);
        }

        private async Task UpdateInputMetadata(string inputPath)
        {
            var inputFileInfo = new FileInfo(inputPath);
            InputSizeTextBlock.Text = FormatFileSize(inputFileInfo.Length);
            inputDurationSeconds = await GetVideoDurationSeconds(inputPath);
            DurationTextBlock.Text = inputDurationSeconds == null
                ? "-"
                : FormatDuration(TimeSpan.FromSeconds(inputDurationSeconds.Value));
        }

        private void ResetMetadata()
        {
            InputSizeTextBlock.Text = "-";
            DurationTextBlock.Text = "-";
            OutputSizeTextBlock.Text = "-";
            inputDurationSeconds = null;
        }

        private async Task<double?> GetVideoDurationSeconds(string inputPath)
        {
            if (!await IsCommandAvailable("ffprobe"))
            {
                return null;
            }

            string args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\"";
            var result = await RunProcessCapture("ffprobe", args);
            string durationText = result.Output.Trim();

            if (result.ExitCode == 0 &&
                double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                return seconds;
            }

            return null;
        }

        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            }

            return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
        }

        private string BuildFfmpegArgs(string input, string output)
        {
            string codec = UseH265CheckBox.IsChecked == true ? "libx265" : "libx264";
            string preset = ((ComboBoxItem)PresetComboBox.SelectedItem).Content.ToString()!;
            int encoderThreads = GetEncoderThreadCount();

            var args = new StringBuilder();
            args.Append($"-y -hide_banner -nostdin -progress pipe:1 -stats_period 1 -i \"{input}\" ");
            args.Append($"-c:v {codec} -threads {encoderThreads} ");

            if (CrfRadio.IsChecked == true)
            {
                args.Append($"-crf {CrfTextBox.Text.Trim()} ");
            }
            else
            {
                int bitrate = int.Parse(BitrateTextBox.Text.Trim(), CultureInfo.InvariantCulture);
                args.Append($"-b:v {bitrate}k -maxrate {bitrate}k -bufsize {bitrate * 2}k ");
            }

            args.Append($"-preset {preset} ");
            args.Append("-c:a copy -c:s copy ");
            args.Append($"\"{output}\"");
            return args.ToString();
        }

        private int GetEncoderThreadCount()
        {
            if (LowLoadCheckBox.IsChecked == true)
            {
                return Math.Max(1, Environment.ProcessorCount / 4);
            }

            return Math.Max(1, Environment.ProcessorCount - 2);
        }

        private async Task<bool> IsCommandAvailable(string command)
        {
            int exitCode = await RunProcess(command, "-version", logOutput: false);
            return exitCode == 0;
        }

        private async Task InstallFfmpegByWinget()
        {
            AppendLog("FFmpegをwingetでインストールします...");
            AppendLog("winget install -e --id Gyan.FFmpeg");
            await RunProcess("winget", "install -e --id Gyan.FFmpeg");
        }

        private async Task<int> RunProcess(
            string fileName,
            string arguments,
            bool logOutput = true,
            bool lowPriority = false)
        {
            var completion = new TaskCompletionSource<int>();
            int lineCount = 0;

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) => HandleProcessLine(e.Data, logOutput, ref lineCount);
            process.ErrorDataReceived += (_, e) => HandleProcessLine(e.Data, logOutput, ref lineCount);
            process.Exited += (_, _) =>
            {
                completion.TrySetResult(process.ExitCode);
                process.Dispose();
            };

            try
            {
                process.Start();
                if (lowPriority)
                {
                    try
                    {
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    }
                    catch (Exception ex)
                    {
                        _ = Dispatcher.BeginInvoke(() => AppendLog("プロセス優先度を変更できませんでした: " + ex.Message));
                    }
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                process.Dispose();
                return -1;
            }

            return await completion.Task;
        }

        private async Task<(int ExitCode, string Output)> RunProcessCapture(string fileName, string arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            try
            {
                process.Start();
            }
            catch
            {
                return (-1, string.Empty);
            }

            string output = await process.StandardOutput.ReadToEndAsync();
            await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, output);
        }

        private void HandleProcessLine(string? data, bool logOutput, ref int lineCount)
        {
            if (!logOutput || data == null)
            {
                return;
            }

            UpdateProgressFromFfmpeg(data);
            if (ShouldLogLine(data, ref lineCount))
            {
                Dispatcher.BeginInvoke(
                    () => AppendLog(data),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private bool ShouldLogLine(string data, ref int lineCount)
        {
            lineCount++;

            if (data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                data.Contains("warning", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (data.StartsWith("frame=", StringComparison.OrdinalIgnoreCase) ||
                data.StartsWith("fps=", StringComparison.OrdinalIgnoreCase) ||
                data.StartsWith("stream_", StringComparison.OrdinalIgnoreCase) ||
                data.StartsWith("bitrate=", StringComparison.OrdinalIgnoreCase) ||
                data.StartsWith("total_size=", StringComparison.OrdinalIgnoreCase) ||
                data.StartsWith("out_time", StringComparison.OrdinalIgnoreCase) ||
                data.StartsWith("dup_frames=", StringComparison.OrdinalIgnoreCase) ||
                data.StartsWith("drop_frames=", StringComparison.OrdinalIgnoreCase) ||
                data.StartsWith("speed=", StringComparison.OrdinalIgnoreCase) ||
                data.StartsWith("progress=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return lineCount % 10 == 0;
        }

        private void UpdateProgressFromFfmpeg(string data)
        {
            try
            {
                if (data.StartsWith("speed=", StringComparison.OrdinalIgnoreCase))
                {
                    lastProgressSpeed = data.Substring("speed=".Length).Trim();
                    return;
                }

                if (data.Equals("progress=end", StringComparison.OrdinalIgnoreCase))
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        ProgressBar.IsIndeterminate = false;
                        ProgressBar.Value = 100;
                        StatusTextBlock.Text = $"{currentQueuePosition} 現在のファイル: 100%";
                    }, System.Windows.Threading.DispatcherPriority.Background);
                    return;
                }

                double? seconds = TryReadProgressSeconds(data);
                if (seconds == null)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                if (now - lastProgressUpdateUtc < TimeSpan.FromMilliseconds(500))
                {
                    return;
                }

                lastProgressUpdateUtc = now;
                double? percent = inputDurationSeconds == null || inputDurationSeconds <= 0
                    ? null
                    : Math.Clamp(seconds.Value / inputDurationSeconds.Value * 100, 0, 100);

                string status = percent == null
                    ? $"{currentQueuePosition} 変換中: {FormatDuration(TimeSpan.FromSeconds(seconds.Value))}"
                    : $"{currentQueuePosition} 変換中: {percent.Value:F1}%";

                if (!string.IsNullOrWhiteSpace(lastProgressSpeed))
                {
                    status += $" speed={lastProgressSpeed}";
                }

                Dispatcher.BeginInvoke(() =>
                {
                    ProgressBar.IsIndeterminate = percent == null;
                    if (percent != null)
                    {
                        ProgressBar.Value = percent.Value;
                    }

                    ProgressBar.ToolTip = status;
                    StatusTextBlock.Text = status;
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch
            {
            }
        }

        private double? TryReadProgressSeconds(string data)
        {
            if (data.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(data.Substring("out_time_ms=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long milliseconds))
            {
                return milliseconds / 1000000.0;
            }

            if (data.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(data.Substring("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long microseconds))
            {
                return microseconds / 1000000.0;
            }

            if (data.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase) &&
                TimeSpan.TryParse(data.Substring("out_time=".Length), CultureInfo.InvariantCulture, out TimeSpan time))
            {
                return time.TotalSeconds;
            }

            var timeMatch = System.Text.RegularExpressions.Regex.Match(data, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (timeMatch.Success &&
                int.TryParse(timeMatch.Groups[1].Value, out int hours) &&
                int.TryParse(timeMatch.Groups[2].Value, out int minutes) &&
                double.TryParse(timeMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                return TimeSpan.FromHours(hours).TotalSeconds +
                    TimeSpan.FromMinutes(minutes).TotalSeconds +
                    seconds;
            }

            return null;
        }

        private void ResetCurrentProgress()
        {
            ProgressBar.Value = 0;
            lastProgressUpdateUtc = DateTime.MinValue;
            lastProgressSpeed = string.Empty;
        }

        private void SetConfigurationEnabled(bool enabled)
        {
            SelectInputButton.IsEnabled = enabled;
            RemoveInputButton.IsEnabled = enabled;
            ClearInputButton.IsEnabled = enabled;
            SelectOutputButton.IsEnabled = enabled;
            InputFilesListBox.IsEnabled = enabled;
            OutputDirectoryTextBox.IsEnabled = enabled;
            CrfRadio.IsEnabled = enabled;
            BitrateRadio.IsEnabled = enabled;
            CrfTextBox.IsEnabled = enabled;
            BitrateTextBox.IsEnabled = enabled;
            PresetComboBox.IsEnabled = enabled;
            UseH265CheckBox.IsEnabled = enabled;
            LowLoadCheckBox.IsEnabled = enabled;
        }

        private void AppendLog(string text)
        {
            logLines.Enqueue(text);
            while (logLines.Count > MaxLogLines)
            {
                logLines.Dequeue();
            }

            LogTextBox.Text = string.Join(Environment.NewLine, logLines) + Environment.NewLine;
            LogTextBox.ScrollToEnd();
        }
    }
}
