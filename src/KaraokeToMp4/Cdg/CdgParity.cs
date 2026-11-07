namespace KaraokeToMp4.Cdg;

/// <summary>Result of checking/repairing one CD+G packet.</summary>
public enum CdgPacketStatus
{
    /// <summary>Parity checks out.</summary>
    Ok,

    /// <summary>One or two damaged symbols were repaired.</summary>
    Repaired,

    /// <summary>Too damaged to repair (more than two bad symbols).</summary>
    Unrepairable,
}

/// <summary>
/// Reed-Solomon error detection and correction for CD+G subcode packets.
/// </summary>
/// <remarks>
/// Each 24-byte packet carries 6-bit symbols protected by two codes over GF(2^6) (primitive polynomial
/// x^6 + x + 1): a (4,2) "Q" code over symbols 0-3 and a (24,20) "P" code over all 24 symbols.
/// The P code has 4 check symbols, so up to 2 damaged symbols per packet can be located and fixed.
/// Rips made from scratched discs (or with poor drives) typically contain a few hundred such packets;
/// left alone, a damaged XOR tile leaves a stray block on screen until the next screen clear.
/// Some rippers zero the parity bytes; such files can't be checked (see <see cref="CdgInspector"/>).
/// </remarks>
public static class CdgParity
{
    private static readonly byte[] Exp = new byte[126];
    private static readonly byte[] Log = new byte[64];

    static CdgParity()
    {
        int x = 1;
        for (int i = 0; i < 63; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x40) != 0) x ^= 0x43;
        }
        for (int i = 63; i < 126; i++) Exp[i] = Exp[i - 63];
    }

    private static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]];
    private static int Div(int a, int b) => a == 0 ? 0 : Exp[(Log[a] - Log[b] + 63) % 63];
    private static int Pow(int k) => Exp[((k % 63) + 63) % 63];

    /// <summary>True when both the P and Q parity of <paramref name="packet"/> check out.</summary>
    public static bool IsValid(ReadOnlySpan<byte> packet)
    {
        Span<int> s = stackalloc int[4];
        return !PSyndromes(packet, s) && QOk(packet);
    }

    /// <summary>
    /// Checks <paramref name="packet"/> and repairs up to two damaged symbols in place. The top two bits
    /// of each byte (other subcode channels) are left untouched.
    /// </summary>
    public static CdgPacketStatus Repair(Span<byte> packet)
    {
        if (packet.Length != CdgDecoder.PacketSize) throw new ArgumentException("A CD+G packet is 24 bytes.", nameof(packet));

        Span<int> syn = stackalloc int[4];
        if (!PSyndromes(packet, syn)) return CdgPacketStatus.Ok;

        // One error at position j with magnitude e: S_k = e * a^(k(23-j)).
        if (syn[0] != 0)
        {
            int e = syn[0];
            for (int j = 0; j < 24; j++)
            {
                bool match = true;
                for (int k = 1; k < 4 && match; k++) match = syn[k] == Mul(e, Pow(k * (23 - j)));
                if (match)
                {
                    Flip(packet, j, e);
                    return CdgPacketStatus.Repaired;
                }
            }
        }

        // Two errors at i < j: solve the magnitudes from S0 and S1, then confirm with S2 and S3.
        for (int i = 0; i < 24; i++)
        {
            int xi = Pow(23 - i);
            for (int j = i + 1; j < 24; j++)
            {
                int xj = Pow(23 - j);
                int det = xi ^ xj;
                if (det == 0) continue;
                int ej = Div(syn[1] ^ Mul(syn[0], xi), det);
                int ei = syn[0] ^ ej;
                if (ei == 0 || ej == 0) continue;
                if (syn[2] == (Mul(ei, Pow(2 * (23 - i))) ^ Mul(ej, Pow(2 * (23 - j)))) &&
                    syn[3] == (Mul(ei, Pow(3 * (23 - i))) ^ Mul(ej, Pow(3 * (23 - j)))))
                {
                    Flip(packet, i, ei);
                    Flip(packet, j, ej);
                    return CdgPacketStatus.Repaired;
                }
            }
        }
        return CdgPacketStatus.Unrepairable;
    }

    /// <summary>
    /// Fills in the Q parity (bytes 2-3) and P parity (bytes 20-23) of <paramref name="packet"/> from its
    /// other symbols, e.g. when writing CD+G data.
    /// </summary>
    public static void ComputeParity(Span<byte> packet)
    {
        if (packet.Length != CdgDecoder.PacketSize) throw new ArgumentException("A CD+G packet is 24 bytes.", nameof(packet));

        // Q: s0^s1^s2^s3 = 0 and a^3 s0 ^ a^2 s1 ^ a s2 ^ s3 = 0.
        int s0 = packet[0] & 0x3F, s1 = packet[1] & 0x3F;
        int a = s0 ^ s1;
        int b = Mul(Pow(3), s0) ^ Mul(Pow(2), s1);
        int q2 = Div(a ^ b, Pow(1) ^ 1);
        int q3 = a ^ q2;
        packet[2] = (byte)((packet[2] & 0xC0) | q2);
        packet[3] = (byte)((packet[3] & 0xC0) | q3);

        // P: systematic RS(24,20); the parity is the remainder of m(x)*x^4 divided by
        // g(x) = (x - 1)(x - a)(x - a^2)(x - a^3), so that c(a^k) = 0 for k = 0..3.
        Span<int> g = stackalloc int[5];
        g.Clear();
        g[0] = 1;
        for (int k = 0; k < 4; k++)
        {
            // multiply g by (x + a^k): coefficients highest-degree first.
            int root = Pow(k);
            for (int i = 4; i > 0; i--) g[i] = g[i] ^ Mul(g[i - 1], root);
        }
        Span<int> rem = stackalloc int[4];
        rem.Clear();
        for (int i = 0; i < 20; i++)
        {
            int feedback = (packet[i] & 0x3F) ^ rem[0];
            for (int j = 0; j < 3; j++) rem[j] = rem[j + 1] ^ Mul(feedback, g[j + 1]);
            rem[3] = Mul(feedback, g[4]);
        }
        for (int j = 0; j < 4; j++) packet[20 + j] = (byte)((packet[20 + j] & 0xC0) | rem[j]);
    }

    /// <summary>
    /// Repairs a packet using only its Q code (symbols 0-3: command, instruction and two check symbols), for
    /// rips that carry Q but not P check data. Q can locate and fix one bad symbol among those four.
    /// </summary>
    public static CdgPacketStatus RepairQ(Span<byte> packet)
    {
        int s0 = packet[0] & 0x3F, s1 = packet[1] & 0x3F, s2 = packet[2] & 0x3F, s3 = packet[3] & 0x3F;
        int syn0 = s0 ^ s1 ^ s2 ^ s3;
        int syn1 = Mul(Pow(3), s0) ^ Mul(Pow(2), s1) ^ Mul(Pow(1), s2) ^ s3;
        if (syn0 == 0 && syn1 == 0) return CdgPacketStatus.Ok;
        if (syn0 == 0 || syn1 == 0) return CdgPacketStatus.Unrepairable;

        // One error e at position j: syn0 = e, syn1 = e * a^(3 - j).
        int k = (Log[syn1] - Log[syn0] + 63) % 63;
        int j = 3 - k;
        if (j is < 0 or > 3) return CdgPacketStatus.Unrepairable;
        Flip(packet, j, syn0);
        return CdgPacketStatus.Repaired;
    }

    /// <summary>The four P syndromes of a packet (all zero when it is intact).</summary>
    internal static void Syndromes(ReadOnlySpan<byte> packet, Span<int> syndromes) => PSyndromes(packet, syndromes);

    /// <summary>True when the Q parity (symbols 0-3: command, instruction, Q check) is consistent.</summary>
    internal static bool QValid(ReadOnlySpan<byte> packet) => QOk(packet);

    /// <summary>
    /// Erasure decoding: assuming the damage is at exactly <paramref name="positions"/> (up to 4), solves for
    /// the error values. Returns false when no such correction fits all four syndromes. With 4 positions a
    /// solution always exists, so callers must validate the result some other way.
    /// </summary>
    internal static bool TrySolveErasures(ReadOnlySpan<int> syndromes, ReadOnlySpan<int> positions, Span<int> values)
    {
        int m = positions.Length;
        if (m is < 1 or > 4) return false;
        // Rows k = 0..3: sum_j e_j * X_j^k = S_k, with X_j = a^(23 - position_j). Gaussian elimination over GF(64).
        Span<int> a = stackalloc int[4 * 5];
        for (int k = 0; k < 4; k++)
        {
            for (int j = 0; j < m; j++) a[k * 5 + j] = Pow(k * (23 - positions[j]));
            a[k * 5 + m] = syndromes[k];
        }
        int row = 0;
        for (int col = 0; col < m; col++)
        {
            int pivot = -1;
            for (int r = row; r < 4; r++) if (a[r * 5 + col] != 0) { pivot = r; break; }
            if (pivot < 0) return false;
            if (pivot != row)
                for (int c = 0; c <= m; c++) (a[row * 5 + c], a[pivot * 5 + c]) = (a[pivot * 5 + c], a[row * 5 + c]);
            int inverse = Div(1, a[row * 5 + col]);
            for (int c = 0; c <= m; c++) a[row * 5 + c] = Mul(a[row * 5 + c], inverse);
            for (int r = 0; r < 4; r++)
            {
                if (r == row || a[r * 5 + col] == 0) continue;
                int f = a[r * 5 + col];
                for (int c = 0; c <= m; c++) a[r * 5 + c] ^= Mul(f, a[row * 5 + c]);
            }
            row++;
        }
        for (int r = row; r < 4; r++) if (a[r * 5 + m] != 0) return false; // inconsistent: not this pattern
        for (int j = 0; j < m; j++) values[j] = a[j * 5 + m];
        return true;
    }

    private static void Flip(Span<byte> packet, int index, int error) =>
        packet[index] = (byte)((packet[index] & 0xC0) | ((packet[index] ^ error) & 0x3F));

    /// <summary>Computes the 4 P syndromes; returns true if any is non-zero (packet damaged).</summary>
    private static bool PSyndromes(ReadOnlySpan<byte> p, Span<int> syn)
    {
        bool any = false;
        for (int k = 0; k < 4; k++)
        {
            int acc = 0;
            for (int i = 0; i < 24; i++) acc ^= Mul(Pow(k * (23 - i)), p[i] & 0x3F);
            syn[k] = acc;
            any |= acc != 0;
        }
        return any;
    }

    private static bool QOk(ReadOnlySpan<byte> p)
    {
        int s0 = p[0] & 0x3F, s1 = p[1] & 0x3F, s2 = p[2] & 0x3F, s3 = p[3] & 0x3F;
        return (s0 ^ s1 ^ s2 ^ s3) == 0 && (Mul(Pow(3), s0) ^ Mul(Pow(2), s1) ^ Mul(Pow(1), s2) ^ s3) == 0;
    }
}
