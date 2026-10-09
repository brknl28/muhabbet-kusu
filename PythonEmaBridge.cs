using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MuhabbetKusu;

internal sealed class PythonEmaBridge : IDisposable, IAsyncDisposable
{
    private Process? _process;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly string _pythonExe;
    private readonly string _bridgePath;
    private readonly StringBuilder _stderrBuffer = new();

    private BridgeReply? _readyReply;

    public bool IsRunning => _process is { HasExited: false };

    public PythonEmaBridge(string pythonExe, string bridgePath)
    {
        _pythonExe = pythonExe;
        _bridgePath = bridgePath;
    }

    public async Task<BridgeReply> StartAsync(CancellationToken cancellationToken = default)
    {
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning && _readyReply != null)
                return _readyReply;

            if (_process != null)
            {
                try { _process.Dispose(); } catch { }
                _process = null;
            }

            lock (_stderrBuffer)
                _stderrBuffer.Clear();

            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var psi = new ProcessStartInfo
            {
                FileName = _pythonExe,
                Arguments = $"-u \"{_bridgePath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = utf8NoBom,
                StandardOutputEncoding = utf8NoBom,
                StandardErrorEncoding = utf8NoBom,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_bridgePath) ?? AppContext.BaseDirectory
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    lock (_stderrBuffer)
                    {
                        _stderrBuffer.AppendLine(e.Data);
                        if (_stderrBuffer.Length > 8192)
                            _stderrBuffer.Remove(0, _stderrBuffer.Length - 8192);
                    }
                }
            };

            _process.Start();
            _process.BeginErrorReadLine();

            // The bridge prints exactly one JSON line when the models are ready or failed.
            string? line = null;
            BridgeReply? reply = null;
            while (true)
            {
                var readTask = _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
                var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromMinutes(2), cancellationToken));
                if (completed != readTask)
                    throw new TimeoutException("Muhabbet Kuşu modelleri başlatılırken zaman aşımı oluştu.");

                line = await readTask;
                if (line == null)
                {
                    string stderr;
                    lock (_stderrBuffer)
                        stderr = _stderrBuffer.ToString();
                    throw new InvalidOperationException("Python köprüsü başlatılamadı. " + stderr);
                }
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    reply = JsonSerializer.Deserialize<BridgeReply>(line, JsonOptions());
                    if (reply != null && reply.Status != null)
                        break;
                }
                catch (JsonException)
                {
                    // Non-JSON logging output from standard libraries; skip to next line
                    continue;
                }
            }

            if (reply == null)
                throw new InvalidOperationException("Python köprüsünden geçersiz yanıt alındı.");

            if (!reply.Ok)
                throw new InvalidOperationException(reply.Error ?? "Modeller başlatılamadı.");

            _readyReply = reply;
            return reply;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<BridgeReply> GenerateAsync(
        string model,
        string text,
        string outputPath,
        decimal speed,
        int sampleRate,
        long? seed,
        int? steps = null,
        double? cfg = null,
        bool? eq = null,
        Action<int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Seslendirilecek metin boş olamaz.", nameof(text));

        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Çıktı ses dosyası yolu belirtilmelidir.", nameof(outputPath));

        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsRunning)
                await StartAsync(cancellationToken);

            var request = new
            {
                command = "generate",
                model,
                text,
                path = outputPath,
                speed = (double)speed,
                sample_rate = sampleRate,
                seed,
                steps,
                cfg,
                eq
            };

            var json = JsonSerializer.Serialize(request);
            await _process!.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync();

            while (true)
            {
                var line = await _process!.StandardOutput.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    string stderr;
                    lock (_stderrBuffer)
                        stderr = _stderrBuffer.ToString();
                    throw new InvalidOperationException("Ses üretim yanıtı alınamadı (işlem sonlandı). " + stderr);
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                BridgeReply? reply;
                try
                {
                    reply = JsonSerializer.Deserialize<BridgeReply>(line, JsonOptions());
                }
                catch (JsonException)
                {
                    // Non-JSON logging output from standard libraries; skip to next line
                    continue;
                }

                if (reply == null)
                    continue;

                if (string.Equals(reply.Status, "progress", StringComparison.OrdinalIgnoreCase))
                {
                    if (reply.Progress.HasValue)
                    {
                        onProgress?.Invoke(reply.Progress.Value);
                    }
                    continue;
                }

                return reply;
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public Task<BridgeReply> GenerateAsync(
        string text,
        string outputPath,
        decimal speed,
        int sampleRate,
        long? seed,
        CancellationToken cancellationToken = default)
        => GenerateAsync("ema", text, outputPath, speed, sampleRate, seed, null, null, null, null, cancellationToken);

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public void Dispose()
    {
        try
        {
            if (IsRunning)
            {
                try
                {
                    _process!.StandardInput.WriteLine("{\"command\":\"exit\"}");
                    _process.StandardInput.Flush();
                    if (!_process.WaitForExit(800))
                        _process.Kill(entireProcessTree: true);
                }
                catch
                {
                    try { _process?.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _readyReply = null;
            _requestLock.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (IsRunning)
            {
                await _process!.StandardInput.WriteLineAsync("{\"command\":\"exit\"}");
                await _process.StandardInput.FlushAsync();
                if (!await Task.Run(() => _process.WaitForExit(1000)))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { _process?.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _readyReply = null;
            _requestLock.Dispose();
        }
    }
}

internal sealed record BridgeReply(
    [property: System.Text.Json.Serialization.JsonPropertyName("ok")] bool Ok,
    [property: System.Text.Json.Serialization.JsonPropertyName("status")] string? Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error,
    [property: System.Text.Json.Serialization.JsonPropertyName("duration")] double? Duration,
    [property: System.Text.Json.Serialization.JsonPropertyName("sample_rate")] int? SampleRate,
    [property: System.Text.Json.Serialization.JsonPropertyName("seed")] long? Seed,
    [property: System.Text.Json.Serialization.JsonPropertyName("device")] string? Device,
    [property: System.Text.Json.Serialization.JsonPropertyName("model")] string? Model,
    [property: System.Text.Json.Serialization.JsonPropertyName("progress")] int? Progress);
