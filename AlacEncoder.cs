namespace AirPlayCast;

/// <summary>
/// Produces "uncompressed" (verbatim) ALAC frames. AirPlay 1 receivers decode these
/// with their normal ALAC decoder, so no real compression is needed on a LAN.
/// </summary>
public static class AlacEncoder
{
    public const int FramesPerPacket = 352;

    /// <param name="pcm">Interleaved stereo 16-bit samples (L,R,L,R...), FramesPerPacket frames.</param>
    public static int Encode(ReadOnlySpan<short> pcm, Span<byte> output)
    {
        int frames = pcm.Length / 2;
        var w = new BitWriter(output);
        w.Write(1, 3);        // element type: CPE (channel pair)
        w.Write(0, 4);        // element instance tag
        w.Write(0, 12);       // unused header bits
        w.Write(1, 1);        // has explicit sample count
        w.Write(0, 2);        // uncompressed bytes
        w.Write(1, 1);        // is not compressed (verbatim)
        w.Write((uint)frames, 32);
        foreach (var s in pcm) w.Write((ushort)s, 16);
        w.Write(7, 3);        // ID_END
        return w.BytesWritten;
    }

    public static int MaxFrameBytes => (23 + 32 + FramesPerPacket * 2 * 16 + 3 + 7) / 8;

    private ref struct BitWriter(Span<byte> buffer)
    {
        private readonly Span<byte> _buf = buffer;
        private int _bitPos;

        public readonly int BytesWritten => (_bitPos + 7) / 8;

        public void Write(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                int byteIndex = _bitPos >> 3;
                if ((_bitPos & 7) == 0) _buf[byteIndex] = 0;
                if (((value >> i) & 1) != 0) _buf[byteIndex] |= (byte)(0x80 >> (_bitPos & 7));
                _bitPos++;
            }
        }
    }
}
