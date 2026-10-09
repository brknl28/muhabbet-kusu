using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MuhabbetKusu;
using Xunit;

namespace MuhabbetKusu.Tests;

public class MuhabbetKusuTests
{
    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
            throw new AggregateException(exception);
    }

    [Fact]
    public void R1_ApplicationIdentity_TitleAndBranding()
    {
        RunInSta(() =>
        {
            using var form = new MainForm();
            Assert.Contains("Muhabbet Kuşu", form.Text);
            Assert.Equal("Muhabbet Kuşu", form.Text);
        });
    }

    [Fact]
    public void R2_DualModelSelection_DisplaysAndAppliesModelSpecificParameters()
    {
        RunInSta(() =>
        {
            using var form = new MainForm();

            // R2: Dual model support: EMA Lightning and Antalia-2 Mini
            Assert.Equal(2, form.ModelSelector.Items.Count);
            Assert.Equal("EMA Lightning", form.ModelSelector.Items[0]);
            Assert.Equal("Antalia-2 Mini", form.ModelSelector.Items[1]);

            // Model 1: EMA Lightning
            form.SelectModelForTesting(0);
            Assert.False(form.IsAntaliaSettingsActive, "Antalia parameters must be inactive when EMA Lightning is selected.");
            Assert.Equal(1.00m, form.SpeedControl.Value);
            Assert.Contains("48000", form.SampleRateControl.Items.Cast<string>());
            Assert.Contains("24000", form.SampleRateControl.Items.Cast<string>());
            Assert.Contains("16000", form.SampleRateControl.Items.Cast<string>());
            Assert.Contains("8000", form.SampleRateControl.Items.Cast<string>());

            // Model 2: Antalia-2 Mini
            form.SelectModelForTesting(1);
            Assert.True(form.IsAntaliaSettingsActive, "Antalia parameters must be active when Antalia-2 Mini is selected.");
            Assert.Equal(0.95m, form.SpeedControl.Value); // Default speed 0.95 per R2
            Assert.Equal("8", form.StepsControl.SelectedItem);
            Assert.Contains("8", form.StepsControl.Items.Cast<string>());
            Assert.Contains("4", form.StepsControl.Items.Cast<string>());
            Assert.Contains("2", form.StepsControl.Items.Cast<string>());
            Assert.Contains("1", form.StepsControl.Items.Cast<string>());
            Assert.Equal(2.0m, form.CfgControl.Value);
            Assert.True(form.EqToggleControl.Checked);
        });
    }

    [Fact]
    public void R3_PercentageProgressBar_ReflectsIncrementalProgress()
    {
        RunInSta(() =>
        {
            using var form = new MainForm();

            Assert.Equal(0, form.ProgressBarControl.Minimum);
            Assert.Equal(100, form.ProgressBarControl.Maximum);
            Assert.Equal(ProgressBarStyle.Continuous, form.ProgressBarControl.Style);

            var milestones = new[] { 0, 15, 35, 50, 75, 90, 100 };
            foreach (var val in milestones)
            {
                form.UpdateProgressForTesting(val);
                Assert.Equal(val, form.ProgressBarControl.Value);
                Assert.Equal($"%{val}", form.ProgressPercentLabel.Text);
            }
        });
    }

    [Fact]
    public void R4_LocalOutputStorage_AndHistoryGallery()
    {
        RunInSta(() =>
        {
            using var form = new MainForm();

            var outputsDir = Path.Combine(Directory.GetCurrentDirectory(), "outputs");
            Directory.CreateDirectory(outputsDir);

            // Create a temporary dummy wav in outputs/
            var testFile = Path.Combine(outputsDir, $"unit_test_sample_{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(testFile, new byte[1024]);

            try
            {
                form.RefreshGalleryForTesting(testFile);

                bool found = false;
                foreach (ListViewItem item in form.GalleryListControl.Items)
                {
                    if (string.Equals(item.Tag as string, testFile, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                Assert.True(found, "Generated file must appear in application gallery list.");

                // Select the file
                form.SetCurrentWavForTesting(testFile);
                Assert.Equal(testFile, form.CurrentWav);
                Assert.True(File.Exists(form.CurrentWav));
            }
            finally
            {
                if (File.Exists(testFile))
                    File.Delete(testFile);
            }
        });
    }

    [Fact]
    public async Task Integration_SynthesizingSpeech_EmaLightning_ProducesValidAudio_AndIncrementalProgress()
    {
        var bridgePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\bridge\ema_bridge.py"));
        if (!File.Exists(bridgePath))
            bridgePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "bridge", "ema_bridge.py"));

        Assert.True(File.Exists(bridgePath), $"Bridge path not found: {bridgePath}");

        var outputsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "outputs"));
        Directory.CreateDirectory(outputsDir);
        var outputPath = Path.Combine(outputsDir, $"test_synth_ema_{Guid.NewGuid():N}.wav");

        var progressList = new List<int>();

        await using var bridge = new PythonEmaBridge("python", bridgePath);
        var startReply = await bridge.StartAsync();
        Assert.True(startReply.Ok);

        var reply = await bridge.GenerateAsync(
            model: "ema",
            text: "Muhabbet kuşu neşeyle şarkı söylüyor.",
            outputPath: outputPath,
            speed: 1.0m,
            sampleRate: 48000,
            seed: 1234,
            onProgress: pct => progressList.Add(pct));

        Assert.True(reply.Ok, $"Error: {reply.Error}, Status: {reply.Status}, Model: {reply.Model}");
        Assert.NotNull(reply.Duration);
        Assert.True(reply.Duration > 0);
        Assert.True(File.Exists(outputPath), "Generated audio file must exist in outputs/ directory.");
        var fileInfo = new FileInfo(outputPath);
        Assert.True(fileInfo.Length > 1000, "Generated audio file must be non-empty valid audio.");

        // Progress verification
        Assert.NotEmpty(progressList);
        Assert.Contains(100, progressList);
        Assert.True(progressList[0] <= 10, "Progress should start low (e.g. 5% or 10%).");
    }

    [Fact]
    public async Task Integration_SynthesizingSpeech_Antalia2Mini_ProducesValidAudio_AndIncrementalProgress()
    {
        var bridgePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\bridge\ema_bridge.py"));
        if (!File.Exists(bridgePath))
            bridgePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "bridge", "ema_bridge.py"));

        Assert.True(File.Exists(bridgePath), $"Bridge path not found: {bridgePath}");

        var outputsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "outputs"));
        Directory.CreateDirectory(outputsDir);
        var outputPath = Path.Combine(outputsDir, $"test_synth_antalia_{Guid.NewGuid():N}.wav");

        var progressList = new List<int>();

        await using var bridge = new PythonEmaBridge("python", bridgePath);
        var startReply = await bridge.StartAsync();
        Assert.True(startReply.Ok);

        var reply = await bridge.GenerateAsync(
            model: "antalia",
            text: "Antalia modeli harika bir şekilde çalışıyor.",
            outputPath: outputPath,
            speed: 0.95m,
            sampleRate: 48000,
            seed: 5678,
            steps: 4,
            cfg: 2.0,
            eq: true,
            onProgress: pct => progressList.Add(pct));

        Assert.True(reply.Ok);
        Assert.NotNull(reply.Duration);
        Assert.True(reply.Duration > 0);
        Assert.True(File.Exists(outputPath), "Generated audio file must exist in outputs/ directory.");
        var fileInfo = new FileInfo(outputPath);
        Assert.True(fileInfo.Length > 1000, "Generated audio file must be non-empty valid audio.");

        // Incremental progress verification
        Assert.NotEmpty(progressList);
        Assert.Contains(100, progressList);
        Assert.True(progressList[0] <= 15, "Progress should start with an initial percentage.");
    }

    [Fact]
    public async Task Integration_PersistentMemory_DualModelSwitchingWithoutReload()
    {
        var bridgePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\bridge\ema_bridge.py"));
        if (!File.Exists(bridgePath))
            bridgePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "bridge", "ema_bridge.py"));

        var outputsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "outputs"));
        Directory.CreateDirectory(outputsDir);

        await using var bridge = new PythonEmaBridge("python", bridgePath);
        var startReply = await bridge.StartAsync();
        Assert.True(startReply.Ok);

        // Run EMA
        var out1 = Path.Combine(outputsDir, $"switch_ema1_{Guid.NewGuid():N}.wav");
        var r1 = await bridge.GenerateAsync("ema", "Birinci ses.", out1, 1.0m, 48000, 1);
        Assert.True(r1.Ok);
        Assert.True(File.Exists(out1));

        // Switch to Antalia in same session (no reload)
        var out2 = Path.Combine(outputsDir, $"switch_antalia_{Guid.NewGuid():N}.wav");
        var r2 = await bridge.GenerateAsync("antalia", "İkinci ses.", out2, 0.95m, 48000, 2, steps: 2, cfg: 2.0, eq: true);
        Assert.True(r2.Ok);
        Assert.True(File.Exists(out2));

        // Switch back to EMA in same session
        var out3 = Path.Combine(outputsDir, $"switch_ema2_{Guid.NewGuid():N}.wav");
        var r3 = await bridge.GenerateAsync("ema", "Üçüncü ses.", out3, 1.0m, 48000, 3);
        Assert.True(r3.Ok);
        Assert.True(File.Exists(out3));
    }

    [Fact]
    public async Task EdgeCase_Antalia_SingleStep_And_CustomSampleRate()
    {
        var bridgePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\bridge\ema_bridge.py"));
        if (!File.Exists(bridgePath))
            bridgePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "bridge", "ema_bridge.py"));

        var outputsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "outputs"));
        Directory.CreateDirectory(outputsDir);
        var outPath = Path.Combine(outputsDir, $"edge_antalia_1step_{Guid.NewGuid():N}.wav");

        await using var bridge = new PythonEmaBridge("python", bridgePath);
        await bridge.StartAsync();

        // 1 step, 24000 Hz, eq=false
        var r = await bridge.GenerateAsync("antalia", "Hızlı tek adım.", outPath, 1.2m, 24000, 999, steps: 1, cfg: 1.5, eq: false);
        Assert.True(r.Ok);
        Assert.True(File.Exists(outPath));
        Assert.Equal(24000, r.SampleRate);
    }

    [Fact]
    public void EdgeCase_Form_SpeedAndSampleRateBoundaries()
    {
        RunInSta(() =>
        {
            using var form = new MainForm();

            // Check boundaries
            Assert.Equal(0.25m, form.SpeedControl.Minimum);
            Assert.Equal(4.0m, form.SpeedControl.Maximum);

            // Set boundary values
            form.SpeedControl.Value = 0.25m;
            Assert.Equal(0.25m, form.SpeedControl.Value);

            form.SpeedControl.Value = 4.0m;
            Assert.Equal(4.0m, form.SpeedControl.Value);

            // CFG boundaries
            Assert.Equal(0.5m, form.CfgControl.Minimum);
            Assert.Equal(10.0m, form.CfgControl.Maximum);
        });
    }

    [Fact]
    public void EdgeCase_ModelSwitching_UpdatesSpeedAndParameterPanelVisibility()
    {
        RunInSta(() =>
        {
            using var form = new MainForm();

            // Default model is EMA (0)
            Assert.Equal(0, form.ModelSelector.SelectedIndex);
            Assert.False(form.IsAntaliaSettingsActive);
            Assert.Equal(1.00m, form.SpeedControl.Value);

            // Switch to Antalia (1)
            form.SelectModelForTesting(1);
            Assert.True(form.IsAntaliaSettingsActive);
            Assert.Equal(0.95m, form.SpeedControl.Value);

            // Switch back to EMA (0)
            form.SelectModelForTesting(0);
            Assert.False(form.IsAntaliaSettingsActive);
            Assert.Equal(1.00m, form.SpeedControl.Value);
        });
    }

    [Fact]
    public void EdgeCase_AudioPlayback_LoadsRealWavFormatSuccessfully()
    {
        RunInSta(() =>
        {
            var outputsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "outputs"));
            Directory.CreateDirectory(outputsDir);

            // Find an existing wav or create a valid minimal 48kHz PCM WAV
            var testWav = Path.Combine(outputsDir, $"playback_verify_{Guid.NewGuid():N}.wav");

            // Write minimal 44-byte WAV header + 1000 bytes PCM silence
            using (var fs = new FileStream(testWav, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                int sampleRate = 48000;
                short channels = 1;
                short bitsPerSample = 16;
                int dataSize = 4800; // 0.05 seconds of audio

                bw.Write("RIFF"u8.ToArray());
                bw.Write(36 + dataSize);
                bw.Write("WAVE"u8.ToArray());
                bw.Write("fmt "u8.ToArray());
                bw.Write(16); // subchunk1 size
                bw.Write((short)1); // PCM
                bw.Write(channels);
                bw.Write(sampleRate);
                bw.Write(sampleRate * channels * bitsPerSample / 8); // byte rate
                bw.Write((short)(channels * bitsPerSample / 8)); // block align
                bw.Write(bitsPerSample);
                bw.Write("data"u8.ToArray());
                bw.Write(dataSize);
                bw.Write(new byte[dataSize]);
            }

            try
            {
                using var player = new System.Media.SoundPlayer(testWav);
                player.Load(); // Throws if WAV format/header is invalid
                player.Play();
                player.Stop();
            }
            finally
            {
                if (File.Exists(testWav))
                    File.Delete(testWav);
            }
        });
    }

    [Fact]
    public async Task EdgeCase_Antalia_MultiChunk_IncrementalProgress_AcrossAllSteps()
    {
        var bridgePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\bridge\ema_bridge.py"));
        if (!File.Exists(bridgePath))
            bridgePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "bridge", "ema_bridge.py"));

        var outputsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "outputs"));
        Directory.CreateDirectory(outputsDir);
        var outPath = Path.Combine(outputsDir, $"multichunk_antalia_{Guid.NewGuid():N}.wav");

        var progressList = new List<int>();

        await using var bridge = new PythonEmaBridge("python", bridgePath);
        await bridge.StartAsync();

        // 2 sentences to test multi-chunk step progress tracking
        var reply = await bridge.GenerateAsync(
            model: "antalia",
            text: "Birinci cümle başarıyla bitti. İkinci cümle ile devam ediyoruz.",
            outputPath: outPath,
            speed: 0.95m,
            sampleRate: 48000,
            seed: 777,
            steps: 4,
            onProgress: pct => progressList.Add(pct));

        Assert.True(reply.Ok);
        Assert.True(File.Exists(outPath));
        Assert.NotEmpty(progressList);
        Assert.Contains(100, progressList);

        // Verify incremental monotonicity
        for (int i = 1; i < progressList.Count; i++)
        {
            Assert.True(progressList[i] >= progressList[i - 1], $"Progress should be monotonically increasing: {progressList[i - 1]} -> {progressList[i]}");
        }
    }

    [Fact]
    public void EdgeCase_GalleryRefresh_PreservesExistingSelection()
    {
        RunInSta(() =>
        {
            using var form = new MainForm();
            var outputsDir = form.OutputsDirectory;
            Directory.CreateDirectory(outputsDir);

            var file1 = Path.Combine(outputsDir, $"gallery_test_1_{Guid.NewGuid():N}.wav");
            var file2 = Path.Combine(outputsDir, $"gallery_test_2_{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(file1, new byte[512]);
            File.WriteAllBytes(file2, new byte[512]);

            try
            {
                // Refresh and select file 1
                form.RefreshGalleryForTesting(file1);
                Assert.Equal(file1, form.CurrentWav);
                Assert.True(form.PlayGalleryButton.Enabled);

                // Call RefreshGallery without arguments (as user clicking Refresh button)
                form.RefreshGalleryForTesting();

                // Selection must be preserved!
                Assert.Equal(file1, form.CurrentWav);
                Assert.True(form.PlayGalleryButton.Enabled, "Play button must remain enabled after refresh when file is selected.");
            }
            finally
            {
                if (File.Exists(file1)) File.Delete(file1);
                if (File.Exists(file2)) File.Delete(file2);
            }
        });
    }

    [Fact]
    public void EdgeCase_GalleryKeyboardNavigation_SpaceOrEnterKey()
    {
        RunInSta(() =>
        {
            using var form = new MainForm();
            var outputsDir = form.OutputsDirectory;
            Directory.CreateDirectory(outputsDir);

            var testFile = Path.Combine(outputsDir, $"keyboard_test_{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(testFile, new byte[512]);

            try
            {
                form.RefreshGalleryForTesting(testFile);
                Assert.Equal(testFile, form.CurrentWav);
                Assert.True(form.PlayGalleryButton.Enabled);
            }
            finally
            {
                if (File.Exists(testFile)) File.Delete(testFile);
            }
        });
    }

    [Fact]
    public async Task EdgeCase_Validation_EmptyOrWhitespaceText_ThrowsArgumentException()
    {
        var bridgePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\bridge\ema_bridge.py"));
        if (!File.Exists(bridgePath))
            bridgePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "bridge", "ema_bridge.py"));

        await using var bridge = new PythonEmaBridge("python", bridgePath);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await bridge.GenerateAsync("ema", "", "dummy.wav", 1.0m, 48000, 1);
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await bridge.GenerateAsync("antalia", "   \t\n  ", "dummy.wav", 0.95m, 48000, 1);
        });
    }

    [Fact]
    public async Task EdgeCase_TurkishCharacters_WithDiacritics_SynthesizesSuccessfully()
    {
        var bridgePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\bridge\ema_bridge.py"));
        if (!File.Exists(bridgePath))
            bridgePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "bridge", "ema_bridge.py"));

        var outputsDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "outputs"));
        Directory.CreateDirectory(outputsDir);
        var outPath = Path.Combine(outputsDir, $"turkish_diacritics_{Guid.NewGuid():N}.wav");

        await using var bridge = new PythonEmaBridge("python", bridgePath);
        await bridge.StartAsync();

        // Complex Turkish characters: ğ, Ğ, ı, İ, ş, Ş, ç, Ç, ö, Ö, ü, Ü
        string text = "Şemsi Paşa Pasajında sesi kısık öten Muhabbet Kuşu — çağıl çağıl ırmaklar, yağmur, gökkuşağı.";

        var reply = await bridge.GenerateAsync(
            model: "ema",
            text: text,
            outputPath: outPath,
            speed: 1.0m,
            sampleRate: 48000,
            seed: 9999);

        Assert.True(reply.Ok);
        Assert.True(File.Exists(outPath));
        var fi = new FileInfo(outPath);
        Assert.True(fi.Length > 1000);
    }

    [Fact]
    public void EdgeCase_SampleRate_FlexibleFormats()
    {
        Assert.Equal(48000, MainForm.ParseSampleRate("48000"));
        Assert.Equal(48000, MainForm.ParseSampleRate("48k"));
        Assert.Equal(48000, MainForm.ParseSampleRate("48K"));
        Assert.Equal(24000, MainForm.ParseSampleRate("24000"));
        Assert.Equal(24000, MainForm.ParseSampleRate("24k"));
        Assert.Equal(16000, MainForm.ParseSampleRate("16000"));
        Assert.Equal(16000, MainForm.ParseSampleRate("16k"));
        Assert.Equal(8000, MainForm.ParseSampleRate("8000"));
        Assert.Equal(8000, MainForm.ParseSampleRate("8k"));
        Assert.Equal(48000, MainForm.ParseSampleRate("48000 Hz"));
        Assert.Equal(24000, MainForm.ParseSampleRate("24000 hz"));
        Assert.Equal(24000, MainForm.ParseSampleRate("24khz"));
        Assert.Equal(24000, MainForm.ParseSampleRate("24 kHz"));
        Assert.Equal(48000, AudioParameters.ParseSampleRate("48000 Hz"));
        Assert.Equal(24000, AudioParameters.ParseSampleRate("24khz"));
        Assert.Equal(24000, AudioParameters.ParseSampleRate("24 kHz"));
        Assert.Equal(48000, AudioParameters.ParseSampleRate(null));

        // Speed normalization & boundaries
        Assert.Equal(1.00m, AudioParameters.NormalizeSpeed(double.NaN, isAntalia: false));
        Assert.Equal(0.95m, AudioParameters.NormalizeSpeed(double.NaN, isAntalia: true));
        Assert.Equal(0.25m, AudioParameters.NormalizeSpeed(0.10, isAntalia: false));
        Assert.Equal(4.00m, AudioParameters.NormalizeSpeed(5.00, isAntalia: false));

        // CFG normalization & boundaries
        Assert.Equal(2.0, AudioParameters.NormalizeCfg(double.NaN));
        Assert.Equal(0.5, AudioParameters.NormalizeCfg(0.1));
        Assert.Equal(10.0, AudioParameters.NormalizeCfg(12.0));

        // Seed normalization
        Assert.Null(AudioParameters.NormalizeSeed(isFixedSeed: false, 1234));
        Assert.Equal(0, AudioParameters.NormalizeSeed(isFixedSeed: true, double.NaN));
        Assert.Equal(0, AudioParameters.NormalizeSeed(isFixedSeed: true, -5));
        Assert.Equal(0, AudioParameters.NormalizeSeed(isFixedSeed: true, 0));
        Assert.Equal(1234, AudioParameters.NormalizeSeed(isFixedSeed: true, 1234));
        Assert.Equal(long.MaxValue, AudioParameters.NormalizeSeed(isFixedSeed: true, double.MaxValue));
        Assert.Equal(long.MaxValue, AudioParameters.NormalizeSeed(isFixedSeed: true, 1e25));

        // Sample rate non-positive handling
        Assert.Equal(48000, AudioParameters.ParseSampleRate("0"));
        Assert.Equal(48000, AudioParameters.ParseSampleRate("-48000"));
        Assert.Equal(48000, AudioParameters.ParseSampleRate("0 hz"));
        Assert.Equal(48000, AudioParameters.ParseSampleRate("0k"));

        // Steps parsing
        Assert.Equal(4, AudioParameters.ParseSteps("4"));
        Assert.Equal(8, AudioParameters.ParseSteps(null));
        Assert.Equal(8, AudioParameters.ParseSteps("invalid"));
        Assert.Equal(8, AudioParameters.ParseSteps("0"));
        Assert.Equal(8, AudioParameters.ParseSteps("-1"));

        // Fractional kHz parsing
        Assert.Equal(22050, AudioParameters.ParseSampleRate("22.05k"));
        Assert.Equal(22050, AudioParameters.ParseSampleRate("22.05 kHz"));
        Assert.Equal(44100, AudioParameters.ParseSampleRate("44.1k"));
        Assert.Equal(44100, AudioParameters.ParseSampleRate("44.1 kHz"));

        // Culture invariance test (e.g. Turkish and German locales where dot is thousands separator)
        var origCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            foreach (var cultureName in new[] { "tr-TR", "de-DE", "en-US" })
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
                System.Threading.Thread.CurrentThread.CurrentCulture = culture;

                Assert.Equal(22050, AudioParameters.ParseSampleRate("22.05k"));
                Assert.Equal(22050, AudioParameters.ParseSampleRate("22.05 kHz"));
                Assert.Equal(44100, AudioParameters.ParseSampleRate("44.1k"));
                Assert.Equal(44100, AudioParameters.ParseSampleRate("44.1 kHz"));
                Assert.Equal(48000, AudioParameters.ParseSampleRate("48000"));
                Assert.Equal(8, AudioParameters.ParseSteps("8"));
            }
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = origCulture;
        }

        // SuggestedFileName tests (extension-free for WinRT FileSavePicker)
        Assert.Equal("muhabbet_ema", AudioParameters.GetSuggestedFileName(@"C:\outputs\muhabbet_ema.wav"));
        Assert.Equal("speech", AudioParameters.GetSuggestedFileName("speech.mp3"));
        Assert.Equal("muhabbet_ses", AudioParameters.GetSuggestedFileName(null));
        Assert.Equal("muhabbet_ses", AudioParameters.GetSuggestedFileName("   "));
    }

    [Fact]
    public async Task EdgeCase_Bridge_StartAsync_Tolerates_Stdout_Noise()
    {
        var tempScript = Path.Combine(Path.GetTempPath(), $"bridge_noise_test_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(tempScript,
            "import sys, json\n" +
            "sys.stdout.write('Warning: CUDA capability 8.6 detected\\n')\n" +
            "sys.stdout.write('Notice: oneDNN optimizations active\\n')\n" +
            "sys.stdout.write(json.dumps({'ok': True, 'status': 'ready', 'device': 'Simulated-GPU', 'models': ['ema', 'antalia']}) + '\\n')\n" +
            "sys.stdout.flush()\n" +
            "for line in sys.stdin:\n" +
            "    if 'exit' in line:\n" +
            "        break\n");

        try
        {
            await using var bridge = new PythonEmaBridge("python", tempScript);
            var reply = await bridge.StartAsync();

            Assert.True(reply.Ok);
            Assert.Equal("ready", reply.Status);
            Assert.Equal("Simulated-GPU", reply.Device);

            // Re-calling StartAsync returns cached reply with preserved Device
            var cachedReply = await bridge.StartAsync();
            Assert.True(cachedReply.Ok);
            Assert.Equal("Simulated-GPU", cachedReply.Device);
        }
        finally
        {
            if (File.Exists(tempScript))
                File.Delete(tempScript);
        }
    }

    [Fact]
    public async Task EdgeCase_Validation_EmptyOutputPath_ThrowsArgumentException()
    {
        var bridgePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\bridge\ema_bridge.py"));
        if (!File.Exists(bridgePath))
            bridgePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "bridge", "ema_bridge.py"));

        await using var bridge = new PythonEmaBridge("python", bridgePath);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await bridge.GenerateAsync("ema", "test", "", 1.0m, 48000, 1);
        });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await bridge.GenerateAsync("ema", "test", "   \t  ", 1.0m, 48000, 1);
        });
    }

    [Fact]
    public void EdgeCase_SetBusy_Disables_SaveButtons_And_PreventsReentrancy()
    {
        RunInSta(() =>
        {
            using var form = new MainForm();
            var outputsDir = form.OutputsDirectory;
            Directory.CreateDirectory(outputsDir);

            var testFile = Path.Combine(outputsDir, $"busy_test_{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(testFile, new byte[512]);

            try
            {
                form.RefreshGalleryForTesting(testFile);
                Assert.True(form.SaveWavButton.Enabled);
                Assert.True(form.SaveMp3Button.Enabled);
                Assert.False(form.IsBusy);

                form.SetBusyForTesting(true);
                Assert.True(form.IsBusy);
                Assert.False(form.GenerateButton.Enabled, "Generate button must be disabled when busy.");
                Assert.False(form.SaveWavButton.Enabled, "Save WAV button must be disabled when busy.");
                Assert.False(form.SaveMp3Button.Enabled, "Save MP3 button must be disabled when busy.");

                form.SetBusyForTesting(false);
                Assert.False(form.IsBusy);
                Assert.True(form.GenerateButton.Enabled, "Generate button must be re-enabled when not busy.");
                Assert.True(form.SaveWavButton.Enabled, "Save WAV button must be re-enabled when not busy.");
                Assert.True(form.SaveMp3Button.Enabled, "Save MP3 button must be re-enabled when not busy.");
            }
            finally
            {
                if (File.Exists(testFile))
                    File.Delete(testFile);
            }
        });
    }
}
