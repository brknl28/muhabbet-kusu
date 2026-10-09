using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MuhabbetKusu;

public sealed class GalleryItem
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastWriteTime { get; set; }
    public long FileSizeBytes { get; set; }
    public string DateFormatted => LastWriteTime.ToString("yyyy-MM-dd HH:mm");
    public string SizeFormatted => $"{FileSizeBytes / 1024:N0} KB";
}

public sealed partial class MainWindow : Window
{
    private PythonEmaBridge? _bridge;
    private Windows.Media.Playback.MediaPlayer? _mediaPlayer;
    private string? _currentWav;
    private string _pythonExe = "python";
    private bool _isBusy;
    private bool _suppressSelectionResultUpdate;
    private string _idleStatusText = "Muhabbet Kuşu başlatılıyor…";
    private readonly ObservableCollection<GalleryItem> _galleryItems = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "Muhabbet Kuşu";

        GalleryListView.ItemsSource = _galleryItems;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1060, 720));

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        UpdateTitleBarTheme();
        if (Content is FrameworkElement rootElement)
        {
            rootElement.ActualThemeChanged += (_, _) => UpdateTitleBarTheme();
        }

        RefreshGallery();
        _ = InitializeModelsAsync();

        Closed += (_, _) =>
        {
            try
            {
                _mediaPlayer?.Dispose();
                _bridge?.Dispose();
            }
            catch { }
        };
    }

    // Keep caption colors, button states, and accent colors under Windows control.
    // Only request the native dark frame when the application follows dark mode.
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, uint size);

    private void UpdateTitleBarTheme()
    {
        var elementTheme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
        int useDarkMode = elementTheme == ElementTheme.Dark ||
                          (elementTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark)
            ? 1 : 0;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        const uint DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
    }
    private async Task InitializeModelsAsync()
    {
        SetBusy(true, "Modeller yükleniyor (EMA Lightning & Antalia-2 Mini); ilk çalıştırmada ağırlıklar indirilebilir…");
        try
        {
            _pythonExe = FindPython();
            var bridgePath = Path.Combine(AppContext.BaseDirectory, "bridge", "ema_bridge.py");
            if (!File.Exists(bridgePath))
            {
                var cwdBridge = Path.Combine(Directory.GetCurrentDirectory(), "bridge", "ema_bridge.py");
                if (File.Exists(cwdBridge))
                    bridgePath = cwdBridge;
                else
                    throw new FileNotFoundException("ema_bridge.py bulunamadı.", bridgePath);
            }

            _bridge = new PythonEmaBridge(_pythonExe, bridgePath);
            var reply = await _bridge.StartAsync();
            _idleStatusText = $"Hazır · Aygıt: {reply.Device ?? "bilinmiyor"} · Modeller: EMA Lightning, Antalia-2 Mini";
            StatusText.Text = _idleStatusText;
        }
        catch (Exception ex)
        {
            _bridge?.Dispose();
            _bridge = null;
            _idleStatusText = "Modeller başlatılamadı: " + ex.Message;
            StatusText.Text = _idleStatusText;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AntaliaSettingsPanel == null) return;

        bool isAntalia = ModelSelector.SelectedIndex == 1;
        AntaliaSettingsPanel.Visibility = isAntalia ? Visibility.Visible : Visibility.Collapsed;

        if (isAntalia)
        {
            SpeedBox.Value = 0.95;
        }
        else
        {
            SpeedBox.Value = 1.00;
        }
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        int length = InputTextBox.Text?.Length ?? 0;
        CharCountText.Text = $"{length:N0} karakter";
    }

    private void FixedSeedCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        SeedBox.IsEnabled = FixedSeedCheckBox.IsChecked == true;
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var input = InputTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            ResultText.Text = "Lütfen seslendirilecek bir metin girin.";
            return;
        }

        if (_bridge is null)
        {
            await InitializeModelsAsync();
            if (_bridge is null) return;
        }

        var outputsDir = GetOutputsDirectory();
        bool isAntalia = ModelSelector.SelectedIndex == 1;
        string modelName = isAntalia ? "antalia" : "ema";
        string fileName = $"muhabbet_{modelName}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..4]}.wav";
        string output = Path.Combine(outputsDir, fileName);

        SetBusy(true, "Ses oluşturuluyor…");

        // Percentage progress bar shows during synthesis and hides when complete per R2
        ProgressPanel.Visibility = Visibility.Visible;
        UpdateProgress(0);

        try
        {
            try { _mediaPlayer?.Pause(); } catch { }
            decimal speed = AudioParameters.NormalizeSpeed(SpeedBox.Value, isAntalia);
            long? seed = AudioParameters.NormalizeSeed(FixedSeedCheckBox.IsChecked == true, SeedBox.Value);
            int sampleRate = ParseSampleRate(SampleRateBox.SelectedItem);

            int? steps = isAntalia ? ParseSteps(StepsBox.SelectedItem) : null;
            double? cfg = isAntalia ? AudioParameters.NormalizeCfg(CfgBox.Value) : null;
            bool? eq = isAntalia ? EqToggle.IsOn : null;

            var reply = await _bridge.GenerateAsync(
                modelName,
                input,
                output,
                speed,
                sampleRate,
                seed,
                steps,
                cfg,
                eq,
                onProgress: pct =>
                {
                    DispatcherQueue.TryEnqueue(() => UpdateProgress(pct));
                });

            if (!reply.Ok)
                throw new InvalidOperationException(reply.Error ?? "Ses üretilemedi.");

            UpdateProgress(100);

            _currentWav = output;
            _suppressSelectionResultUpdate = true;
            try
            {
                RefreshGallery(output);
            }
            finally
            {
                _suppressSelectionResultUpdate = false;
            }

            ResultText.Text = $"Hazır · {reply.Duration:0.00} sn · {reply.SampleRate} Hz · seed {reply.Seed}";
            PlayButton.IsEnabled = StopButton.IsEnabled = SaveWavButton.IsEnabled = SaveMp3Button.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ResultText.Text = $"Üretim başarısız: {ex.Message}";
        }
        finally
        {
            // Hide progress bar when synthesis is finished per R2
            ProgressPanel.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void UpdateProgress(int pct)
    {
        int clamped = Math.Clamp(pct, 0, 100);
        SynthesisProgressBar.Value = clamped;
        ProgressPercentText.Text = $"%{clamped}";
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => PlayCurrent();

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _mediaPlayer?.Pause();
            if (_mediaPlayer?.PlaybackSession != null)
            {
                _mediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
            }
        }
        catch { }
    }

    private void PlayGalleryButton_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryListView.SelectedItem is GalleryItem item && File.Exists(item.FilePath))
        {
            _currentWav = item.FilePath;
        }
        PlayCurrent();
    }

    private void RefreshGalleryButton_Click(object sender, RoutedEventArgs e) => RefreshGallery();

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = GetOutputsDirectory();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void GalleryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GalleryListView.SelectedItem is GalleryItem item && File.Exists(item.FilePath))
        {
            _currentWav = item.FilePath;
            if (!_suppressSelectionResultUpdate)
            {
                ResultText.Text = "Ses seçildi.";
            }
        }
        UpdatePlaybackButtons();
    }

    private void GalleryListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (GalleryListView.SelectedItem is GalleryItem item && File.Exists(item.FilePath))
        {
            _currentWav = item.FilePath;
            PlayCurrent();
        }
    }

    private void GalleryListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            if (GalleryListView.SelectedItem is GalleryItem item && File.Exists(item.FilePath))
            {
                _currentWav = item.FilePath;
                PlayCurrent();
                e.Handled = true;
            }
        }
    }

    private void PlayCurrent()
    {
        if (string.IsNullOrEmpty(_currentWav) || !File.Exists(_currentWav)) return;
        try
        {
            _mediaPlayer?.Dispose();
            _mediaPlayer = new Windows.Media.Playback.MediaPlayer();
            _mediaPlayer.MediaFailed += (_, args) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ResultText.Text = $"Oynatma hatası: {args.ErrorMessage}";
                });
            };
            _mediaPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(Path.GetFullPath(_currentWav)));
            _mediaPlayer.Play();
        }
        catch (Exception ex)
        {
            ResultText.Text = $"Oynatma hatası: {ex.Message}";
        }
    }

    private async void SaveWavButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || string.IsNullOrEmpty(_currentWav) || !File.Exists(_currentWav)) return;
        try
        {
            var savePicker = new Windows.Storage.Pickers.FileSavePicker();
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hWnd == IntPtr.Zero) return;
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);
            savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            savePicker.FileTypeChoices.Add("WAV ses dosyası", new List<string> { ".wav" });
            savePicker.SuggestedFileName = AudioParameters.GetSuggestedFileName(_currentWav);

            var file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                if (!string.Equals(Path.GetFullPath(_currentWav), Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(_currentWav, file.Path, overwrite: true);
                }
                ResultText.Text = "WAV kaydedildi.";
            }
        }
        catch (Exception ex)
        {
            ResultText.Text = $"WAV kaydetme hatası: {ex.Message}";
        }
    }

    private async void SaveMp3Button_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || string.IsNullOrEmpty(_currentWav) || !File.Exists(_currentWav)) return;

        var ffmpeg = FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
        if (ffmpeg is null)
        {
            ResultText.Text = "MP3 için FFmpeg bulunamadı (winget install Gyan.FFmpeg).";
            return;
        }

        var savePicker = new Windows.Storage.Pickers.FileSavePicker();
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hWnd == IntPtr.Zero) return;
        WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hWnd);
        savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
        savePicker.FileTypeChoices.Add("MP3 ses dosyası", new List<string> { ".mp3" });
        savePicker.SuggestedFileName = AudioParameters.GetSuggestedFileName(_currentWav);

        var file = await savePicker.PickSaveFileAsync();
        if (file == null) return;

        if (string.Equals(Path.GetFullPath(_currentWav), Path.GetFullPath(file.Path), StringComparison.OrdinalIgnoreCase))
        {
            ResultText.Text = "MP3 hedef dosyası kaynak WAV dosyası ile aynı olamaz.";
            return;
        }

        SetBusy(true, "MP3'e dönüştürülüyor…");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-y -hide_banner -loglevel error -i \"{_currentWav}\" -codec:a libmp3lame -q:a 2 \"{file.Path}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg başlatılamadı.");
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error);

            ResultText.Text = "MP3 kaydedildi.";
        }
        catch (Exception ex)
        {
            ResultText.Text = $"MP3 dönüşüm hatası: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshGallery(string? selectFilePath = null)
    {
        selectFilePath ??= _currentWav;
        _galleryItems.Clear();

        var dir = GetOutputsDirectory();
        if (!Directory.Exists(dir))
        {
            _currentWav = null;
            UpdatePlaybackButtons();
            return;
        }

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dir)
                .GetFiles("*.wav")
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
        }
        catch
        {
            files = new List<FileInfo>();
        }

        GalleryItem? itemToSelect = null;
        foreach (var fi in files)
        {
            var item = new GalleryItem
            {
                Name = fi.Name,
                FilePath = fi.FullName,
                LastWriteTime = fi.LastWriteTime,
                FileSizeBytes = fi.Length
            };

            if (selectFilePath != null && string.Equals(fi.FullName, selectFilePath, StringComparison.OrdinalIgnoreCase))
            {
                itemToSelect = item;
            }

            _galleryItems.Add(item);
        }

        if (itemToSelect != null)
        {
            GalleryListView.SelectedItem = itemToSelect;
            _currentWav = itemToSelect.FilePath;
            GalleryListView.ScrollIntoView(itemToSelect);
        }
        else if (_galleryItems.Count > 0)
        {
            GalleryListView.SelectedItem = _galleryItems[0];
            _currentWav = _galleryItems[0].FilePath;
            GalleryListView.ScrollIntoView(_galleryItems[0]);
        }
        else
        {
            GalleryListView.SelectedItem = null;
            _currentWav = null;
        }

        UpdatePlaybackButtons();
    }

    private void UpdatePlaybackButtons()
    {
        bool hasCurrentWav = !string.IsNullOrEmpty(_currentWav) &&
                             File.Exists(_currentWav);
        bool hasGallerySelection = GalleryListView.SelectedItem is GalleryItem sel &&
                                   File.Exists(sel.FilePath);

        PlayButton.IsEnabled = hasCurrentWav;
        StopButton.IsEnabled = hasCurrentWav;
        SaveWavButton.IsEnabled = !_isBusy && hasCurrentWav;
        SaveMp3Button.IsEnabled = !_isBusy && hasCurrentWav;
        PlayGalleryButton.IsEnabled = hasGallerySelection;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        GenerateButton.IsEnabled = !busy;
        ModelSelector.IsEnabled = !busy;
        InputTextBox.IsEnabled = !busy;
        SpeedBox.IsEnabled = !busy;
        SampleRateBox.IsEnabled = !busy;
        FixedSeedCheckBox.IsEnabled = !busy;
        SeedBox.IsEnabled = !busy && FixedSeedCheckBox.IsChecked == true;
        StepsBox.IsEnabled = !busy;
        CfgBox.IsEnabled = !busy;
        EqToggle.IsEnabled = !busy;

        UpdatePlaybackButtons();

        if (busy)
        {
            if (!string.IsNullOrWhiteSpace(message))
                StatusText.Text = message;
        }
        else
        {
            StatusText.Text = !string.IsNullOrWhiteSpace(message) ? message : _idleStatusText;
        }
    }

    private static int ParseSteps(object? item)
    {
        if (item is ComboBoxItem cbi)
            return AudioParameters.ParseSteps(cbi.Content);
        return AudioParameters.ParseSteps(item);
    }

    internal static int ParseSampleRate(object? item)
    {
        if (item is ComboBoxItem cbi)
            return AudioParameters.ParseSampleRate(cbi.Content);
        return AudioParameters.ParseSampleRate(item);
    }

    private static string GetOutputsDirectory()
    {
        string cwdOutputs = Path.Combine(Directory.GetCurrentDirectory(), "outputs");
        if (Directory.Exists(cwdOutputs) || File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "MuhabbetKusu.csproj")))
        {
            Directory.CreateDirectory(cwdOutputs);
            return cwdOutputs;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MuhabbetKusu.csproj")))
            {
                string projOutputs = Path.Combine(dir.FullName, "outputs");
                Directory.CreateDirectory(projOutputs);
                return projOutputs;
            }
            dir = dir.Parent;
        }

        string fallback = Path.Combine(AppContext.BaseDirectory, "outputs");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string FindPython()
    {
        var candidates = new[] { "python.exe", "python", "py.exe", "py" };
        foreach (var candidate in candidates)
        {
            var found = FindOnPath(candidate);
            if (found is not null)
            {
                if (Path.GetFileNameWithoutExtension(found).Equals("py", StringComparison.OrdinalIgnoreCase))
                    continue;
                return found;
            }
        }
        return "python";
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
