using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text.Json;
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
    private string? _lastGeneratedModel;
    private string? _lastGeneratedSettings;
    private string? _lastGeneratedWav;
    private bool _hasGeneratedAudio;
    private bool _sttActive;
    private string _ttsInput = string.Empty;
    private string _sttTranscript = string.Empty;
    private string? _selectedAudio;
    private string? _lastTranscribedAudio;
    private DateTime _lastTranscribedWriteTime;
    private string? _lastTranscribedModel;
    private Process? _sttProcess;
    private MicrophoneRecorder? _microphone;
    private string? _recordingFile;
    private bool _recording;
    private DispatcherTimer? _recordingTimer;
    private DateTime _recordingStarted;
    private string _idleStatusText = "Muhabbet Kuşu başlatılıyor…";
    private readonly ObservableCollection<GalleryItem> _galleryItems = new();
    private string SelectedModelKey => (ModelSelector.SelectedItem as PivotItem)?.Tag as string ?? "ahenk";

    public MainWindow()
    {
        InitializeComponent();
        _uiReady = true;
        ModelSelector_SelectionChanged(ModelSelector, null!);
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
                _microphone?.Dispose();
                _recordingTimer?.Stop();
                if (_sttProcess is { HasExited: false }) _sttProcess.Kill(entireProcessTree: true);
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

        if (_uiReady)
        {
            bool nextStt = SttModels.IsStt(SelectedModelKey);
            if (nextStt != _sttActive)
            {
                if (nextStt) _ttsInput = InputTextBox.Text;
                else _sttTranscript = InputTextBox.Text;
                _sttActive = nextStt;
                InputTextBox.Text = nextStt ? _sttTranscript : _ttsInput;
            }
            InputTextBox.IsReadOnly = nextStt;
            InputTextBox.PlaceholderText = nextStt ? "Ses dosyasını seçin ve Metne Dönüştür düğmesine basın." : "Seslendirmek istediğiniz Türkçe metni buraya yazın…";
            SttFilePanel.Visibility = nextStt ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(StatusProgressPanel, nextStt ? 1 : 0);
            Grid.SetColumnSpan(StatusProgressPanel, nextStt ? 1 : 2);
            ParametersCard.Visibility = nextStt ? Visibility.Collapsed : Visibility.Visible;
            SaveMp3Button.Visibility = nextStt ? Visibility.Collapsed : Visibility.Visible;
            GenerateButton.Content = nextStt ? "Metne Dönüştür" : "▶  Ses Oluştur";
        }

        bool isAntalia = SelectedModelKey == "antalia";
        bool isAhenk = SelectedModelKey == "ahenk";
        AntaliaSettingsPanel.Visibility = isAntalia || isAhenk ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(CommonParametersPanel, isAntalia || isAhenk ? 1 : 2);
        TtsSettingsTitle.Text = isAhenk ? "Ahenk Cep Ayarları" : "Antalia Özel Ayarları";
        EqSettingsPanel.Visibility = isAntalia ? Visibility.Visible : Visibility.Collapsed;
        StyleSettingsPanel.Visibility = isAhenk ? Visibility.Visible : Visibility.Collapsed;
        StepsBox.Items.Clear();
        foreach (int steps in isAhenk ? new[] { 16, 8, 4 } : new[] { 8, 4, 2, 1 })
            StepsBox.Items.Add(new ComboBoxItem { Content = steps.ToString() });
        StepsBox.SelectedIndex = 0;

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
        if (_sttActive)
        {
            var model = SttModels.ForKey(SelectedModelKey);
            EstimatedSizeText.Text = model.Attribution;
            ToolTipService.SetToolTip(EstimatedSizeText, model.License);
            return;
        }
        double speed = (double)AudioParameters.NormalizeSpeed(SpeedBox.Value, SelectedModelKey == "antalia");
        double estimatedSeconds = length / (14.0 * speed);
        double estimatedKb = (44 + estimatedSeconds * ParseSampleRate(SampleRateBox.SelectedItem) * 2) / 1024;
        EstimatedSizeText.Text = $"Tahmini WAV: {estimatedKb:N0} KB";
        ToolTipService.SetToolTip(EstimatedSizeText, "Yaklaşık 14 karakter/sn, tek kanal ve 16 bit ses hesabı. Gerçek boyut üretimden sonra belli olur.");
    }

    private void FixedSeedCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        SeedBox.IsEnabled = FixedSeedCheckBox.IsChecked == true;
        if (_uiReady) UpdatePlaybackButtons();
    }

    private void SynthesisParameter_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateInputSummary();
        UpdatePlaybackButtons();
    }

    private void SynthesisParameter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        UpdateInputSummary();
        UpdatePlaybackButtons();
    }

    private void SynthesisParameter_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        UpdatePlaybackButtons();
    }

    private string GetSynthesisSettingsKey()
    {
        bool isAntalia = SelectedModelKey == "antalia";
        bool isAhenk = SelectedModelKey == "ahenk";
        var settings = new List<string>
        {
            SelectedModelKey,
            AudioParameters.NormalizeSpeed(SpeedBox.Value, isAntalia).ToString(CultureInfo.InvariantCulture),
            ParseSampleRate(SampleRateBox.SelectedItem).ToString(CultureInfo.InvariantCulture),
            AudioParameters.NormalizeSeed(FixedSeedCheckBox.IsChecked == true, SeedBox.Value)?.ToString(CultureInfo.InvariantCulture) ?? "random"
        };

        if (isAntalia || isAhenk)
        {
            settings.Add(ParseSteps(StepsBox.SelectedItem).ToString(CultureInfo.InvariantCulture));
            settings.Add(AudioParameters.NormalizeCfg(CfgBox.Value).ToString("R", CultureInfo.InvariantCulture));
        }
        if (isAntalia)
            settings.Add(EqToggle.IsOn ? "eq:on" : "eq:off");
        if (isAhenk)
            settings.Add((StyleBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "neutral");

        return string.Join("|", settings);
    }

    private void ChooseAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _recording) return;
        try
        {
        var path = AudioFilePicker.Pick(WinRT.Interop.WindowNative.GetWindowHandle(this), GetOutputsDirectory());
        if (path == null) return;
        _selectedAudio = path;
        SttFileText.Text = Path.GetFileName(path);
        UpdatePlaybackButtons();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            StatusText.Text = "Dosya seçim penceresi açılamadı. Yeniden deneyin.";
        }
    }

    private void TranscribeGalleryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _recording || GalleryListView.SelectedItem is not GalleryItem item) return;
        if (!SttModels.IsStt(SelectedModelKey))
            ModelSelector.SelectedItem = ModelSelector.Items.OfType<PivotItem>().First(item => (string)item.Tag == "duyu");
        _selectedAudio = item.FilePath;
        SttFileText.Text = item.Name;
        UpdatePlaybackButtons();
    }

    private async void RecordAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        RecordAudioButton.IsEnabled = false;
        try
        {
            if (_recording)
            {
                await _microphone!.StopAsync();
                _recording = false;
                _selectedAudio = _recordingFile!;
                SttFileText.Text = Path.GetFileName(_recordingFile);
                _microphone.Dispose();
                _microphone = null;
                StatusText.Text = "Kayıt Hazır.";
                RefreshGallery(_selectedAudio);
            }
            else
            {
                if (!await EnsureMicrophonePermissionAsync()) return;
                _microphone = new MicrophoneRecorder();
                var recordingModel = SttModels.ForKey(SelectedModelKey).Key + "-stt";
                _recordingFile = Path.Combine(GetOutputsDirectory(), $"{recordingModel}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..4]}.wav");
                _microphone.Start(_recordingFile);
                _recording = true;
                _recordingStarted = DateTime.UtcNow;
                _recordingTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _recordingTimer.Tick -= RecordingTimer_Tick;
                _recordingTimer.Tick += RecordingTimer_Tick;
                RecordingTimeText.Text = "Kayıt Sürüyor · 0:00";
                _recordingTimer.Start();
                StatusText.Text = "● Mikrofon Kaydı Sürüyor";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _recording = false;
            _microphone?.Dispose();
            _microphone = null;
            StatusText.Text = ex is InvalidOperationException ? ex.Message : "Mikrofon kaydı başlatılamadı veya tamamlanamadı.";
            var errorDialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = "Mikrofon Kaydı Tamamlanamadı",
                Content = "Mikrofonun bağlı ve kullanılabilir olduğunu kontrol edin. Windows ayarlarında masaüstü uygulamalarına mikrofon erişimi verilmiş olmalıdır.",
                PrimaryButtonText = "Mikrofon Ayarlarını Aç", CloseButtonText = "Kapat", DefaultButton = ContentDialogButton.Close
            };
            if (await errorDialog.ShowAsync() == ContentDialogResult.Primary)
                await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));
        }
        finally
        {
            RecordAudioIcon.Glyph = _recording ? "\uE71A" : "\uE720";
            RecordingIndicator.Visibility = _recording ? Visibility.Visible : Visibility.Collapsed;
            SttFileText.Visibility = _recording ? Visibility.Collapsed : Visibility.Visible;
            if (_recording)
            {
                RecordAudioButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 185, 28, 28));
                RecordAudioButton.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            }
            else
            {
                RecordAudioButton.ClearValue(Control.BackgroundProperty);
                RecordAudioButton.ClearValue(Control.ForegroundProperty);
            }
            if (!_recording) _recordingTimer?.Stop();
            var recordAction = _recording ? "Kaydı Bitir" : "Mikrofon Kaydı";
            ToolTipService.SetToolTip(RecordAudioButton, recordAction);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RecordAudioButton, recordAction);
            RecordAudioButton.IsEnabled = true;
            ModelSelector.IsEnabled = !_recording;
            ChooseAudioButton.IsEnabled = !_recording;
            UpdatePlaybackButtons();
        }
    }

    private void RecordingTimer_Tick(object? sender, object e)
    {
        var elapsed = DateTime.UtcNow - _recordingStarted;
        RecordingTimeText.Text = $"Kayıt Sürüyor · {FormatPlaybackTime(elapsed)}";
    }

    private async Task<bool> EnsureMicrophonePermissionAsync()
    {
        var consentFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuhabbetKusu", "microphone-consent.txt");
        if (!File.Exists(consentFile))
        {
            var consent = new ContentDialog
            {
                XamlRoot = Content.XamlRoot, Title = "Mikrofon Kullanımına İzin Verilsin Mi?",
                Content = "Mikrofon yalnızca kayıt düğmesine bastığınızda kullanılır. Kayıt bilgisayarınızda saklanır ve yerel olarak metne dönüştürülür.",
                PrimaryButtonText = "İzin Ver", CloseButtonText = "Vazgeç", DefaultButton = ContentDialogButton.Close
            };
            if (await consent.ShowAsync() != ContentDialogResult.Primary) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(consentFile)!);
            await File.WriteAllTextAsync(consentFile, "allowed");
        }
        var access = Windows.Devices.Enumeration.DeviceAccessInformation.CreateFromDeviceClass(
            Windows.Devices.Enumeration.DeviceClass.AudioCapture).CurrentStatus;
        if (access is Windows.Devices.Enumeration.DeviceAccessStatus.DeniedByUser or
            Windows.Devices.Enumeration.DeviceAccessStatus.DeniedBySystem)
        {
            await ShowMicrophoneSettingsAsync();
            return false;
        }
        return true;
    }

    private async void LicensesButton_Click(object sender, RoutedEventArgs e)
    {
        var noticePath = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt");
        var text = File.Exists(noticePath) ? await File.ReadAllTextAsync(noticePath) : "Lisans dosyası bulunamadı.";
        await new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Lisanslar",
            Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } },
            CloseButtonText = "Kapat"
        }.ShowAsync();
    }

    private async Task<bool> EnsureMihuLicenseAsync()
    {
        var consent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MuhabbetKusu", "mihu-license-v1.accepted");
        if (File.Exists(consent)) return true;
        var license = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "mihu-NOTICE.txt"));
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "mihu Community License",
            Content = new ScrollViewer { MaxHeight = 400, Content = new TextBlock { Text = license, TextWrapping = TextWrapping.Wrap } },
            PrimaryButtonText = "Kabul Et", CloseButtonText = "Vazgeç", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(consent)!);
        await File.WriteAllTextAsync(consent, "Accepted " + DateTime.UtcNow.ToString("O"));
        return true;
    }

    private async Task ShowMicrophoneSettingsAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot, Title = "Mikrofon Erişimi Kapalı",
            Content = "Windows ayarlarında mikrofon erişimini ve masaüstü uygulamalarının mikrofon erişimini açın. Ardından Mikrofon Kaydı düğmesine yeniden basın.",
            PrimaryButtonText = "Ayarları Aç", CloseButtonText = "Kapat", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));
    }

    private async Task TranscribeSelectedAudioAsync()
    {
        if (string.IsNullOrEmpty(_selectedAudio) || !File.Exists(_selectedAudio)) return;
        var model = SttModels.ForKey(SelectedModelKey);
        if (model.RequiresLicenseAcceptance && !await EnsureMihuLicenseAsync()) return;
        SetBusy(true, "STT modeli hazırlanıyor…");
        ProgressPanel.Visibility = Visibility.Visible;
        SynthesisProgressBar.IsIndeterminate = true;
        ProgressPercentText.Visibility = Visibility.Collapsed;
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10));
        Process? process = null;
        try
        {
            var info = new ProcessStartInfo(FindPython())
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = AppContext.BaseDirectory
            };
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "bridge", "stt_bridge.py"));
            info.ArgumentList.Add(_selectedAudio);
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(model.Key);
            info.ArgumentList.Add("--models-dir");
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "models"));
            info.ArgumentList.Add("--packages");
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "runtime", "stt-packages"));
            var ffmpeg = Path.Combine(AppContext.BaseDirectory, "runtime", "ffmpeg", "ffmpeg.exe");
            if (!File.Exists(ffmpeg)) ffmpeg = FindOnPath("ffmpeg.exe");
            if (ffmpeg != null && File.Exists(ffmpeg)) { info.ArgumentList.Add("--ffmpeg"); info.ArgumentList.Add(ffmpeg); }
            info.Environment["PYTHONIOENCODING"] = "utf-8";
            info.Environment["PYTHONNOUSERSITE"] = "1";
            process = Process.Start(info) ?? throw new InvalidOperationException("STT başlatılamadı.");
            _sttProcess = process;
            var errors = process.StandardError.ReadToEndAsync();
            string? transcript = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var result = json.RootElement;
                if (result.TryGetProperty("message", out var message)) StatusText.Text = message.GetString();
                if (result.TryGetProperty("text", out var text)) transcript = text.GetString();
            }
            await process.WaitForExitAsync(timeout.Token);
            var stderr = await errors;
            if (process.ExitCode != 0 || transcript == null)
            {
                Debug.WriteLine(stderr);
                throw new InvalidOperationException("Ses metne dönüştürülemedi. Dosya biçimini ve internet bağlantısını kontrol edin.");
            }
            _sttTranscript = transcript;
            InputTextBox.Text = transcript;
            _lastTranscribedAudio = _selectedAudio;
            _lastTranscribedWriteTime = File.GetLastWriteTimeUtc(_selectedAudio);
            _lastTranscribedModel = SelectedModelKey;
            _idleStatusText = string.IsNullOrWhiteSpace(transcript) ? "Bu kayıtta konuşma algılanmadı." : "Metne dönüştürme tamamlandı.";
        }
        catch (Exception ex) { _idleStatusText = ex is OperationCanceledException ? "STT işlemi zaman aşımına uğradı." : ex.Message; }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            _sttProcess = null;
            process?.Dispose();
            SynthesisProgressBar.IsIndeterminate = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ProgressPercentText.Visibility = Visibility.Visible;
            SetBusy(false);
        }
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (_sttActive) { await TranscribeSelectedAudioAsync(); return; }

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
        bool isAntalia = SelectedModelKey == "antalia";
        bool isAhenk = SelectedModelKey == "ahenk";
        string modelName = SelectedModelKey;
        string fileName = $"{modelName}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..4]}.wav";
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

            int? steps = isAntalia || isAhenk ? ParseSteps(StepsBox.SelectedItem) : null;
            double? cfg = isAntalia || isAhenk ? AudioParameters.NormalizeCfg(CfgBox.Value) : null;
            bool? eq = isAntalia ? EqToggle.IsOn : null;
            string? style = isAhenk ? (StyleBox.SelectedItem as ComboBoxItem)?.Tag as string : null;

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
                style: style,
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
            _lastGeneratedModel = SelectedModelKey;
            _lastGeneratedSettings = GetSynthesisSettingsKey();
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
        if (_isBusy || _recording || GalleryListView.SelectedItem is not GalleryItem item) return;
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
        if (savePicker.SuggestedFileName.StartsWith("muhabbet_", StringComparison.OrdinalIgnoreCase))
            savePicker.SuggestedFileName = savePicker.SuggestedFileName[9..];

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
        DeleteGalleryButton.IsEnabled = !_isBusy && !_recording && GalleryListView.SelectedItem is GalleryItem selected && File.Exists(selected.FilePath);
        TranscribeGalleryButton.IsEnabled = !_isBusy && !_recording && GalleryListView.SelectedItem is GalleryItem audio && File.Exists(audio.FilePath);
        SaveMp3Button.IsEnabled = !_isBusy && _hasGeneratedAudio && hasCurrentWav;
        bool matchesLastGeneration = _lastGeneratedModel == SelectedModelKey &&
            string.Equals(_lastGeneratedText, InputTextBox.Text?.Trim(), StringComparison.Ordinal) &&
            _lastGeneratedSettings == GetSynthesisSettingsKey() &&
            !string.IsNullOrEmpty(_lastGeneratedWav) && File.Exists(_lastGeneratedWav);
        GenerateButton.IsEnabled = !_isBusy && !matchesLastGeneration && !string.IsNullOrWhiteSpace(InputTextBox.Text);
        if (_sttActive)
            GenerateButton.IsEnabled = !_isBusy && !_recording && !string.IsNullOrEmpty(_selectedAudio) && File.Exists(_selectedAudio) &&
                (_lastTranscribedModel != SelectedModelKey || _lastTranscribedAudio != _selectedAudio || _lastTranscribedWriteTime != File.GetLastWriteTimeUtc(_selectedAudio));
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _isBusy = busy;
        ModelSelector.IsEnabled = !busy;
        InputTextBox.IsEnabled = !busy;
        ChooseAudioButton.IsEnabled = !busy;
        RecordAudioButton.IsEnabled = !busy;
        SpeedBox.IsEnabled = !busy;
        SampleRateBox.IsEnabled = !busy;
        FixedSeedCheckBox.IsEnabled = !busy;
        SeedBox.IsEnabled = !busy && FixedSeedCheckBox.IsChecked == true;
        StepsBox.IsEnabled = !busy;
        CfgBox.IsEnabled = !busy;
        EqToggle.IsEnabled = !busy;
        StyleBox.IsEnabled = !busy;

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
