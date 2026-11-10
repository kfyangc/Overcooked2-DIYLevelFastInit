using System;

namespace DIYLevelFastInit.BundleCodecs
{
    /// <summary>Encodes LZ4HC blocks with bounded hash chains, lazy matching and the final literal margin.</summary>
    internal sealed class Lz4BlockEncoder
    {
        private readonly int[] _positions = new int[65536];
        private readonly int[] _chain = new int[65536];
        private const int SearchDepth = 256;

        internal int Encode(byte[] source, int length, byte[] destination)
        {
            if (source == null || destination == null || length < 0 || length > source.Length
                || destination.Length < length + length / 255 + 16) throw new ArgumentException("Invalid LZ4 buffer");
            for (int i = 0; i < _positions.Length; i++) _positions[i] = -1;
            int position = 0, anchor = 0, written = 0;
            while (position <= length - 12)
            {
                int previous = Insert(source, position), selected;
                int match = FindMatch(source, position, previous, length - 5, out selected);
                if (match < 4)
                {
                    position++;
                    continue;
                }
                if (position < length - 12)
                {
                    int nextSelected;
                    int next = FindMatch(source, position + 1, _positions[Hash(Read32(source, position + 1))], length - 5, out nextSelected);
                    if (next > match + 1) { position++; continue; }
                }
                int literals = position - anchor;
                destination[written++] = (byte)((Math.Min(literals, 15) << 4) | Math.Min(match - 4, 15));
                if (literals >= 15) WriteLength(destination, ref written, literals - 15);
                Buffer.BlockCopy(source, anchor, destination, written, literals);
                written += literals;
                int distance = position - selected;
                destination[written++] = (byte)distance;
                destination[written++] = (byte)(distance >> 8);
                if (match >= 19) WriteLength(destination, ref written, match - 19);
                int end = position + match;
                for (int skipped = position + 1; skipped < end; skipped++) Insert(source, skipped);
                position = end;
                anchor = position;
            }
            int remaining = length - anchor;
            destination[written++] = (byte)(Math.Min(remaining, 15) << 4);
            if (remaining >= 15) WriteLength(destination, ref written, remaining - 15);
            Buffer.BlockCopy(source, anchor, destination, written, remaining);
            return written + remaining;
        }

        private int Insert(byte[] source, int position)
        {
            int hash = Hash(Read32(source, position)), previous = _positions[hash];
            _chain[position & 65535] = previous;
            _positions[hash] = position;
            return previous;
        }

        private int FindMatch(byte[] source, int position, int candidate, int limit, out int selected)
        {
            int best = 3, attempts = SearchDepth, oldest = Math.Max(0, position - 65535);
            uint value = Read32(source, position);
            selected = -1;
            while (candidate >= oldest && candidate < position && attempts-- != 0)
            {
                if (Read32(source, candidate) == value && source[candidate + best] == source[position + best])
                {
                    int match = 4;
                    while (position + match < limit && source[candidate + match] == source[position + match]) match++;
                    if (match > best)
                    {
                        best = match; selected = candidate;
                        if (position + best == limit) break;
                    }
                }
                int next = _chain[candidate & 65535];
                if (next >= candidate) break;
                candidate = next;
            }
            return selected < 0 ? 0 : best;
        }

        private static int Hash(uint value) { return (int)(unchecked(value * 2654435761U) >> 16); }

        private static uint Read32(byte[] bytes, int offset)
        {
            return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
        }

        private static void WriteLength(byte[] bytes, ref int position, int length)
        {
            while (length >= 255) { bytes[position++] = 255; length -= 255; }
            bytes[position++] = (byte)length;
        }
    }
}
