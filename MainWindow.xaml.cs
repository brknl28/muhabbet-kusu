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
    private bool _uiReady;
    private string? _lastGeneratedText;
    private int _lastGeneratedModel = -1;
    private string? _lastGeneratedWav;
    private bool _hasGeneratedAudio;
    private string _idleStatusText = "Muhabbet Kuşu başlatılıyor…";
    private readonly ObservableCollection<GalleryItem> _galleryItems = new();

    public MainWindow()
    {
        InitializeComponent();
        _uiReady = true;
        UpdateInputSummary();
        _mediaPlayer = new Windows.Media.Playback.MediaPlayer { AutoPlay = false };
        InitializePlaybackControls();
        _mediaPlayer.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() => StatusText.Text = "Ses oynatılamadı.");
        Title = "Muhabbet Kuşu";

        GalleryListView.ItemsSource = _galleryItems;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 720));

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
        SetBusy(true, "Ses modelleri hazırlanıyor. İlk açılışta internet bağlantısı gerekebilir…");
        ProgressPanel.Visibility = Visibility.Visible;
        SynthesisProgressBar.IsIndeterminate = true;
        ProgressPercentText.Visibility = Visibility.Collapsed;
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
            var reply = await _bridge.StartAsync(onStatus: message =>
                DispatcherQueue.TryEnqueue(() => StatusText.Text = message));
            _idleStatusText = $"Hazır · Aygıt: {reply.Device ?? "bilinmiyor"}";
            StatusText.Text = _idleStatusText;
        }
        catch (Exception ex)
        {
            _bridge?.Dispose();
            _bridge = null;
            Debug.WriteLine(ex);
            _idleStatusText = "Ses modelleri hazırlanamadı. İnternet bağlantınızı kontrol edip Sesi Oluştur ile yeniden deneyin.";
            StatusText.Text = _idleStatusText;
        }
        finally
        {
            SynthesisProgressBar.IsIndeterminate = false;
            ProgressPercentText.Visibility = Visibility.Visible;
            ProgressPanel.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AntaliaSettingsPanel == null) return;

        bool isAntalia = ModelSelector.SelectedIndex == 1;
        AntaliaSettingsPanel.Visibility = isAntalia ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(CommonParametersPanel, isAntalia ? 1 : 2);

        if (isAntalia)
        {
            SpeedBox.Value = 0.95;
        }
        else
        {
            SpeedBox.Value = 1.00;
        }
        if (_uiReady) { UpdateInputSummary(); UpdatePlaybackButtons(); }
    }

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateInputSummary();
        UpdatePlaybackButtons();
    }

    private void UpdateInputSummary()
    {
        int length = InputTextBox.Text?.Length ?? 0;
        CharCountText.Text = $"{length:N0} Karakter";
        double speed = (double)AudioParameters.NormalizeSpeed(SpeedBox.Value, ModelSelector.SelectedIndex == 1);
        double estimatedSeconds = length / (14.0 * speed);
        double estimatedKb = (44 + estimatedSeconds * ParseSampleRate(SampleRateBox.SelectedItem) * 2) / 1024;
        EstimatedSizeText.Text = $"Tahmini WAV: {estimatedKb:N0} KB";
        ToolTipService.SetToolTip(EstimatedSizeText, "Yaklaşık 14 karakter/sn, tek kanal ve 16 bit ses hesabı. Gerçek boyut üretimden sonra belli olur.");
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
            StatusText.Text = "Lütfen seslendirilecek bir metin girin.";
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
            RefreshGallery(output);

            StatusText.Text = $"Hazır · {reply.Duration:0.00} sn · {reply.SampleRate} Hz · seed {reply.Seed}";
            _lastGeneratedText = input;
            _lastGeneratedModel = isAntalia ? 1 : 0;
            _lastGeneratedWav = output;
            _hasGeneratedAudio = true;
            EstimatedSizeText.Text = $"WAV: {new FileInfo(output).Length / 1024.0:N0} KB";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Üretim başarısız: {ex.Message}";
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

    private string? _loadedWav;
    private DispatcherTimer? _playbackTimer;
    private bool _updatingPlayback;

    private void InitializePlaybackControls()
    {
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _playbackTimer.Tick += (_, _) => RefreshPlaybackControls();
        _playbackTimer.Start();
        Closed += (_, _) => _playbackTimer.Stop();
    }

    private void RefreshPlaybackControls()
    {
        if (_mediaPlayer == null) return;
        var session = _mediaPlayer.PlaybackSession;
        bool playing = session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
        PlaybackIcon.Glyph = playing ? "\uE769" : "\uE768";
        string action = playing ? "Duraklat" : "Oynat";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlaybackButton, action);
        ToolTipService.SetToolTip(PlaybackButton, action);
        _updatingPlayback = true;
        try
        {
            PlaybackSlider.Maximum = Math.Max(1, session.NaturalDuration.TotalSeconds);
            PlaybackSlider.Value = session.Position.TotalSeconds;
            PlaybackTime.Text = $"{FormatPlaybackTime(session.Position)} / {FormatPlaybackTime(session.NaturalDuration)}";
            PlaybackSlider.IsEnabled = session.NaturalDuration.TotalSeconds > 0;
        }
        finally { _updatingPlayback = false; }
    }

    private static string FormatPlaybackTime(TimeSpan value) =>
        $"{(int)value.TotalMinutes}:{value.Seconds:00}";

    private void PlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer?.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
            _mediaPlayer.Pause();
        else PlayCurrent();
        RefreshPlaybackControls();
    }

    private void PlaybackSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_updatingPlayback && _mediaPlayer?.PlaybackSession.NaturalDuration.TotalSeconds > 0)
            _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.IsMuted = ((Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)sender).IsChecked == true;
        VolumeIcon.Glyph = _mediaPlayer.IsMuted ? "\uE74F" : "\uE767";
        var action = _mediaPlayer.IsMuted ? "Sesi aç" : "Sesi kapat";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName((DependencyObject)sender, action);
        ToolTipService.SetToolTip((DependencyObject)sender, action);
    }

    private void RefreshGalleryButton_Click(object sender, RoutedEventArgs e) => RefreshGallery();

    private async void DeleteGalleryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || GalleryListView.SelectedItem is not GalleryItem item) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Ses silinsin mi?",
            Content = $"{item.Name}\nBu işlem geri alınamaz.",
            PrimaryButtonText = "Sil",
            CloseButtonText = "Vazgeç",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            if (_loadedWav == item.FilePath)
            {
                _mediaPlayer?.Pause();
                if (_mediaPlayer != null) _mediaPlayer.Source = null;
                _loadedWav = null;
            }
            File.Delete(item.FilePath);
            if (_currentWav == item.FilePath) _currentWav = null;
            RefreshGallery();
        }
        catch (Exception ex) { StatusText.Text = $"Ses silinemedi: {ex.Message}"; }
    }

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
            LoadSelectedAudio();
            _mediaPlayer?.Play();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Oynatma hatası: {ex.Message}";
        }
    }

    private void LoadSelectedAudio()
    {
        if (_loadedWav == _currentWav) return;
        _mediaPlayer?.Pause();
        _loadedWav = _currentWav;
        if (_mediaPlayer != null)
            _mediaPlayer.Source = string.IsNullOrEmpty(_currentWav) || !File.Exists(_currentWav)
                ? null : Windows.Media.Core.MediaSource.CreateFromUri(new Uri(Path.GetFullPath(_currentWav)));
    }

    private async void SaveMp3Button_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || string.IsNullOrEmpty(_currentWav) || !File.Exists(_currentWav)) return;

        var bundledFfmpeg = Path.Combine(AppContext.BaseDirectory, "runtime", "ffmpeg", "ffmpeg.exe");
        var ffmpeg = File.Exists(bundledFfmpeg) ? bundledFfmpeg : FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
        if (ffmpeg is null)
        {
            StatusText.Text = "MP3 için FFmpeg bulunamadı (winget install Gyan.FFmpeg).";
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
            StatusText.Text = "MP3 hedef dosyası kaynak WAV dosyası ile aynı olamaz.";
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

            StatusText.Text = "MP3 kaydedildi.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"MP3 dönüşüm hatası: {ex.Message}";
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

        LoadSelectedAudio();
        PlaybackButton.IsEnabled = hasCurrentWav;
        DeleteGalleryButton.IsEnabled = !_isBusy && GalleryListView.SelectedItem is GalleryItem selected && File.Exists(selected.FilePath);
        SaveMp3Button.IsEnabled = !_isBusy && _hasGeneratedAudio && hasCurrentWav;
        bool matchesLastGeneration = _lastGeneratedModel == ModelSelector.SelectedIndex &&
            string.Equals(_lastGeneratedText, InputTextBox.Text?.Trim(), StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(_lastGeneratedWav) && File.Exists(_lastGeneratedWav);
        GenerateButton.IsEnabled = !_isBusy && !matchesLastGeneration && !string.IsNullOrWhiteSpace(InputTextBox.Text);
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
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
        var bundledPython = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
        if (File.Exists(bundledPython)) return bundledPython;
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
