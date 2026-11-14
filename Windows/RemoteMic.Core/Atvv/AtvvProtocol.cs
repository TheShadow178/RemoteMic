namespace RemoteMic.Core.Atvv;

public enum AtvvVersion
{
    V04,
    V10,
}

public enum AtvvCodec : byte
{
    Adpcm8Khz = 0x01,
    Adpcm16Khz = 0x02,
}

public static class AtvvCodecExtensions
{
    public static int SampleRate(this AtvvCodec codec) =>
        codec == AtvvCodec.Adpcm8Khz ? 8_000 : 16_000;
}

public sealed record AtvvCapabilities(
    AtvvVersion Version,
    byte Codecs,
    byte InteractionModel,
    int FrameSize)
{
    public AtvvCodec? SelectedCodec =>
        (Codecs & (byte)AtvvCodec.Adpcm16Khz) != 0 ? AtvvCodec.Adpcm16Khz :
        (Codecs & (byte)AtvvCodec.Adpcm8Khz) != 0 ? AtvvCodec.Adpcm8Khz : null;
}

public abstract record AtvvControlEvent
{
    public sealed record AudioStop(byte Reason) : AtvvControlEvent;
    public sealed record AudioStart(byte Reason, AtvvCodec Codec, byte StreamId) : AtvvControlEvent;
    public sealed record StartSearch : AtvvControlEvent;
    public sealed record AudioSync(AtvvCodec Codec, ushort Sequence, short Predictor, byte StepIndex) : AtvvControlEvent;
    public sealed record Capabilities(AtvvCapabilities Value) : AtvvControlEvent;
    public sealed record MicrophoneOpenError(ushort Code) : AtvvControlEvent;
    public sealed record Unknown(byte[] Data) : AtvvControlEvent;
}

public readonly record struct AtvvAudioFrame(ushort Sequence, short[] Samples);

/// <summary>
/// Platform-independent Google Android TV Voice (ATVV) protocol state.
/// The GATT transport owns connection/retry behavior and passes packets here.
/// </summary>
public sealed class AtvvProtocol
{
    public static readonly Guid ServiceUuid = Guid.Parse("AB5E0001-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid CommandUuid = Guid.Parse("AB5E0002-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid AudioUuid = Guid.Parse("AB5E0003-5A21-4F05-BC7D-AF01F617B664");
    public static readonly Guid ControlUuid = Guid.Parse("AB5E0004-5A21-4F05-BC7D-AF01F617B664");

    public static ReadOnlyMemory<byte> GetCapabilitiesCommand { get; } =
        new byte[] { 0x0A, 0x01, 0x00, 0x00, 0x03, 0x03 };

    private readonly AdpcmDecoder _decoder = new();
    private ushort _v10Sequence;
    private bool _audioStreamActive;
    private bool _hasPendingSyncBeforeStart;

    public AtvvCapabilities? Capabilities { get; private set; }
    public AtvvCodec? Codec { get; private set; }

    public void AcceptCapabilities(AtvvCapabilities capabilities)
    {
        var selected = capabilities.SelectedCodec ??
            throw new InvalidDataException("Remote does not support ATVV ADPCM at 8 or 16 kHz.");
        Capabilities = capabilities;
        Codec = selected;
        _decoder.Reset();
        _v10Sequence = 0;
        _audioStreamActive = false;
        _hasPendingSyncBeforeStart = false;
    }

    public byte[] CreateMicrophoneOpenCommand()
    {
        var capabilities = RequireCapabilities();
        var codec = Codec ?? throw new InvalidOperationException("ATVV codec is not negotiated.");
        return capabilities.Version == AtvvVersion.V04
            ? [0x0C, 0x00, (byte)codec]
            : [0x0C, 0x00];
    }

    public byte[] CreateMicrophoneCloseCommand(byte streamId)
    {
        var capabilities = RequireCapabilities();
        return capabilities.Version == AtvvVersion.V04 ? [0x0D] : [0x0D, streamId];
    }

    public byte[] CreateKeepAliveCommand(byte streamId)
    {
        var capabilities = RequireCapabilities();
        return capabilities.Version == AtvvVersion.V04
            ? CreateMicrophoneOpenCommand()
            : [0x0E, streamId];
    }

    public AtvvControlEvent ParseControl(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return new AtvvControlEvent.Unknown([]);
        switch (data[0])
        {
            case 0x00:
                return new AtvvControlEvent.AudioStop(data.Length > 1 ? data[1] : (byte)0);
            case 0x04:
                if (Capabilities?.Version == AtvvVersion.V10 && data.Length >= 4 &&
                    Enum.IsDefined(typeof(AtvvCodec), data[2]))
                {
                    return new AtvvControlEvent.AudioStart(data[1], (AtvvCodec)data[2], data[3]);
                }

                return new AtvvControlEvent.AudioStart(0, Codec ?? AtvvCodec.Adpcm8Khz, 0);
            case 0x08:
                return new AtvvControlEvent.StartSearch();
            case 0x0A:
                if (Capabilities?.Version == AtvvVersion.V10 && data.Length >= 7 &&
                    Enum.IsDefined(typeof(AtvvCodec), data[1]))
                {
                    var sequence = ReadBigEndianUInt16(data[2], data[3]);
                    var predictor = unchecked((short)ReadBigEndianUInt16(data[4], data[5]));
                    return new AtvvControlEvent.AudioSync((AtvvCodec)data[1], sequence, predictor, data[6]);
                }

                return new AtvvControlEvent.Unknown(data.ToArray());
            case 0x0B:
                return TryParseCapabilities(data, out var capabilities)
                    ? new AtvvControlEvent.Capabilities(capabilities!)
                    : new AtvvControlEvent.Unknown(data.ToArray());
            case 0x0C:
                return new AtvvControlEvent.MicrophoneOpenError(
                    data.Length >= 3 ? ReadBigEndianUInt16(data[1], data[2]) : ushort.MaxValue);
            default:
                return new AtvvControlEvent.Unknown(data.ToArray());
        }
    }

    public static bool TryParseCapabilities(ReadOnlySpan<byte> data, out AtvvCapabilities? capabilities)
    {
        capabilities = null;
        if (data.Length < 3 || data[0] != 0x0B) return false;
        var version = ReadBigEndianUInt16(data[1], data[2]);
        switch (version)
        {
            case 0x0004 when data.Length >= 9:
                capabilities = new AtvvCapabilities(
                    AtvvVersion.V04, data[4], 0, ReadBigEndianUInt16(data[5], data[6]));
                return true;
            case 0x0100 when data.Length >= 7:
                capabilities = new AtvvCapabilities(
                    AtvvVersion.V10, data[3], data[4], ReadBigEndianUInt16(data[5], data[6]));
                return true;
            default:
                return false;
        }
    }

    public void PrepareForAudioStream()
    {
        _v10Sequence = 0;
        _decoder.Reset();
        _audioStreamActive = false;
        _hasPendingSyncBeforeStart = false;
    }

    public void ApplyAudioSync(AtvvCodec codec, ushort sequence, short predictor, byte stepIndex)
    {
        Codec = codec;
        _v10Sequence = sequence;
        _decoder.Reset(predictor, stepIndex);
        _hasPendingSyncBeforeStart = !_audioStreamActive;
    }

    public void BeginAudioStream(AtvvCodec codec)
    {
        Codec = codec;
        if (!_hasPendingSyncBeforeStart)
        {
            _v10Sequence = 0;
            _decoder.Reset();
        }

        _audioStreamActive = true;
        _hasPendingSyncBeforeStart = false;
    }

    public void EndAudioStream()
    {
        _audioStreamActive = false;
        _hasPendingSyncBeforeStart = false;
    }

    public AtvvAudioFrame? DecodeAudio(ReadOnlySpan<byte> data)
    {
        if (Capabilities is null || Codec is null) return null;
        switch (Capabilities.Version)
        {
            case AtvvVersion.V04:
                if (data.Length != Capabilities.FrameSize || data.Length < 6) return null;
                var sequence = ReadBigEndianUInt16(data[0], data[1]);
                var predictor = unchecked((short)ReadBigEndianUInt16(data[3], data[4]));
                _decoder.Reset(predictor, data[5]);
                var decoded = _decoder.Decode(data[6..]);
                var samples = new short[decoded.Length + 1];
                samples[0] = predictor;
                decoded.CopyTo(samples, 1);
                return new AtvvAudioFrame(sequence, samples);
            case AtvvVersion.V10:
                if (data.IsEmpty) return null;
                return new AtvvAudioFrame(_v10Sequence++, _decoder.Decode(data));
            default:
                throw new InvalidOperationException("Unsupported ATVV version.");
        }
    }

    private AtvvCapabilities RequireCapabilities() => Capabilities ??
        throw new InvalidOperationException("ATVV capabilities have not been negotiated.");

    private static ushort ReadBigEndianUInt16(byte high, byte low) =>
        (ushort)((high << 8) | low);
}
