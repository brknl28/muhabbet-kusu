using NAudio.Wave;

namespace MuhabbetKusu;

internal sealed class MicrophoneRecorder : IDisposable
{
    private readonly WaveInEvent _input = new() { DeviceNumber = -1, WaveFormat = new WaveFormat(16000, 16, 1) };
    private readonly object _gate = new();
    private WaveFileWriter? _writer;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Start(string path)
    {
        if (WaveIn.DeviceCount == 0) throw new InvalidOperationException("Mikrofon bulunamadı. Bir mikrofon bağlayın.");
        _writer = new WaveFileWriter(path, _input.WaveFormat);
        _input.DataAvailable += (_, args) => { lock (_gate) _writer?.Write(args.Buffer, 0, args.BytesRecorded); };
        _input.RecordingStopped += (_, args) =>
        {
            lock (_gate) { _writer?.Dispose(); _writer = null; }
            if (args.Exception != null) _stopped.TrySetException(args.Exception);
            else _stopped.TrySetResult();
        };
        _input.StartRecording();
    }

    public async Task StopAsync()
    {
        _input.StopRecording();
        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        _input.Dispose();
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }
}
