using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MkvBitrateChanger
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void SelectInput_Click(object sender, RoutedEventArgs e)
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
            InputSizeTextBlock.Text = "-";
            OutputSizeTextBlock.Text = "-";

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

                // Display input file size
                var inputFileInfo = new FileInfo(InputPathTextBox.Text);
                InputSizeTextBlock.Text = FormatFileSize(inputFileInfo.Length);

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

                string args = BuildFfmpegArgs();

                AppendLog("ffmpeg " + args);
                AppendLog("");

                ProgressBar.IsIndeterminate = true;

                int exitCode = await RunProcess("ffmpeg", args);

                ProgressBar.IsIndeterminate = false;

                if (exitCode == 0)
                {
                    // Display output file size
                    var outputFileInfo = new FileInfo(OutputPathTextBox.Text);
                    OutputSizeTextBlock.Text = FormatFileSize(outputFileInfo.Length);

                    MessageBox.Show("変換が完了しました。");
                }
                else
                {
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

        private string BuildFfmpegArgs()
        {
            string input = InputPathTextBox.Text;
            string output = OutputPathTextBox.Text;

            string codec = UseH265CheckBox.IsChecked == true ? "libx265" : "libx264";
            string preset = ((ComboBoxItem)PresetComboBox.SelectedItem).Content.ToString()!;

            var sb = new StringBuilder();

            sb.Append($"-y -i \"{input}\" ");
            sb.Append($"-c:v {codec} ");

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
                if (logOutput && e.Data != null)
                {
                    // Filter output: only log important messages, not every frame
                    if (ShouldLogLine(e.Data, ref lineCount))
                    {
                        Dispatcher.Invoke(() => AppendLog(e.Data), System.Windows.Threading.DispatcherPriority.Background);
                    }
                    
                    // Update progress bar with ffmpeg progress information
                    UpdateProgressFromFfmpeg(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (logOutput && e.Data != null)
                {
                    // Filter output: only log important messages
                    if (ShouldLogLine(e.Data, ref lineCount))
                    {
                        Dispatcher.Invoke(() => AppendLog(e.Data), System.Windows.Threading.DispatcherPriority.Background);
                    }
                    
                    // Update progress bar with ffmpeg progress information
                    UpdateProgressFromFfmpeg(e.Data);
                }
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

        private bool ShouldLogLine(string data, ref int lineCount)
        {
            // Log every 10th line to reduce UI updates
            lineCount++;
            
            // Always log important messages
            if (data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                data.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                data.Contains("frame=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            
            // Log periodically to show progress
            return lineCount % 10 == 0;
        }

        private void UpdateProgressFromFfmpeg(string data)
        {
            // Extract frame information from ffmpeg output for progress indication
            // Format: frame= 1234 fps= 45 q=-1.0 Lsize=   1234kB time=00:00:27.48 bitrate=367.8kbps speed=1.5x
            if (data.Contains("frame="))
            {
                try
                {
                    // Simple heuristic: update progress based on time output
                    var timeMatch = System.Text.RegularExpressions.Regex.Match(data, @"time=(\d+):(\d+):(\d+\.\d+)");
                    if (timeMatch.Success)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ProgressBar.IsIndeterminate = false;
                            // Show time as status (could be enhanced with duration calculation)
                            ProgressBar.ToolTip = timeMatch.Value;
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
                catch { }
            }
        }

        private void AppendLog(string text)
        {
            LogTextBox.AppendText(text + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        }
    }
}
