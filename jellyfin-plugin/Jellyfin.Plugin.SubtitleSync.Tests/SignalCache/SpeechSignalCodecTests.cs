using System;
using System.Buffers.Binary;
using System.Linq;
using Jellyfin.Plugin.SubtitleSync.SignalCache;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.SignalCache;

/// <summary>
/// Covers <see cref="SpeechSignalCodec"/>, the bit-packing and the envelope
/// that wraps it.
/// </summary>
/// <remarks>
/// Two separate concerns are guarded here. The packing is a pure round-trip
/// property: a 100 Hz signal whose length is not a multiple of eight is the
/// normal case, not the edge case, so the tail byte and its padding get the
/// most attention. The envelope is a trust boundary: its bytes arrive from a
/// browser over POST and are later read back off a disk that may have been
/// truncated mid-write, so every field is checked before a single sample is
/// handed to a caller.
/// </remarks>
public class SpeechSignalCodecTests
{
    // ------------------------------------------------------------------
    // Packing
    // ------------------------------------------------------------------

    /// <summary>
    /// The stated storage cost. A bit per 10 ms sample and nothing else.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(7, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(360_000, 45_000)]
    public void PackedLengthIsOneBitPerSample(int sampleCount, int expectedBytes)
    {
        Assert.Equal(expectedBytes, SpeechSignalCodec.PackedLength(sampleCount));
    }

    /// <summary>
    /// The round trip, over every length in a window that crosses several byte
    /// boundaries. A length that is not a multiple of eight is the case that
    /// breaks naive implementations.
    /// </summary>
    [Fact]
    public void PackUnpackRoundTripsEveryLengthAcrossByteBoundaries()
    {
        for (var length = 0; length <= 40; length++)
        {
            var samples = PseudoRandomSignal(length, seed: length);

            var packed = SpeechSignalCodec.Pack(samples);
            var recovered = SpeechSignalCodec.Unpack(packed, length);

            Assert.Equal(SpeechSignalCodec.PackedLength(length), packed.Length);
            Assert.Equal(samples, recovered);
        }
    }

    /// <summary>
    /// A realistic run length. An hour of runtime at 100 Hz.
    /// </summary>
    [Fact]
    public void PackUnpackRoundTripsAnHourOfSignal()
    {
        var samples = PseudoRandomSignal(360_000, seed: 7);

        var packed = SpeechSignalCodec.Pack(samples);

        Assert.Equal(45_000, packed.Length);
        Assert.Equal(samples, SpeechSignalCodec.Unpack(packed, samples.Length));
    }

    /// <summary>
    /// Bit order is part of the wire contract with the browser, so it is pinned
    /// explicitly rather than left to whatever the implementation happens to do.
    /// Sample <c>i</c> is bit <c>i % 8</c> of byte <c>i / 8</c>, least
    /// significant first.
    /// </summary>
    [Fact]
    public void PacksLeastSignificantBitFirst()
    {
        var samples = new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 1 };

        var packed = SpeechSignalCodec.Pack(samples);

        Assert.Equal(new byte[] { 0b0000_0001, 0b0000_0010 }, packed);
    }

    /// <summary>
    /// The unused high bits of the final byte are zero. They are checked on
    /// decode, so a packer that left them dirty would produce payloads its own
    /// validator rejects.
    /// </summary>
    [Fact]
    public void LeavesTrailingPaddingBitsZero()
    {
        var samples = Enumerable.Repeat((byte)1, 9).ToArray();

        var packed = SpeechSignalCodec.Pack(samples);

        Assert.Equal(new byte[] { 0b1111_1111, 0b0000_0001 }, packed);
    }

    /// <summary>
    /// Any non-zero sample is speech. The browser hands over a
    /// <c>Float32Array</c> of exactly 0 and 1, but nothing about the transport
    /// guarantees that, and silently mapping 2 to "not speech" would be worse
    /// than either rejecting it or treating it as set.
    /// </summary>
    [Fact]
    public void TreatsAnyNonZeroSampleAsSpeech()
    {
        Assert.Equal(new byte[] { 0b0000_0011 }, SpeechSignalCodec.Pack(new byte[] { 1, 255 }));
    }

    /// <summary>
    /// Unpacking has to be told how many samples the packed bytes represent,
    /// and a count the bytes cannot possibly hold is a corrupt input, not a
    /// value to guess at.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 9)]
    [InlineData(2, 8)]
    public void UnpackRejectsASampleCountThePackedBytesCannotHold(int packedLength, int sampleCount)
    {
        var packed = new byte[packedLength];

        Assert.Throws<ArgumentException>(() => SpeechSignalCodec.Unpack(packed, sampleCount));
    }

    /// <summary>
    /// A negative count is nonsense rather than merely inconsistent.
    /// </summary>
    [Fact]
    public void UnpackRejectsANegativeSampleCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechSignalCodec.Unpack(Array.Empty<byte>(), -1));
    }

    // ------------------------------------------------------------------
    // The envelope
    // ------------------------------------------------------------------

    /// <summary>
    /// The whole point: what goes in over POST comes back out of GET.
    /// </summary>
    [Fact]
    public void EncodeDecodeRoundTripsIncludingARaggedLength()
    {
        var samples = PseudoRandomSignal(1_234, seed: 3);

        var envelope = SpeechSignalCodec.Encode(samples);

        Assert.Equal(SignalPayloadError.None, SpeechSignalCodec.Validate(envelope));
        Assert.Equal(1_234, SpeechSignalCodec.ReadSampleCount(envelope));
        Assert.Equal(samples, SpeechSignalCodec.Decode(envelope));
    }

    /// <summary>
    /// An empty signal is a legitimate, if useless, answer. It must not be
    /// mistaken for a corrupt payload.
    /// </summary>
    [Fact]
    public void EncodeDecodeRoundTripsAnEmptySignal()
    {
        var envelope = SpeechSignalCodec.Encode(Array.Empty<byte>());

        Assert.Equal(SpeechSignalCodec.HeaderLength, envelope.Length);
        Assert.Equal(SignalPayloadError.None, SpeechSignalCodec.Validate(envelope));
        Assert.Empty(SpeechSignalCodec.Decode(envelope));
    }

    /// <summary>
    /// The header is fixed and self-describing, so a payload from some other
    /// producer, or a text error page saved by mistake, is rejected on sight.
    /// </summary>
    [Fact]
    public void RejectsAPayloadWithoutTheMagic()
    {
        var envelope = SpeechSignalCodec.Encode(new byte[] { 1, 0, 1 });
        envelope[0] = (byte)'X';

        Assert.Equal(SignalPayloadError.BadMagic, SpeechSignalCodec.Validate(envelope));
    }

    /// <summary>
    /// Anything shorter than the header cannot even be inspected.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(11)]
    public void RejectsAPayloadShorterThanTheHeader(int length)
    {
        Assert.Equal(SignalPayloadError.TooShort, SpeechSignalCodec.Validate(new byte[length]));
    }

    /// <summary>
    /// Truncation is the failure mode that matters: a half-uploaded POST, or a
    /// cache file written by a process that died. It must never decode to a
    /// short-but-plausible signal, because a signal that is silently missing its
    /// tail corrupts the sync it feeds without ever looking wrong.
    /// </summary>
    [Fact]
    public void RejectsATruncatedPayload()
    {
        var envelope = SpeechSignalCodec.Encode(PseudoRandomSignal(1_000, seed: 11));
        var truncated = envelope[..(envelope.Length - 1)];

        Assert.Equal(SignalPayloadError.LengthMismatch, SpeechSignalCodec.Validate(truncated));
    }

    /// <summary>
    /// Trailing junk is as suspicious as a missing tail: the length is implied
    /// exactly by the sample count, so there is nothing legitimate to append.
    /// </summary>
    [Fact]
    public void RejectsAPayloadWithTrailingBytes()
    {
        var envelope = SpeechSignalCodec.Encode(PseudoRandomSignal(100, seed: 12));
        var padded = envelope.Concat(new byte[] { 0 }).ToArray();

        Assert.Equal(SignalPayloadError.LengthMismatch, SpeechSignalCodec.Validate(padded));
    }

    /// <summary>
    /// A flipped bit inside the packed body survives the length check, so the
    /// checksum is the only thing standing between a corrupt cache file and a
    /// silently wrong sync.
    /// </summary>
    [Fact]
    public void RejectsAPayloadWhoseBodyHasBeenAltered()
    {
        var envelope = SpeechSignalCodec.Encode(PseudoRandomSignal(500, seed: 13));
        envelope[^1] ^= 0b0000_0001;

        Assert.Equal(SignalPayloadError.ChecksumMismatch, SpeechSignalCodec.Validate(envelope));
    }

    /// <summary>
    /// Padding bits carry no signal, so a payload that sets them is either from
    /// a buggy producer or is trying to smuggle something past a byte-for-byte
    /// comparison. Either way the encoding is not canonical and is refused.
    /// </summary>
    [Fact]
    public void RejectsAPayloadWithNonZeroPaddingBits()
    {
        var samples = PseudoRandomSignal(9, seed: 14);
        var packed = SpeechSignalCodec.Pack(samples);
        packed[^1] |= 0b1000_0000;

        var envelope = BuildEnvelope(samples.Length, packed);

        Assert.Equal(SignalPayloadError.PaddingNotZero, SpeechSignalCodec.Validate(envelope));
    }

    /// <summary>
    /// A declared sample count beyond the cap is refused from the header alone,
    /// before anything is allocated for it. This is the allocation-bomb guard:
    /// twelve bytes of attacker input must not be able to ask for a four
    /// gigabyte buffer.
    /// </summary>
    [Fact]
    public void RejectsAnAbsurdSampleCountWithoutAllocating()
    {
        var envelope = new byte[SpeechSignalCodec.HeaderLength];
        "SSC1"u8.CopyTo(envelope);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(4), uint.MaxValue);

        Assert.Equal(SignalPayloadError.SampleCountOutOfRange, SpeechSignalCodec.Validate(envelope));
    }

    /// <summary>
    /// The cap itself, at the boundary. Twenty-four hours at 100 Hz is far more
    /// runtime than any single media file, and the packed body it implies is
    /// around a megabyte.
    /// </summary>
    [Fact]
    public void CapsTheSampleCountAtTwentyFourHoursOfRuntime()
    {
        Assert.Equal(8_640_000, SpeechSignalCodec.MaxSampleCount);
        Assert.Equal(SpeechSignalCodec.HeaderLength + 1_080_000, SpeechSignalCodec.MaxEnvelopeLength);

        var justOver = new byte[SpeechSignalCodec.HeaderLength];
        "SSC1"u8.CopyTo(justOver);
        BinaryPrimitives.WriteUInt32LittleEndian(justOver.AsSpan(4), (uint)SpeechSignalCodec.MaxSampleCount + 1);

        Assert.Equal(SignalPayloadError.SampleCountOutOfRange, SpeechSignalCodec.Validate(justOver));
    }

    /// <summary>
    /// Encoding refuses to produce something its own validator would reject.
    /// </summary>
    [Fact]
    public void EncodeRejectsASignalLongerThanTheCap()
    {
        Assert.Throws<ArgumentException>(
            () => SpeechSignalCodec.Encode(new byte[SpeechSignalCodec.MaxSampleCount + 1]));
    }

    /// <summary>
    /// <see cref="SpeechSignalCodec.Decode"/> is the unchecked fast path used
    /// once <see cref="SpeechSignalCodec.Validate"/> has passed. Called on
    /// rubbish it throws rather than returning garbage.
    /// </summary>
    [Fact]
    public void DecodeThrowsOnAnInvalidPayloadRatherThanReturningGarbage()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => SpeechSignalCodec.Decode(new byte[7]));
    }

    // ------------------------------------------------------------------
    // The checksum primitive
    // ------------------------------------------------------------------

    /// <summary>
    /// CRC-32/ISO-HDLC against its published check value, so the table is known
    /// good rather than merely self-consistent.
    /// </summary>
    [Fact]
    public void Crc32MatchesTheStandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, SpeechSignalCodec.Crc32("123456789"u8));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static byte[] PseudoRandomSignal(int length, int seed)
    {
        var random = new Random(seed);
        var samples = new byte[length];
        for (var i = 0; i < length; i++)
        {
            samples[i] = (byte)random.Next(2);
        }

        return samples;
    }

    private static byte[] BuildEnvelope(int sampleCount, byte[] packed)
    {
        var envelope = new byte[SpeechSignalCodec.HeaderLength + packed.Length];
        "SSC1"u8.CopyTo(envelope);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(4), (uint)sampleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(8), SpeechSignalCodec.Crc32(packed));
        packed.CopyTo(envelope.AsSpan(SpeechSignalCodec.HeaderLength));
        return envelope;
    }
}
