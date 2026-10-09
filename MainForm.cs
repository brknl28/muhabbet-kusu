using System.Diagnostics;
using System.Media;

namespace MuhabbetKusu;

public sealed class MainForm : Form
{
    private readonly ComboBox _modelSelector = new();
    private readonly TextBox _text = new();
    private readonly Label _charCount = new();
    private readonly NumericUpDown _speed = new();
    private readonly ComboBox _sampleRate = new();
    private readonly CheckBox _fixedSeed = new();
    private readonly NumericUpDown _seed = new();

    // Antalia-specific parameter controls
    private readonly FlowLayoutPanel _antaliaSettingsPanel = new();
    private readonly ComboBox _steps = new();
    private readonly NumericUpDown _cfg = new();
    private readonly CheckBox _eqToggle = new();

    private readonly Label _modelInfo = new();

    private readonly Button _generate = new();
    private readonly Button _play = new();
    private readonly Button _stop = new();
    private readonly Button _saveWav = new();
    private readonly Button _saveMp3 = new();

    private readonly ProgressBar _progress = new();
    private readonly Label _progressPercent = new();
    private readonly TableLayoutPanel _progressPanel = new();
    private readonly Label _status = new();
    private readonly Label _result = new();

    // History Gallery controls
    private readonly ListView _galleryList = new();
    private readonly Button _playGalleryBtn = new();
    private readonly Button _refreshGalleryBtn = new();
    private readonly Button _openFolderBtn = new();

    private PythonEmaBridge? _bridge;
    private SoundPlayer? _player;
    private string? _currentWav;
    private string _pythonExe = "python";
    private bool _suppressSelectionResultUpdate;
    private bool _isBusy;

    public MainForm()
    {
        Text = "Muhabbet Kuşu";
        Width = 1060;
        Height = 720;
        MinimumSize = new Size(880, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);

        BuildUi();

        Shown += async (_, _) =>
        {
            RefreshGallery();
            await InitializeModelsAsync();
        };

        FormClosing += (_, _) =>
        {
            _player?.Stop();
            _player?.Dispose();
            _bridge?.Dispose();
        };
    }

    private void BuildUi()
    {
        var splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical
        };
        Controls.Add(splitContainer);
        try
        {
            if (ClientSize.Width > 500)
                splitContainer.SplitterDistance = Math.Max(100, Math.Min(ClientSize.Width - 260, 640));
        }
        catch { }

        // --- Panel 1: Studio / Generation Controls ---
        var leftRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 8,
            AutoScroll = true
        };
        leftRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 0: Heading
        leftRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 1: Model selection
        leftRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 2: Text input
        leftRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 3: Parameters
        leftRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 4: Model info
        leftRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 5: Action buttons
        leftRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 6: Progress
        leftRoot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 7: Footer
        splitContainer.Panel1.Controls.Add(leftRoot);

        // Heading
        var title = new Label
        {
            Text = "Muhabbet Kuşu",
            AutoSize = true,
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 2)
        };
        var subtitle = new Label
        {
            Text = "EMA Lightning ve Antalia-2 Mini ile yerel Türkçe metinden sese",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 10)
        };
        var heading = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill
        };
        heading.Controls.Add(title);
        heading.Controls.Add(subtitle);
        leftRoot.Controls.Add(heading, 0, 0);

        // Model Selection
        var modelPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        var modelLabel = new Label
        {
            Text = "Model:",
            AutoSize = true,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Padding = new Padding(0, 5, 4, 0)
        };
        _modelSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _modelSelector.Items.AddRange(["EMA Lightning", "Antalia-2 Mini"]);
        _modelSelector.SelectedIndex = 0;
        _modelSelector.Width = 160;
        _modelSelector.SelectedIndexChanged += (_, _) => OnModelChanged();
        modelPanel.Controls.Add(modelLabel);
        modelPanel.Controls.Add(_modelSelector);
        leftRoot.Controls.Add(modelPanel, 0, 1);

        // Text input
        var textPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        textPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        textPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _text.Multiline = true;
        _text.ScrollBars = ScrollBars.Vertical;
        _text.Dock = DockStyle.Fill;
        _text.Font = new Font("Segoe UI", 11.5f);
        _text.PlaceholderText = "Seslendirmek istediğiniz Türkçe metni buraya yazın…";
        _text.Text = "Merhaba, Muhabbet Kuşu çalışıyor. Bu ses tamamen bilgisayarınızda üretildi.";
        _text.TextChanged += (_, _) => UpdateCharCount();
        _charCount.AutoSize = true;
        _charCount.Anchor = AnchorStyles.Right;
        _charCount.ForeColor = Color.DimGray;
        _charCount.Margin = new Padding(0, 4, 0, 6);
        textPanel.Controls.Add(_text, 0, 0);
        textPanel.Controls.Add(_charCount, 0, 1);
        leftRoot.Controls.Add(textPanel, 0, 2);
        UpdateCharCount();

        // Parameter Settings Panel
        var settingsContainer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 4, 0, 6)
        };

        var commonSettings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };
        commonSettings.Controls.Add(new Label { Text = "Hız", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _speed.DecimalPlaces = 2;
        _speed.Increment = 0.05m;
        _speed.Minimum = 0.25m;
        _speed.Maximum = 4m;
        _speed.Value = 1m;
        _speed.Width = 75;
        commonSettings.Controls.Add(_speed);

        commonSettings.Controls.Add(new Label { Text = "  Örnekleme", AutoSize = true, Padding = new Padding(6, 6, 4, 0) });
        _sampleRate.DropDownStyle = ComboBoxStyle.DropDownList;
        _sampleRate.Items.AddRange(["48000", "24000", "16000", "8000"]);
        _sampleRate.SelectedIndex = 0;
        _sampleRate.Width = 85;
        commonSettings.Controls.Add(_sampleRate);

        _fixedSeed.Text = "Sabit seed";
        _fixedSeed.AutoSize = true;
        _fixedSeed.Padding = new Padding(8, 4, 0, 0);
        _fixedSeed.CheckedChanged += (_, _) => _seed.Enabled = _fixedSeed.Checked;
        commonSettings.Controls.Add(_fixedSeed);
        _seed.Minimum = 0;
        _seed.Maximum = int.MaxValue;
        _seed.Value = 0;
        _seed.Width = 95;
        _seed.Enabled = false;
        commonSettings.Controls.Add(_seed);
        settingsContainer.Controls.Add(commonSettings, 0, 0);

        // Antalia specific settings
        _antaliaSettingsPanel.Dock = DockStyle.Fill;
        _antaliaSettingsPanel.AutoSize = true;
        _antaliaSettingsPanel.WrapContents = true;
        _antaliaSettingsPanel.Margin = new Padding(0, 4, 0, 0);
        _antaliaSettingsPanel.Visible = false;

        _antaliaSettingsPanel.Controls.Add(new Label { Text = "Adım (Steps)", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _steps.DropDownStyle = ComboBoxStyle.DropDownList;
        _steps.Items.AddRange(["8", "4", "2", "1"]);
        _steps.SelectedIndex = 0;
        _steps.Width = 65;
        _antaliaSettingsPanel.Controls.Add(_steps);

        _antaliaSettingsPanel.Controls.Add(new Label { Text = "  CFG", AutoSize = true, Padding = new Padding(6, 6, 4, 0) });
        _cfg.DecimalPlaces = 1;
        _cfg.Increment = 0.1m;
        _cfg.Minimum = 0.5m;
        _cfg.Maximum = 10m;
        _cfg.Value = 2.0m;
        _cfg.Width = 65;
        _antaliaSettingsPanel.Controls.Add(_cfg);

        _eqToggle.Text = "EQ Ton Filtresi";
        _eqToggle.AutoSize = true;
        _eqToggle.Checked = true;
        _eqToggle.Padding = new Padding(8, 4, 0, 0);
        _antaliaSettingsPanel.Controls.Add(_eqToggle);

        settingsContainer.Controls.Add(_antaliaSettingsPanel, 0, 1);
        leftRoot.Controls.Add(settingsContainer, 0, 3);

        // Model Info
        _modelInfo.Text = "EMA Lightning: Tek sesli, ultra hızlı yerel Türkçe konuşma sentezi.";
        _modelInfo.AutoSize = true;
        _modelInfo.ForeColor = Color.DarkGoldenrod;
        _modelInfo.Margin = new Padding(0, 0, 0, 8);
        leftRoot.Controls.Add(_modelInfo, 0, 4);

        // Action Buttons
        var mainButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        _generate.Text = "▶  Sesi Oluştur";
        _generate.AutoSize = true;
        _generate.Padding = new Padding(12, 6, 12, 6);
        _generate.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _generate.Click += async (_, _) => await GenerateAsync();
        mainButtons.Controls.Add(_generate);

        _play.Text = "Oynat";
        _play.AutoSize = true;
        _play.Enabled = false;
        _play.Padding = new Padding(10, 6, 10, 6);
        _play.Click += (_, _) => PlayCurrent();
        mainButtons.Controls.Add(_play);

        _stop.Text = "Durdur";
        _stop.AutoSize = true;
        _stop.Enabled = false;
        _stop.Padding = new Padding(10, 6, 10, 6);
        _stop.Click += (_, _) => { _player?.Stop(); };
        mainButtons.Controls.Add(_stop);

        _saveWav.Text = "WAV Kaydet…";
        _saveWav.AutoSize = true;
        _saveWav.Enabled = false;
        _saveWav.Padding = new Padding(10, 6, 10, 6);
        _saveWav.Click += (_, _) => SaveWav();
        mainButtons.Controls.Add(_saveWav);

        _saveMp3.Text = "MP3 Kaydet…";
        _saveMp3.AutoSize = true;
        _saveMp3.Enabled = false;
        _saveMp3.Padding = new Padding(10, 6, 10, 6);
        _saveMp3.Click += async (_, _) => await SaveMp3Async();
        mainButtons.Controls.Add(_saveMp3);
        leftRoot.Controls.Add(mainButtons, 0, 5);

        // Percentage Progress Bar Row
        _progressPanel.Dock = DockStyle.Fill;
        _progressPanel.AutoSize = true;
        _progressPanel.ColumnCount = 2;
        _progressPanel.RowCount = 1;
        _progressPanel.Margin = new Padding(0, 2, 0, 6);
        _progressPanel.Visible = false;
        _progressPanel.ColumnStyles.Clear();
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Value = 0;
        _progress.Dock = DockStyle.Fill;
        _progress.Height = 18;

        _progressPercent.Text = "%0";
        _progressPercent.AutoSize = true;
        _progressPercent.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _progressPercent.ForeColor = Color.SteelBlue;
        _progressPercent.Padding = new Padding(6, 0, 0, 0);
        _progressPercent.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        _progressPanel.Controls.Add(_progress, 0, 0);
        _progressPanel.Controls.Add(_progressPercent, 1, 0);
        leftRoot.Controls.Add(_progressPanel, 0, 6);

        // Status & Result Footer
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };
        _status.AutoSize = true;
        _status.Text = "Muhabbet Kuşu başlatılıyor…";
        _status.ForeColor = Color.DimGray;
        _result.AutoSize = true;
        _result.Text = "Henüz ses oluşturulmadı.";
        _result.ForeColor = Color.DimGray;
        _result.Margin = new Padding(0, 4, 0, 0);
        footer.Controls.Add(_status);
        footer.Controls.Add(_result);
        leftRoot.Controls.Add(footer, 0, 7);

        // --- Panel 2: Local Output History Gallery ---
        var rightGroup = new GroupBox
        {
            Text = "Üretilen Sesler Galerisi (outputs/)",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            Font = new Font("Segoe UI", 9.5f)
        };
        splitContainer.Panel2.Controls.Add(rightGroup);

        var galleryLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        galleryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        galleryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rightGroup.Controls.Add(galleryLayout);

        _galleryList.View = View.Details;
        _galleryList.FullRowSelect = true;
        _galleryList.MultiSelect = false;
        _galleryList.GridLines = true;
        _galleryList.Dock = DockStyle.Fill;
        _galleryList.Columns.Add("Dosya", 160);
        _galleryList.Columns.Add("Tarih", 110);
        _galleryList.Columns.Add("Boyut", 65);
        _galleryList.SelectedIndexChanged += (_, _) => OnGallerySelectionChanged();
        _galleryList.DoubleClick += (_, _) =>
        {
            if (_galleryList.SelectedItems.Count > 0)
            {
                var path = _galleryList.SelectedItems[0].Tag as string;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    _currentWav = path;
                    PlayCurrent();
                }
            }
        };
        _galleryList.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                if (_galleryList.SelectedItems.Count > 0)
                {
                    var path = _galleryList.SelectedItems[0].Tag as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        _currentWav = path;
                }
                PlayCurrent();
                e.Handled = true;
            }
        };
        galleryLayout.Controls.Add(_galleryList, 0, 0);

        var galleryButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0)
        };
        _playGalleryBtn.Text = "▶ Seçileni Oynat";
        _playGalleryBtn.AutoSize = true;
        _playGalleryBtn.Enabled = false;
        _playGalleryBtn.Padding = new Padding(8, 4, 8, 4);
        _playGalleryBtn.Click += (_, _) =>
        {
            if (_galleryList.SelectedItems.Count > 0)
            {
                var path = _galleryList.SelectedItems[0].Tag as string;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    _currentWav = path;
            }
            PlayCurrent();
        };
        galleryButtons.Controls.Add(_playGalleryBtn);

        _refreshGalleryBtn.Text = "🔄 Yenile";
        _refreshGalleryBtn.AutoSize = true;
        _refreshGalleryBtn.Padding = new Padding(8, 4, 8, 4);
        _refreshGalleryBtn.Click += (_, _) => RefreshGallery();
        galleryButtons.Controls.Add(_refreshGalleryBtn);

        _openFolderBtn.Text = "📁 Klasörü Aç";
        _openFolderBtn.AutoSize = true;
        _openFolderBtn.Padding = new Padding(8, 4, 8, 4);
        _openFolderBtn.Click += (_, _) => OpenOutputsFolder();
        galleryButtons.Controls.Add(_openFolderBtn);

        galleryLayout.Controls.Add(galleryButtons, 0, 1);
    }

    private void OnModelChanged()
    {
        bool isAntalia = _modelSelector.SelectedIndex == 1;
        _antaliaSettingsPanel.Visible = isAntalia;

        if (isAntalia)
        {
            _speed.Value = 0.95m;
            _modelInfo.Text = "Antalia-2 Mini (cloud0day3/antalia-mini): Hızlı difüzyon, adımlar (8, 4, 2, 1), CFG ve EQ ton filtresi.";
        }
        else
        {
            _speed.Value = 1.00m;
            _modelInfo.Text = "EMA Lightning: Tek sesli, ultra hızlı yerel Türkçe konuşma sentezi.";
        }
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
                // Fallback to current directory bridge if running during dev
                var cwdBridge = Path.Combine(Directory.GetCurrentDirectory(), "bridge", "ema_bridge.py");
                if (File.Exists(cwdBridge))
                    bridgePath = cwdBridge;
                else
                    throw new FileNotFoundException("ema_bridge.py bulunamadı.", bridgePath);
            }

            _bridge = new PythonEmaBridge(_pythonExe, bridgePath);
            var reply = await _bridge.StartAsync();
            _status.Text = $"Hazır · Python: {_pythonExe} · Aygıt: {reply.Device ?? "bilinmiyor"} · Modeller: EMA Lightning, Antalia-2 Mini";
            _status.ForeColor = Color.DarkGreen;
        }
        catch (Exception ex)
        {
            _bridge?.Dispose();
            _bridge = null;
            _status.Text = "Modeller başlatılamadı";
            _status.ForeColor = Color.DarkRed;
            MessageBox.Show(
                "Muhabbet Kuşu Python tarafını başlatamadı.\n\n" + ex.Message +
                "\n\nPowerShell'de şunların kurulu olduğunu kontrol edin:\npython -m pip show ema-lightning antalia-mini",
                "Muhabbet Kuşu",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task GenerateAsync()
    {
        if (_isBusy) return;

        var input = _text.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            MessageBox.Show("Önce seslendirilecek metni girin.", "Muhabbet Kuşu", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_bridge is null)
        {
            await InitializeModelsAsync();
            if (_bridge is null) return;
        }

        var outputsDir = GetOutputsDirectory();
        bool isAntalia = _modelSelector.SelectedIndex == 1;
        string modelName = isAntalia ? "antalia" : "ema";
        string fileName = $"muhabbet_{modelName}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..4]}.wav";
        string output = Path.Combine(outputsDir, fileName);

        SetBusy(true, "Ses oluşturuluyor…");
        UpdateProgress(0);

        try
        {
            _player?.Stop();
            long? seed = _fixedSeed.Checked ? (long)_seed.Value : null;
            int sampleRate = ParseSampleRate(_sampleRate.SelectedItem);

            int? steps = isAntalia
                ? (_steps.SelectedItem is string stStr && int.TryParse(stStr, out var stVal) ? stVal : 8)
                : null;
            double? cfg = isAntalia ? (double)_cfg.Value : null;
            bool? eq = isAntalia ? _eqToggle.Checked : null;

            var reply = await _bridge.GenerateAsync(
                modelName,
                input,
                output,
                _speed.Value,
                sampleRate,
                seed,
                steps,
                cfg,
                eq,
                onProgress: (pct) =>
                {
                    if (IsDisposed || Disposing) return;
                    if (InvokeRequired)
                        BeginInvoke(() => UpdateProgress(pct));
                    else
                        UpdateProgress(pct);
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

            _result.Text = $"Hazır · {reply.Duration:0.00} sn · {reply.SampleRate} Hz · seed {reply.Seed} · {Path.GetFileName(output)}";
            _result.ForeColor = Color.DarkGreen;
            _play.Enabled = _stop.Enabled = _saveWav.Enabled = _saveMp3.Enabled = true;
        }
        catch (Exception ex)
        {
            UpdateProgress(0);
            _result.Text = "Üretim başarısız.";
            _result.ForeColor = Color.DarkRed;
            MessageBox.Show(ex.Message, "Ses üretilemedi", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateProgress(int pct)
    {
        if (IsDisposed || Disposing) return;
        int clamped = Math.Clamp(pct, 0, 100);
        if (clamped >= _progress.Value || clamped == 0)
        {
            _progress.Value = clamped;
            _progressPercent.Text = $"%{clamped}";
        }
    }

    private void OnGallerySelectionChanged()
    {
        if (_galleryList.SelectedItems.Count > 0)
        {
            var path = _galleryList.SelectedItems[0].Tag as string;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _currentWav = path;
                UpdateGalleryPlaybackButtons();
                if (!_suppressSelectionResultUpdate)
                {
                    _result.Text = $"Seçildi · {Path.GetFileName(path)}";
                    _result.ForeColor = Color.DarkSlateGray;
                }
                return;
            }
        }
        UpdateGalleryPlaybackButtons();
    }

    private void RefreshGallery(string? selectFilePath = null)
    {
        selectFilePath ??= _currentWav;
        _galleryList.Items.Clear();
        var dir = GetOutputsDirectory();
        if (!Directory.Exists(dir))
        {
            _currentWav = null;
            UpdateGalleryPlaybackButtons();
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

        if (files.Count == 0)
        {
            _currentWav = null;
            UpdateGalleryPlaybackButtons();
            return;
        }

        ListViewItem? itemToSelect = null;
        foreach (var fi in files)
        {
            var item = new ListViewItem(fi.Name);
            item.SubItems.Add(fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
            item.SubItems.Add($"{fi.Length / 1024:N0} KB");
            item.Tag = fi.FullName;

            if (selectFilePath != null && string.Equals(fi.FullName, selectFilePath, StringComparison.OrdinalIgnoreCase))
            {
                itemToSelect = item;
            }

            _galleryList.Items.Add(item);
        }

        if (itemToSelect != null)
        {
            itemToSelect.Selected = true;
            itemToSelect.EnsureVisible();
            _currentWav = itemToSelect.Tag as string;
        }
        else if (_galleryList.Items.Count > 0)
        {
            _galleryList.Items[0].Selected = true;
            _galleryList.Items[0].EnsureVisible();
            _currentWav = _galleryList.Items[0].Tag as string;
        }

        UpdateGalleryPlaybackButtons();
    }

    private void UpdateGalleryPlaybackButtons()
    {
        bool hasSelection = !string.IsNullOrEmpty(_currentWav) &&
                            File.Exists(_currentWav) &&
                            (_galleryList.SelectedItems.Count > 0 || _galleryList.Items.Count > 0);
        _play.Enabled = hasSelection;
        _stop.Enabled = hasSelection;
        _saveWav.Enabled = !_isBusy && hasSelection;
        _saveMp3.Enabled = !_isBusy && hasSelection;
        _playGalleryBtn.Enabled = hasSelection;
    }

    private void OpenOutputsFolder()
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
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Klasör açılamadı", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string GetOutputsDirectory()
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

    private void PlayCurrent()
    {
        if (_currentWav is null || !File.Exists(_currentWav)) return;
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _player = new SoundPlayer(_currentWav);
            _player.Play();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Oynatma hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveWav()
    {
        if (_isBusy || _currentWav is null || !File.Exists(_currentWav)) return;
        try
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "WAV ses dosyası|*.wav",
                FileName = Path.GetFileName(_currentWav),
                RestoreDirectory = true
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                File.Copy(_currentWav, dialog.FileName, overwrite: true);
                _result.Text = $"WAV kaydedildi · {Path.GetFileName(dialog.FileName)}";
                _result.ForeColor = Color.DarkGreen;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "WAV kaydetme hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SaveMp3Async()
    {
        if (_isBusy || _currentWav is null || !File.Exists(_currentWav)) return;

        var ffmpeg = FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg");
        if (ffmpeg is null)
        {
            var result = MessageBox.Show(
                "MP3 dönüşümü için FFmpeg bulunamadı.\n\nPowerShell'de şu komutla kurabilirsiniz:\nwinget install Gyan.FFmpeg\n\nKomutu panoya kopyalayayım mı?",
                "FFmpeg gerekli",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (result == DialogResult.Yes)
                Clipboard.SetText("winget install Gyan.FFmpeg");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "MP3 ses dosyası|*.mp3",
            FileName = Path.GetFileNameWithoutExtension(_currentWav) + ".mp3",
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        SetBusy(true, "MP3'e dönüştürülüyor…");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-y -hide_banner -loglevel error -i \"{_currentWav}\" -codec:a libmp3lame -q:a 2 \"{dialog.FileName}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg başlatılamadı.");
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(error);

            _result.Text = $"MP3 kaydedildi · {dialog.FileName}";
            _result.ForeColor = Color.DarkGreen;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "MP3 dönüşüm hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        if (IsDisposed || Disposing) return;
        _isBusy = busy;
        _progressPanel.Visible = busy;
        if (busy)
        {
            _progress.Value = 0;
            _progressPercent.Text = "%0";
        }

        _generate.Enabled = !busy;
        _modelSelector.Enabled = !busy;
        _text.Enabled = !busy;
        _speed.Enabled = !busy;
        _sampleRate.Enabled = !busy;
        _fixedSeed.Enabled = !busy;
        _seed.Enabled = !busy && _fixedSeed.Checked;
        _steps.Enabled = !busy;
        _cfg.Enabled = !busy;
        _eqToggle.Enabled = !busy;

        UpdateGalleryPlaybackButtons();

        if (!string.IsNullOrWhiteSpace(message))
        {
            _status.Text = message;
            _status.ForeColor = Color.DimGray;
        }
    }

    internal static int ParseSampleRate(object? item)
    {
        if (item is string s)
        {
            s = s.Trim().ToLowerInvariant();
            if (s.EndsWith("k"))
            {
                if (double.TryParse(s[..^1], out var kVal))
                    return (int)(kVal * 1000);
            }
            if (s.EndsWith("hz"))
            {
                s = s[..^2].Trim();
            }
            if (int.TryParse(s, out var val))
                return val;
        }
        return 48000;
    }

    private void UpdateCharCount() => _charCount.Text = $"{_text.TextLength:N0} karakter";

    private static string FindPython()
    {
        var candidates = new[] { "python.exe", "python", "py.exe", "py" };
        foreach (var candidate in candidates)
        {
            var found = FindOnPath(candidate);
            if (found is not null)
            {
                if (Path.GetFileNameWithoutExtension(found).Equals("py", StringComparison.OrdinalIgnoreCase))
                    continue; // py launcher needs different args; prefer actual python executable.
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

    // Internal accessors for unit testing
    internal ComboBox ModelSelector => _modelSelector;
    internal NumericUpDown SpeedControl => _speed;
    internal ComboBox SampleRateControl => _sampleRate;
    internal ComboBox StepsControl => _steps;
    internal NumericUpDown CfgControl => _cfg;
    internal CheckBox EqToggleControl => _eqToggle;
    internal CheckBox FixedSeedControl => _fixedSeed;
    internal NumericUpDown SeedControl => _seed;
    internal ProgressBar ProgressBarControl => _progress;
    internal Label ProgressPercentLabel => _progressPercent;
    internal ListView GalleryListControl => _galleryList;
    internal FlowLayoutPanel AntaliaSettingsPanel => _antaliaSettingsPanel;
    internal bool IsAntaliaModelSelected => _modelSelector.SelectedIndex == 1;
    internal bool IsAntaliaSettingsActive => _modelSelector.SelectedIndex == 1;
    internal Button PlayButton => _play;
    internal Button StopButton => _stop;
    internal Button GenerateButton => _generate;
    internal Button PlayGalleryButton => _playGalleryBtn;
    internal string OutputsDirectory => GetOutputsDirectory();
    internal string? CurrentWav => _currentWav;
    internal Button SaveWavButton => _saveWav;
    internal Button SaveMp3Button => _saveMp3;
    internal bool IsBusy => _isBusy;
    internal void SetBusyForTesting(bool busy) => SetBusy(busy);
    internal void SetCurrentWavForTesting(string wavPath) => _currentWav = wavPath;
    internal void RefreshGalleryForTesting(string? selectFilePath = null) => RefreshGallery(selectFilePath);
    internal void SelectModelForTesting(int index) => _modelSelector.SelectedIndex = index;
    internal void UpdateProgressForTesting(int pct) => UpdateProgress(pct);
}
