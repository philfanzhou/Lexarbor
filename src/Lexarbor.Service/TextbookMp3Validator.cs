using System.Diagnostics;
using NLayer;

namespace Lexarbor.Service;

public interface ITextbookMp3Validator
{
    double Validate(byte[] bytes, int maxDurationSeconds, TimeSpan timeout, CancellationToken token);
}
public sealed class TextbookMediaException(int statusCode) : Exception
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Strict sequential Layer III decoding. Never use VBR counts or gapless trimming as validation.</summary>
public sealed class TextbookMp3Validator : ITextbookMp3Validator
{
    private readonly Func<IMpegFrame, float[], int>? _decode;
    private readonly Func<long> _timestamp;
    public TextbookMp3Validator() : this(null, Stopwatch.GetTimestamp) { }
    internal TextbookMp3Validator(Func<IMpegFrame, float[], int>? decode, Func<long>? timestamp = null)
    { _decode = decode; _timestamp = timestamp ?? Stopwatch.GetTimestamp; }

    public double Validate(byte[] bytes, int maxDurationSeconds, TimeSpan timeout, CancellationToken token)
    {
        var budget = new Budget(token, _timestamp, timeout, bytes.LongLength * 8);
        budget.Check();
        try
        {
            var offset = TagOffset(bytes);
            var decoder = new MpegFrameDecoder();
            var pcm = new float[2304];
            Frame? first = null;
            long samples = 0;
            var frames = 0;
            while (offset < bytes.Length)
            {
                budget.Check();
                if (bytes.Length - offset == 128 && bytes.AsSpan(offset, 3).SequenceEqual("TAG"u8)) { offset += 128; break; }
                var frame = new Frame(bytes, offset, budget);
                first ??= frame;
                if (frame.Version != first.Version || frame.SampleRate != first.SampleRate || frame.ChannelMode != first.ChannelMode)
                    throw new InvalidDataException();
                // Both source length and the configured PCM duration bound frame count and work.
                if (++frames > bytes.Length / 24 || frames > (long)maxDurationSeconds * frame.SampleRate / frame.SampleCount + 2)
                    throw new TextbookMediaException(422);
                var decoded = _decode is null ? decoder.DecodeFrame(frame, pcm, 0) : _decode(frame, pcm);
                budget.Check();
                var channels = frame.ChannelMode == MpegChannelMode.Mono ? 1 : 2;
                if (decoded != frame.SampleCount * channels) throw new InvalidDataException();
                for (var index = 0; index < decoded; index++)
                    if (!float.IsFinite(pcm[index])) throw new InvalidDataException();
                samples = checked(samples + decoded);
                if (samples > (long)maxDurationSeconds * frame.SampleRate * channels) throw new TextbookMediaException(422);
                offset += frame.FrameLength;
            }
            if (first is null || frames < 2 || offset != bytes.Length) throw new InvalidDataException();
            budget.Check();
            return samples / (double)(first.SampleRate * (first.ChannelMode == MpegChannelMode.Mono ? 1 : 2));
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or TimeoutException or TextbookMediaException))
        { throw new TextbookMediaException(415); }
    }

    private static int TagOffset(byte[] bytes)
    {
        if (bytes.Length < 3 || !bytes.AsSpan(0, 3).SequenceEqual("ID3"u8)) return 0;
        if (bytes.Length < 10 || bytes[3] is < 2 or > 4 || bytes[4] == 255 || (bytes[6] | bytes[7] | bytes[8] | bytes[9]) >= 128)
            throw new InvalidDataException();
        var allowedFlags = bytes[3] switch { 2 => 0xc0, 3 => 0xe0, _ => 0xf0 };
        if ((bytes[5] & ~allowedFlags) != 0) throw new InvalidDataException();
        var size = 10 + (bytes[6] << 21 | bytes[7] << 14 | bytes[8] << 7 | bytes[9]);
        // Tags carry no audio and need no allocation or interpretation.
        if (size > 1024 * 1024 || size > bytes.Length) throw new InvalidDataException();
        if (bytes[3] == 4 && (bytes[5] & 16) != 0)
        {
            if (size + 10 > bytes.Length || !bytes.AsSpan(size, 3).SequenceEqual("3DI"u8)
                || !bytes.AsSpan(size + 3, 7).SequenceEqual(bytes.AsSpan(3, 7))) throw new InvalidDataException();
            size += 10;
        }
        return size;
    }

    private sealed class Budget(CancellationToken token, Func<long> timestamp, TimeSpan timeout, long bitLimit)
    {
        private readonly long _deadline = timestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        private long _bits;
        public void Check()
        {
            token.ThrowIfCancellationRequested();
            if (timestamp() >= _deadline) throw new TimeoutException();
        }
        public void Read(int bits)
        {
            Check();
            if ((_bits += bits) > bitLimit) throw new InvalidDataException();
        }
    }

    private sealed class Frame : IMpegFrame
    {
        private static readonly int[] Rates = [44100, 48000, 32000];
        private static readonly int[] Mpeg1Rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        private static readonly int[] Mpeg2Rates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        private readonly byte[] _bytes;
        private readonly int _offset;
        private readonly Budget _budget;
        private int _bit;
        public Frame(byte[] bytes, int offset, Budget budget)
        {
            budget.Check(); _bytes = bytes; _offset = offset; _budget = budget;
            if (bytes.Length - offset < 4 || bytes[offset] != 255 || (bytes[offset + 1] & 224) != 224
                || ((bytes[offset + 1] >> 3) & 3) == 1 || ((bytes[offset + 1] >> 1) & 3) != 1
                || BitRateIndex is 0 or 15 || SampleRateIndex == 3 || (bytes[offset + 3] & 3) == 2)
                throw new InvalidDataException();
            var sideLength = Version == MpegVersion.Version1 ? ChannelMode == MpegChannelMode.Mono ? 17 : 32
                : ChannelMode == MpegChannelMode.Mono ? 9 : 17;
            if (FrameLength < 4 + (HasCrc ? 2 : 0) + sideLength || FrameLength > bytes.Length - offset)
                throw new InvalidDataException();
            if (HasCrc)
            {
                // MPEG audio CRC-16, polynomial x^16+x^15+x^2+1; low header word plus Layer III side information.
                ushort crc = 0xffff;
                AddCrc(bytes[offset + 2], ref crc); AddCrc(bytes[offset + 3], ref crc);
                for (var i = 0; i < sideLength; i++) { budget.Check(); AddCrc(bytes[offset + 6 + i], ref crc); }
                if (crc != (bytes[offset + 4] << 8 | bytes[offset + 5])) throw new InvalidDataException();
            }
        }
        public int SampleRateIndex => (_bytes[_offset + 2] >> 2) & 3;
        public MpegVersion Version => ((_bytes[_offset + 1] >> 3) & 3) switch
        { 3 => MpegVersion.Version1, 2 => MpegVersion.Version2, _ => MpegVersion.Version25 };
        public int SampleRate => Rates[SampleRateIndex] / (Version == MpegVersion.Version1 ? 1 : Version == MpegVersion.Version2 ? 2 : 4);
        public MpegLayer Layer => MpegLayer.LayerIII;
        public int BitRateIndex => _bytes[_offset + 2] >> 4;
        public int BitRate => (Version == MpegVersion.Version1 ? Mpeg1Rates : Mpeg2Rates)[BitRateIndex] * 1000;
        public int FrameLength => (Version == MpegVersion.Version1 ? 144 : 72) * BitRate / SampleRate + ((_bytes[_offset + 2] >> 1) & 1);
        public MpegChannelMode ChannelMode => (MpegChannelMode)(_bytes[_offset + 3] >> 6);
        public int ChannelModeExtension => (_bytes[_offset + 3] >> 4) & 3;
        public int SampleCount => Version == MpegVersion.Version1 ? 1152 : 576;
        public bool IsCopyrighted => (_bytes[_offset + 3] & 8) != 0;
        public bool HasCrc => (_bytes[_offset + 1] & 1) == 0;
        public bool IsCorrupted => false; // Bad CRC is rejected before reaching the decoder.
        public void Reset() { _budget.Check(); _bit = HasCrc ? 48 : 32; }
        public int ReadBits(int count)
        {
            _budget.Read(count);
            if (count is < 1 or > 32 || _bit + count > FrameLength * 8) throw new InvalidDataException();
            uint value = 0;
            for (var i = 0; i < count; i++, _bit++)
                value = (value << 1) | (uint)((_bytes[_offset + _bit / 8] >> (7 - _bit % 8)) & 1);
            return (int)value;
        }
        private static void AddCrc(byte value, ref ushort crc)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var xor = ((crc >> 15) ^ (value >> bit)) & 1;
                crc = (ushort)(crc << 1);
                if (xor != 0) crc ^= 0x8005;
            }
        }
    }
}
