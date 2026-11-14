using System.Runtime.InteropServices;

namespace RemoteMic.Core.Audio;

public enum AudioRouteSource
{
    Silence,
    LocalMicrophone,
    RemoteMicrophone,
}

public sealed record AudioRenderResult(float[] MonoSamples, AudioRouteSource Source);

/// <summary>
/// Pure audio routing policy shared by any WASAPI/virtual-cable implementation.
/// It outputs 48 kHz mono samples for duplication into the two virtual channels.
/// Remote audio has priority; local audio is retained as a near-real-time fallback.
/// </summary>
public sealed class AudioRoutePolicy
{
    public const int OutputSampleRate = 48_000;
    public const float RemoteGain = 10.0f; // +20 dB, matching the macOS build.
    public const int LocalAlignmentFrames = 5_760; // 120 ms at 48 kHz.
    public const int FallbackBacklogFrames = 12_000; // 250 ms at 48 kHz.

    private readonly object _gate = new();
    private readonly SampleQueue _local = new();
    private readonly SampleQueue _remote = new();
    private bool _active;
    private bool _localEnabled = true;
    private bool _remoteEnabled = true;

    public bool Active
    {
        get { lock (_gate) return _active; }
    }

    public int PendingLocalFrames
    {
        get { lock (_gate) return _local.Count; }
    }

    public int PendingRemoteFrames
    {
        get { lock (_gate) return _remote.Count; }
    }

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (_active == active) return;
            _active = active;
            _local.Clear();
            _remote.Clear();
        }
    }

    public void SetInputsEnabled(bool local, bool remote)
    {
        lock (_gate)
        {
            _localEnabled = local;
            _remoteEnabled = remote;
            if (!local) _local.Clear();
            if (!remote) _remote.Clear();
        }
    }

    public void FeedLocal(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (samples.IsEmpty || sampleRate <= 0) return;
        var resampled = Resample(samples, sampleRate, OutputSampleRate);
        lock (_gate)
        {
            if (!_active || !_localEnabled) return;
            _local.Enqueue(resampled);
        }
    }

    public void FeedRemote(ReadOnlySpan<short> samples, int sampleRate)
    {
        if (samples.IsEmpty || sampleRate <= 0) return;
        var normalized = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            normalized[i] = Math.Clamp(samples[i] / 32768f * RemoteGain, -1f, 1f);
        var resampled = Resample(normalized, sampleRate, OutputSampleRate);
        lock (_gate)
        {
            if (!_active || !_remoteEnabled) return;
            _remote.Enqueue(resampled);
        }
    }

    public AudioRenderResult Render(int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        var local = new float[frameCount];
        var remote = new float[frameCount];
        var localTaken = 0;
        var remoteTaken = 0;

        lock (_gate)
        {
            if (_active)
            {
                remoteTaken = _remote.Dequeue(remote);

                var alignment = remoteTaken > 0 ? LocalAlignmentFrames : 0;
                var localReady = Math.Max(0, _local.Count - alignment);
                localTaken = _local.Dequeue(local.AsSpan(0, Math.Min(frameCount, localReady)));

                if (remoteTaken > 0 && _local.Count > FallbackBacklogFrames)
                    _local.Discard(_local.Count - FallbackBacklogFrames);
            }
        }

        var mixed = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var value = frame < remoteTaken ? remote[frame] :
                frame < localTaken ? local[frame] : 0f;
            mixed[frame] = Math.Clamp(value, -1f, 1f);
        }

        var source = remoteTaken > 0 ? AudioRouteSource.RemoteMicrophone :
            localTaken > 0 ? AudioRouteSource.LocalMicrophone : AudioRouteSource.Silence;
        return new AudioRenderResult(mixed, source);
    }

    public static float[] Resample(ReadOnlySpan<float> input, int inputRate, int outputRate)
    {
        if (input.IsEmpty || inputRate <= 0 || outputRate <= 0) return [];
        if (inputRate == outputRate) return input.ToArray();

        var ratio = (double)outputRate / inputRate;
        var outputCount = Math.Max(1, (int)Math.Floor(input.Length * ratio));
        var output = new float[outputCount];
        for (var index = 0; index < outputCount; index++)
        {
            var position = index / ratio;
            var lower = Math.Min((int)position, input.Length - 1);
            var upper = Math.Min(lower + 1, input.Length - 1);
            var fraction = (float)(position - lower);
            output[index] = input[lower] + ((input[upper] - input[lower]) * fraction);
        }

        return output;
    }

    private sealed class SampleQueue
    {
        private readonly List<float> _samples = [];
        private int _offset;
        public int Count => _samples.Count - _offset;

        public void Enqueue(ReadOnlySpan<float> values)
        {
            foreach (var value in values) _samples.Add(value);
        }

        public int Dequeue(Span<float> destination)
        {
            var count = Math.Min(destination.Length, Count);
            if (count == 0) return 0;
            CollectionsMarshal.AsSpan(_samples).Slice(_offset, count).CopyTo(destination);
            _offset += count;
            Compact();
            return count;
        }

        public void Discard(int count)
        {
            _offset += Math.Min(Math.Max(0, count), Count);
            Compact();
        }

        public void Clear()
        {
            _samples.Clear();
            _offset = 0;
        }

        private void Compact()
        {
            if (_offset <= 4_096) return;
            _samples.RemoveRange(0, _offset);
            _offset = 0;
        }
    }
}
