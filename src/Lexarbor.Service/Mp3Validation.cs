namespace Lexarbor.Service;

public static class Mp3Validation
{
    private static readonly int[] Rates = [44100, 48000, 32000];
    private static readonly int[] Mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    /// <summary>Require a bounded ID3 header followed by complete MPEG Layer III frames; filenames and MIME do not prove audio.</summary>
    public static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual("ID3"u8))
        {
            if (bytes.Length < 10 || bytes[3] is < 2 or > 4 || bytes[4] == 255 || (bytes[6] | bytes[7] | bytes[8] | bytes[9]) >= 128) return false;
            offset = 10 + (bytes[6] << 21 | bytes[7] << 14 | bytes[8] << 7 | bytes[9]);
            if (bytes[3] == 4 && (bytes[5] & 16) != 0) offset += 10;
            if (offset > bytes.Length) return false;
        }
        var frames = 0;
        int? firstVersion = null, firstRate = null;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset == 128 && bytes.Slice(offset, 3).SequenceEqual("TAG"u8)) break;
            if (bytes.Length - offset < 4 || bytes[offset] != 255 || (bytes[offset + 1] & 224) != 224) return false;
            var version = (bytes[offset + 1] >> 3) & 3;
            var layer = (bytes[offset + 1] >> 1) & 3;
            var bitrateIndex = bytes[offset + 2] >> 4;
            var rateIndex = (bytes[offset + 2] >> 2) & 3;
            if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3 || (bytes[offset + 3] & 3) == 2) return false;
            var bitrates = version == 3 ? Mpeg1Bitrates : Mpeg2Bitrates;
            var rate = Rates[rateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
            var length = (version == 3 ? 144000 : 72000) * bitrates[bitrateIndex] / rate + ((bytes[offset + 2] >> 1) & 1);
            if (length < 24 || length > bytes.Length - offset || (firstVersion != null && (firstVersion != version || firstRate != rate))) return false;
            firstVersion = version; firstRate = rate;
            offset += length; frames++;
        }
        return frames >= 2;
    }
}
