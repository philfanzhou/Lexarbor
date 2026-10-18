using System.Diagnostics;
using NLayer;

namespace Lexarbor.Service.Tests;

public sealed class TextbookMp3ValidatorTests
{
    private static byte[] Tone => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static double Validate(byte[] bytes, int max = 7200) => new TextbookMp3Validator().Validate(bytes, max, TimeSpan.FromSeconds(5), Token);

    [Fact]
    public void RealTone_UsesEveryDecodedSample_WithoutGaplessTrimming()
        => Assert.Equal(25344d / 44100, Validate(Tone), 10);

    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(true, false, 3)]
    [InlineData(true, true, 3)]
    [InlineData(true, true, 2)]
    [InlineData(true, true, 0)]
    public void CompleteCbrVbrCrcAndId3Frames_AreDecoded(bool vbr, bool crc, int version)
    {
        var frames = SilentFrames(4, vbr, crc, version);
        var rate = version == 3 ? 44100 : version == 2 ? 22050 : 11025;
        var samples = version == 3 ? 1152 : 576;
        var expected = 4d * samples / rate;
        Assert.Equal(expected, Validate(frames), 10);
        var id3 = new byte[] { 73, 68, 51, 4, 0, 0, 0, 0, 0, 4 }.Concat(new byte[4]).Concat(frames).ToArray();
        Assert.Equal(expected, Validate(id3), 10);
        var footer = new byte[] { 73, 68, 51, 4, 0, 16, 0, 0, 0, 0, 51, 68, 73, 4, 0, 16, 0, 0, 0, 0 }.Concat(frames).ToArray();
        Assert.Equal(expected, Validate(footer), 10);
        Assert.Equal(expected, Validate(frames.Concat("TAG"u8.ToArray()).Concat(new byte[125]).ToArray()), 10);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("header")]
    [InlineData("truncated")]
    [InlineData("middle")]
    [InlineData("crc")]
    [InlineData("rate")]
    [InlineData("channel")]
    [InlineData("version")]
    [InlineData("id3")]
    [InlineData("footer")]
    public void InvalidFrames_AreRejected(string kind)
    {
        var bytes = kind == "crc" ? SilentFrames(3, crc: true) : Tone;
        if (kind == "text") bytes = "audio/mpeg is only a hint"u8.ToArray();
        if (kind == "header") bytes = new byte[] { 255, 251, 144, 192, 255, 251, 144, 192 };
        if (kind == "truncated") bytes = bytes[..^1];
        if (kind is "middle" or "rate" or "channel" or "version")
        {
            var offset = FrameOffsets(bytes)[2];
            if (kind == "middle") Array.Fill(bytes, (byte)255, offset + 4, 17);
            if (kind == "rate") bytes[offset + 2] |= 4;
            if (kind == "channel") bytes[offset + 3] = 0;
            if (kind == "version") bytes[offset + 1] &= 0xf7;
        }
        if (kind == "crc") bytes[4] ^= 1;
        if (kind == "id3") bytes = new byte[] { 73, 68, 51, 4, 0, 0, 127, 127, 127, 127 }.Concat(bytes).ToArray();
        if (kind == "footer") bytes = new byte[] { 73, 68, 51, 4, 0, 16, 0, 0, 0, 0 }.Concat(new byte[10]).Concat(bytes).ToArray();
        Assert.Equal(415, Assert.Throws<TextbookMediaException>(() => Validate(bytes)).StatusCode);
    }

    [Fact]
    public void XingVbriAndLameDeclarations_CannotShortenValidationOrDuration()
    {
        var frames = SilentFrames(40);
        "Xing"u8.CopyTo(frames.AsSpan(21));
        frames[28] = 1; // A false short frame count; it is ancillary data, never trusted.
        "LAME"u8.CopyTo(frames.AsSpan(33)); frames[54] = frames[55] = frames[56] = 255;
        Assert.Equal(40d * 1152 / 44100, Validate(frames), 10);
        Assert.Equal(422, Assert.Throws<TextbookMediaException>(() => Validate(frames, 1)).StatusCode);
        var offsets = FrameOffsets(frames); Array.Fill(frames, (byte)255, offsets[^1] + 4, 17);
        Assert.Equal(415, Assert.Throws<TextbookMediaException>(() => Validate(frames)).StatusCode);
        var vbri = SilentFrames(4); "VBRI"u8.CopyTo(vbri.AsSpan(36));
        Assert.Equal(4d * 1152 / 44100, Validate(vbri), 10);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFinitePcm_IsNeverAccepted(float value)
    {
        var validator = new TextbookMp3Validator((frame, pcm) => { pcm[0] = value; return frame.SampleCount; });
        Assert.Equal(415, Assert.Throws<TextbookMediaException>(() => validator.Validate(Tone, 7200, TimeSpan.FromSeconds(5), Token)).StatusCode);
    }
    [Fact]
    public void ShortDecode_CancellationAndMonotonicDeadline_StopWorkInline()
    {
        var shortDecoder = new TextbookMp3Validator((_, _) => 0);
        Assert.Equal(415, Assert.Throws<TextbookMediaException>(() => shortDecoder.Validate(Tone, 7200, TimeSpan.FromSeconds(5), Token)).StatusCode);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new TextbookMp3Validator().Validate(Tone, 7200, TimeSpan.FromSeconds(5), cancelled.Token));
        long timestamp = 0;
        var calls = 0;
        var deadline = new TextbookMp3Validator((frame, _) =>
        { calls++; frame.Reset(); timestamp = Stopwatch.Frequency * 2; frame.ReadBits(1); return frame.SampleCount; }, () => timestamp);
        Assert.Throws<TimeoutException>(() => deadline.Validate(Tone, 7200, TimeSpan.FromSeconds(1), Token));
        Assert.Equal(1, calls);
        using var during = new CancellationTokenSource();
        var cancellation = new TextbookMp3Validator((frame, _) => { frame.Reset(); during.Cancel(); frame.ReadBits(1); return 0; });
        Assert.Throws<OperationCanceledException>(() => cancellation.Validate(Tone, 7200, TimeSpan.FromSeconds(5), during.Token));
    }

    internal static byte[] SilentFrames(int count, bool vbr = false, bool crc = false, int version = 3)
    {
        using var output = new MemoryStream();
        for (var index = 0; index < count; index++)
        {
            var bitrateIndex = vbr && index % 2 == 1 ? 10 : 9;
            var rate = version == 3 ? 44100 : version == 2 ? 22050 : 11025;
            var bitrate = version == 3 ? bitrateIndex == 9 ? 128000 : 160000 : bitrateIndex == 9 ? 80000 : 96000;
            var frame = new byte[(version == 3 ? 144 : 72) * bitrate / rate];
            frame[0] = 255; frame[1] = (byte)(224 | version << 3 | 2 | (crc ? 0 : 1));
            frame[2] = (byte)(bitrateIndex << 4); frame[3] = 192;
            if (crc)
            {
                ushort value = 0xffff;
                foreach (var b in frame.AsSpan(2, 2).ToArray().Concat(frame.AsSpan(6, version == 3 ? 17 : 9).ToArray()))
                    for (var bit = 7; bit >= 0; bit--)
                    { var xor = ((value >> 15) ^ (b >> bit)) & 1; value = (ushort)(value << 1); if (xor != 0) value ^= 0x8005; }
                frame[4] = (byte)(value >> 8); frame[5] = (byte)value;
            }
            output.Write(frame);
        }
        return output.ToArray();
    }
    internal static List<int> FrameOffsets(byte[] bytes)
    {
        List<int> result = [];
        var offset = bytes.AsSpan(0, 3).SequenceEqual("ID3"u8) ? 10 + (bytes[6] << 21 | bytes[7] << 14 | bytes[8] << 7 | bytes[9]) : 0;
        while (offset < bytes.Length)
        { result.Add(offset); offset += 144000 * new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }[bytes[offset + 2] >> 4] / 44100 + ((bytes[offset + 2] >> 1) & 1); }
        return result;
    }
}
