using System;
using System.Buffers.Binary;
using System.IO;

namespace Jellyfin.Plugin.SubtitleSync.SignalCache;

/// <summary>
/// The wire and storage format of a 100 Hz speech signal: one bit per sample,
/// wrapped in a small self-describing envelope.
/// </summary>
/// <remarks>
/// <para>
/// The browser produces a <c>SpeechSignal</c> (<c>lib/types.ts</c>): a
/// <c>Float32Array</c> of 0 and 1 sampled at <c>SIGNAL_HZ</c> = 100. That is one
/// value per 10 ms, so an hour of runtime is 360,000 samples. As bits it is
/// 45,000 bytes; as the raw PCM it was derived from it would have been about
/// 115 MB. The whole point of the cache is the difference between those two
/// numbers, so the format is a bit per sample and nothing else.
/// </para>
/// <para>
/// The envelope is twelve bytes, little-endian throughout:
/// </para>
/// <code>
/// 0..3    magic, ASCII "SSC1"
/// 4..7    uint32 sample count
/// 8..11   uint32 CRC-32 of the packed body
/// 12..    packed body, ceil(sampleCount / 8) bytes, LSB first
/// </code>
/// <para>
/// Every field earns its place at a trust boundary. The magic rejects payloads
/// from some other producer, including the HTML error page a misconfigured
/// proxy will happily hand back. The sample count is checked against a cap
/// before a buffer is sized from it, so twelve bytes of attacker input cannot
/// ask for a four gigabyte allocation. The implied length catches truncation,
/// which is what a dropped upload and a crash mid-write both look like. The
/// checksum catches the rest. A signal that has silently lost its tail or had a
/// bit flipped does not look wrong: it produces a confidently incorrect offset,
/// which is a far worse failure than an error.
/// </para>
/// <para>
/// The version lives in the magic rather than a separate field. There is no
/// migration story for a cache: if the format ever changes, <c>SSC2</c> entries
/// simply miss against <c>SSC1</c> files and the old ones age out.
/// </para>
/// </remarks>
public static class SpeechSignalCodec
{
    /// <summary>
    /// The fixed header length in bytes: magic, sample count, checksum.
    /// </summary>
    public const int HeaderLength = 12;

    /// <summary>
    /// The largest signal that will be encoded or accepted, in samples.
    /// </summary>
    /// <remarks>
    /// Twenty-four hours at 100 Hz. Comfortably beyond any single media file,
    /// and it bounds the packed body at 1,080,000 bytes, so the worst case a
    /// request can force the server to hold is about a megabyte.
    /// </remarks>
    public const int MaxSampleCount = 8_640_000;

    private const uint Crc32Polynomial = 0xEDB88320u;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>
    /// Gets the largest envelope, in bytes, that <see cref="Validate"/> can
    /// return <see cref="SignalPayloadError.None"/> for.
    /// </summary>
    /// <remarks>
    /// The request-body cap on the POST endpoint. A body longer than this is
    /// refused without being read into memory at all.
    /// </remarks>
    public static int MaxEnvelopeLength => HeaderLength + PackedLength(MaxSampleCount);

    /// <summary>
    /// The number of bytes needed to hold a given number of one-bit samples.
    /// </summary>
    /// <param name="sampleCount">The number of samples.</param>
    /// <returns>The packed length in bytes.</returns>
    public static int PackedLength(int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);

        return (sampleCount + 7) / 8;
    }

    /// <summary>
    /// Packs one sample per bit, least significant bit first.
    /// </summary>
    /// <param name="samples">
    /// The signal. Any non-zero value is speech; the browser sends exactly 0 and
    /// 1 but nothing about the transport enforces that, and mapping an
    /// unexpected 2 to silence would be a silent data change.
    /// </param>
    /// <returns>The packed bytes, with the final byte's unused bits zeroed.</returns>
    public static byte[] Pack(ReadOnlySpan<byte> samples)
    {
        var packed = new byte[PackedLength(samples.Length)];

        for (var i = 0; i < samples.Length; i++)
        {
            if (samples[i] != 0)
            {
                packed[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return packed;
    }

    /// <summary>
    /// The inverse of <see cref="Pack"/>.
    /// </summary>
    /// <param name="packed">The packed bytes.</param>
    /// <param name="sampleCount">
    /// How many samples they represent. Bit-packing is not self-delimiting, so
    /// this cannot be inferred: the envelope carries it.
    /// </param>
    /// <returns>One byte per sample, each 0 or 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleCount"/> is negative.</exception>
    /// <exception cref="ArgumentException">
    /// The packed bytes are not exactly the length <paramref name="sampleCount"/> implies.
    /// </exception>
    public static byte[] Unpack(ReadOnlySpan<byte> packed, int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);

        if (packed.Length != PackedLength(sampleCount))
        {
            throw new ArgumentException(
                "Packed length " + packed.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " does not hold exactly " + sampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " samples.",
                nameof(packed));
        }

        var samples = new byte[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = (byte)((packed[i >> 3] >> (i & 7)) & 1);
        }

        return samples;
    }

    /// <summary>
    /// Wraps a signal in the envelope described on this class.
    /// </summary>
    /// <param name="samples">The signal, one byte per 10 ms sample.</param>
    /// <returns>The envelope, ready to be sent or stored.</returns>
    /// <exception cref="ArgumentException">
    /// The signal is longer than <see cref="MaxSampleCount"/>.
    /// </exception>
    public static byte[] Encode(ReadOnlySpan<byte> samples)
    {
        if (samples.Length > MaxSampleCount)
        {
            throw new ArgumentException(
                "A signal of " + samples.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " samples exceeds the cap of "
                + MaxSampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
                nameof(samples));
        }

        var packed = Pack(samples);
        var envelope = new byte[HeaderLength + packed.Length];

        "SSC1"u8.CopyTo(envelope);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(4), (uint)samples.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(8), Crc32(packed));
        packed.CopyTo(envelope.AsSpan(HeaderLength));

        return envelope;
    }

    /// <summary>
    /// Checks an envelope from end to end without allocating anything sized by
    /// its contents.
    /// </summary>
    /// <param name="envelope">The candidate bytes.</param>
    /// <returns>
    /// <see cref="SignalPayloadError.None"/> if the envelope is well formed, or
    /// the first thing found wrong with it.
    /// </returns>
    public static SignalPayloadError Validate(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < HeaderLength)
        {
            return SignalPayloadError.TooShort;
        }

        if (!envelope[..4].SequenceEqual("SSC1"u8))
        {
            return SignalPayloadError.BadMagic;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(envelope[4..]);
        if (declared > MaxSampleCount)
        {
            return SignalPayloadError.SampleCountOutOfRange;
        }

        var sampleCount = (int)declared;
        var body = envelope[HeaderLength..];
        if (body.Length != PackedLength(sampleCount))
        {
            return SignalPayloadError.LengthMismatch;
        }

        var usedBitsInLastByte = sampleCount & 7;
        if (usedBitsInLastByte != 0 && (body[^1] >> usedBitsInLastByte) != 0)
        {
            return SignalPayloadError.PaddingNotZero;
        }

        if (Crc32(body) != BinaryPrimitives.ReadUInt32LittleEndian(envelope[8..]))
        {
            return SignalPayloadError.ChecksumMismatch;
        }

        return SignalPayloadError.None;
    }

    /// <summary>
    /// Reads the declared sample count out of a header.
    /// </summary>
    /// <param name="envelope">An envelope that <see cref="Validate"/> has accepted.</param>
    /// <returns>The number of 10 ms samples the envelope carries.</returns>
    /// <exception cref="InvalidDataException">The envelope is not valid.</exception>
    public static int ReadSampleCount(ReadOnlySpan<byte> envelope)
    {
        ThrowIfInvalid(envelope);

        return (int)BinaryPrimitives.ReadUInt32LittleEndian(envelope[4..]);
    }

    /// <summary>
    /// Unwraps an envelope back into a signal.
    /// </summary>
    /// <param name="envelope">The envelope.</param>
    /// <returns>The signal, one byte per sample, each 0 or 1.</returns>
    /// <exception cref="InvalidDataException">
    /// The envelope is not valid. It throws rather than returning what it can
    /// salvage, because a signal missing its tail is indistinguishable from a
    /// good one until it produces a wrong answer.
    /// </exception>
    public static byte[] Decode(ReadOnlySpan<byte> envelope)
    {
        ThrowIfInvalid(envelope);

        var sampleCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(envelope[4..]);
        return Unpack(envelope[HeaderLength..], sampleCount);
    }

    /// <summary>
    /// CRC-32/ISO-HDLC, the same variant gzip and zip use.
    /// </summary>
    /// <param name="data">The bytes to checksum.</param>
    /// <returns>The checksum.</returns>
    /// <remarks>
    /// Hand-rolled because <c>System.IO.Hashing</c> is a separate package and
    /// the plugin's dependency set is deliberately tiny. The table is generated
    /// from the polynomial rather than pasted, and
    /// <c>SpeechSignalCodecTests.Crc32MatchesTheStandardCheckValue</c> pins it
    /// to the published check value for <c>"123456789"</c>.
    /// </remarks>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static void ThrowIfInvalid(ReadOnlySpan<byte> envelope)
    {
        var error = Validate(envelope);
        if (error != SignalPayloadError.None)
        {
            throw new InvalidDataException("Speech signal payload rejected: " + error + ".");
        }
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (var i = 0u; i < table.Length; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ Crc32Polynomial : entry >> 1;
            }

            table[i] = entry;
        }

        return table;
    }
}
