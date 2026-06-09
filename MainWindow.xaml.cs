using Microsoft.Win32;
using System;
using System.Collections.Generic;
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
        private DateTime lastProgressUpdateUtc = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void SelectInput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "MKV Files (*.mkv)|*.mkv"
            };

            if (dialog.ShowDialog() == true)
            {
                InputPathTextBox.Text = dialog.FileName;

                var dir = Path.GetDirectoryName(dialog.FileName)!;
                var name = Path.GetFileNameWithoutExtension(dialog.FileName);
                OutputPathTextBox.Text = Path.Combine(dir, $"{name}_converted.mkv");

                await UpdateInputMetadata();
            }
        }

        private void SelectOutput_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "MKV Files (*.mkv)|*.mkv",
                FileName = "output.mkv"
            };

            if (dialog.ShowDialog() == true)
            {
                OutputPathTextBox.Text = dialog.FileName;
            }
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;
            LogTextBox.Clear();
            logLines.Clear();
            InputSizeTextBlock.Text = "-";
            DurationTextBlock.Text = "-";
            OutputSizeTextBlock.Text = "-";
            StatusTextBlock.Text = "待機中";

            try
            {
                if (!File.Exists(InputPathTextBox.Text))
                {
                    MessageBox.Show("入力MKVファイルを選択してください。");
                    return;
                }

                if (string.IsNullOrWhiteSpace(OutputPathTextBox.Text))
                {
                    MessageBox.Show("出力先を指定してください。");
                    return;
                }

                if (!await IsCommandAvailable("ffmpeg"))
                {
                    var result = MessageBox.Show(
                        "FFmpegが見つかりません。wingetでインストールしますか？",
                        "FFmpeg未検出",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        await InstallFfmpegByWinget();
                    }
                    else
                    {
                        return;
                    }

                    if (!await IsCommandAvailable("ffmpeg"))
                    {
                        MessageBox.Show("FFmpegを検出できませんでした。インストール後、アプリを再起動してください。");
                        return;
                    }
                }

                await UpdateInputMetadata();

                string args = BuildFfmpegArgs();

                AppendLog("ffmpeg " + args);
                AppendLog("");

                ProgressBar.IsIndeterminate = true;
                StatusTextBlock.Text = "変換中...";

                int exitCode = await RunProcess("ffmpeg", args);

                ProgressBar.IsIndeterminate = false;

                if (exitCode == 0)
                {
                    // Display output file size
                    var outputFileInfo = new FileInfo(OutputPathTextBox.Text);
                    OutputSizeTextBlock.Text = FormatFileSize(outputFileInfo.Length);
                    StatusTextBlock.Text = "完了";

                    MessageBox.Show("変換が完了しました。");
                }
                else
                {
                    StatusTextBlock.Text = "失敗";
                    MessageBox.Show("変換に失敗しました。ログを確認してください。");
                }
            }
            finally
            {
                ProgressBar.IsIndeterminate = false;
                StartButton.IsEnabled = true;
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:F2} {sizes[order]}";
        }

        private async Task UpdateInputMetadata()
        {
            if (!File.Exists(InputPathTextBox.Text))
            {
                InputSizeTextBlock.Text = "-";
                DurationTextBlock.Text = "-";
                return;
            }

            var inputFileInfo = new FileInfo(InputPathTextBox.Text);
            InputSizeTextBlock.Text = FormatFileSize(inputFileInfo.Length);
            DurationTextBlock.Text = await GetVideoDuration(InputPathTextBox.Text);
        }

        private async Task<string> GetVideoDuration(string inputPath)
        {
            if (!await IsCommandAvailable("ffprobe"))
            {
                return "-";
            }

            string args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\"";
            var result = await RunProcessCapture("ffprobe", args);
            string durationText = result.Output.Trim();

            if (result.ExitCode == 0 &&
                double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                return FormatDuration(TimeSpan.FromSeconds(seconds));
            }

            return "-";
        }

        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            }

            return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
        }

        private string BuildFfmpegArgs()
        {
            string input = InputPathTextBox.Text;
            string output = OutputPathTextBox.Text;

            string codec = UseH265CheckBox.IsChecked == true ? "libx265" : "libx264";
            string preset = ((ComboBoxItem)PresetComboBox.SelectedItem).Content.ToString()!;
            int encoderThreads = GetEncoderThreadCount();

            var sb = new StringBuilder();

            sb.Append($"-y -hide_banner -nostdin -stats_period 5 -i \"{input}\" ");
            sb.Append($"-c:v {codec} -threads {encoderThreads} ");

            if (CrfRadio.IsChecked == true)
            {
                string crf = CrfTextBox.Text.Trim();
                sb.Append($"-crf {crf} ");
            }
            else
            {
                string bitrate = BitrateTextBox.Text.Trim();
                sb.Append($"-b:v {bitrate}k -maxrate {bitrate}k -bufsize {int.Parse(bitrate) * 2}k ");
            }

            sb.Append($"-preset {preset} ");
            sb.Append("-c:a copy -c:s copy ");
            sb.Append($"\"{output}\"");

            return sb.ToString();
        }

        private int GetEncoderThreadCount()
        {
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

        private async Task<int> RunProcess(string fileName, string arguments, bool logOutput = true)
        {
            var tcs = new TaskCompletionSource<int>();
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

            process.OutputDataReceived += (_, e) =>
            {
                HandleProcessLine(e.Data, logOutput, ref lineCount);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                HandleProcessLine(e.Data, logOutput, ref lineCount);
            };

            process.Exited += (_, _) =>
            {
                tcs.TrySetResult(process.ExitCode);
                process.Dispose();
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch
            {
                return -1;
            }

            return await tcs.Task;
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
                Dispatcher.BeginInvoke(() => AppendLog(data), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private bool ShouldLogLine(string data, ref int lineCount)
        {
            // Log every 10th line to reduce UI updates
            lineCount++;
            
            // Always log important messages
            if (data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                data.Contains("warning", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (data.Contains("frame=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            
            // Log periodically to show progress
            return lineCount % 10 == 0;
        }

        private void UpdateProgressFromFfmpeg(string data)
        {
            if (data.Contains("frame="))
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if (now - lastProgressUpdateUtc < TimeSpan.FromSeconds(1))
                    {
                        return;
                    }

                    lastProgressUpdateUtc = now;
                    var timeMatch = System.Text.RegularExpressions.Regex.Match(data, @"time=(\d+):(\d+):(\d+\.\d+)");
                    var speedMatch = System.Text.RegularExpressions.Regex.Match(data, @"speed=\s*([^\s]+)");
                    if (timeMatch.Success)
                    {
                        string status = $"変換中... {timeMatch.Value}";
                        if (speedMatch.Success)
                        {
                            status += $" {speedMatch.Value}";
                        }

                        Dispatcher.BeginInvoke(() =>
                        {
                            ProgressBar.IsIndeterminate = false;
                            ProgressBar.ToolTip = status;
                            StatusTextBlock.Text = status;
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
                catch { }
            }
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
