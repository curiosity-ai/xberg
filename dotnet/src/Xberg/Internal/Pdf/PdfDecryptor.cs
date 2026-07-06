// PDF standard security handler (ISO 32000-1 §7.6) — decryption with the empty
// user password. Supports RC4 (V1/V2, R2-4), AESV2 (R4) and AESV3 (R6, /AESV3).
using System.Security.Cryptography;

namespace Xberg.Internal.Pdf;

public sealed class PdfDecryptor
{
    private readonly byte[] _key;
    private readonly bool _aes;
    private readonly bool _v5;        // R5/R6: file key is used directly (no per-object key)
    private readonly bool _encryptMetadata;

    private static readonly byte[] Pad = {
        0x28,0xBF,0x4E,0x5E,0x4E,0x75,0x8A,0x41,0x64,0x00,0x4E,0x56,0xFF,0xFA,0x01,0x08,
        0x2E,0x2E,0x00,0xB6,0xD0,0x68,0x3E,0x80,0x2F,0x0C,0xA9,0xFE,0x64,0x53,0x69,0x7A
    };

    private PdfDecryptor(byte[] key, bool aes, bool v5, bool encMeta)
    { _key = key; _aes = aes; _v5 = v5; _encryptMetadata = encMeta; }

    public static PdfDecryptor? TryCreate(PdfDict enc, byte[]? id, PdfDocument doc)
    {
        string? filter = doc.Resolve(enc.Get("Filter")).AsName();
        if (filter != null && filter != "Standard") return null; // only Standard handler

        int v = (int)(doc.Resolve(enc.Get("V")).AsLong() ?? 0);
        int r = (int)(doc.Resolve(enc.Get("R")).AsLong() ?? 0);
        int length = (int)(doc.Resolve(enc.Get("Length")).AsLong() ?? 40);
        long p = doc.Resolve(enc.Get("P")).AsLong() ?? 0;
        byte[] o = enc.Get("O").AsStringBytes() ?? Array.Empty<byte>();
        byte[] u = enc.Get("U").AsStringBytes() ?? Array.Empty<byte>();
        bool encMeta = doc.Resolve(enc.Get("EncryptMetadata")).AsBool() ?? true;

        bool aes = false;
        // V4/V5: crypt filters. Determine method from /CF /StdCF /CFM.
        if (v >= 4)
        {
            var cf = doc.Resolve(enc.Get("CF")).AsDict();
            var stdcf = doc.Resolve(cf?.Get("StdCF")).AsDict();
            string? cfm = doc.Resolve(stdcf?.Get("CFM")).AsName();
            if (cfm == "AESV2") { aes = true; length = 128; }
            else if (cfm == "AESV3") { aes = true; length = 256; }
            else if (cfm == "V2") aes = false;
        }

        if (r >= 5)
        {
            // AESV3 (R5/R6): derive 256-bit file key from empty password.
            byte[] oe = enc.Get("OE").AsStringBytes() ?? Array.Empty<byte>();
            byte[] ue = enc.Get("UE").AsStringBytes() ?? Array.Empty<byte>();
            byte[]? key = DeriveKeyR6(Array.Empty<byte>(), u, ue, r);
            if (key == null) return null;
            return new PdfDecryptor(key, true, true, encMeta);
        }

        // R2-R4: Algorithm 2 with empty user password.
        int keyLen = v == 1 ? 5 : length / 8;
        byte[] fileKey = ComputeKeyR234(Array.Empty<byte>(), o, (int)p, id ?? Array.Empty<byte>(), r, keyLen, encMeta);
        return new PdfDecryptor(fileKey, aes, false, encMeta);
    }

    private static byte[] ComputeKeyR234(byte[] password, byte[] o, int p, byte[] id, int r, int keyLen, bool encMeta)
    {
        using var md5 = MD5.Create();
        var input = new List<byte>();
        // Padded password (32 bytes).
        byte[] padded = new byte[32];
        int n = Math.Min(password.Length, 32);
        Array.Copy(password, padded, n);
        Array.Copy(Pad, 0, padded, n, 32 - n);
        input.AddRange(padded);
        input.AddRange(o.Length >= 32 ? o[..32] : Pad2(o, 32));
        input.Add((byte)(p & 0xFF));
        input.Add((byte)((p >> 8) & 0xFF));
        input.Add((byte)((p >> 16) & 0xFF));
        input.Add((byte)((p >> 24) & 0xFF));
        input.AddRange(id);
        if (r >= 4 && !encMeta) { input.Add(0xFF); input.Add(0xFF); input.Add(0xFF); input.Add(0xFF); }
        byte[] hash = md5.ComputeHash(input.ToArray());
        if (r >= 3)
            for (int i = 0; i < 50; i++) hash = md5.ComputeHash(hash[..keyLen]);
        return hash[..keyLen];
    }

    private static byte[] Pad2(byte[] src, int len)
    {
        byte[] r = new byte[len];
        Array.Copy(src, r, Math.Min(src.Length, len));
        return r;
    }

    // Algorithm 2.A (R6/R5) — empty password, verify via U/UE.
    private static byte[]? DeriveKeyR6(byte[] password, byte[] u, byte[] ue, int r)
    {
        if (u.Length < 48) return null;
        byte[] salt = u[32..40];      // validation salt
        byte[] keySalt = u[40..48];   // key salt
        // Intermediate key = Hash(password + keySalt).
        byte[] ik = Hash2B(Concat(password, keySalt), password, Array.Empty<byte>(), r);
        // File key = AES-256 no-padding CBC decrypt of UE with IV=0.
        try
        {
            using var aes = Aes.Create();
            aes.Key = ik; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
            aes.IV = new byte[16];
            using var dec = aes.CreateDecryptor();
            return dec.TransformFinalBlock(ue, 0, ue.Length);
        }
        catch { return null; }
    }

    private static byte[] Hash2B(byte[] input, byte[] password, byte[] udata, int r)
    {
        using var sha256 = SHA256.Create();
        byte[] k = sha256.ComputeHash(input);
        if (r < 6) return k; // R5 uses plain SHA-256
        // R6 hardened hash (Algorithm 2.B).
        int round = 0;
        while (true)
        {
            var k1List = new List<byte>();
            byte[] block = Concat(password, k, udata);
            for (int i = 0; i < 64; i++) k1List.AddRange(block);
            byte[] k1 = k1List.ToArray();
            byte[] e;
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
                aes.Key = k[..16]; aes.IV = k[16..32];
                using var enc = aes.CreateEncryptor();
                e = enc.TransformFinalBlock(k1, 0, k1.Length);
            }
            int mod = 0; for (int i = 0; i < 16; i++) mod += e[i];
            mod %= 3;
            using var h = mod == 0 ? (HashAlgorithm)SHA256.Create() : mod == 1 ? SHA384.Create() : SHA512.Create();
            k = h.ComputeHash(e);
            round++;
            if (round >= 64 && (e[^1] & 0xFF) <= round - 32) break;
        }
        return k[..32];
    }

    private static byte[] Concat(params byte[][] arrs)
    {
        int n = 0; foreach (var a in arrs) n += a.Length;
        byte[] r = new byte[n]; int p = 0;
        foreach (var a in arrs) { Array.Copy(a, 0, r, p, a.Length); p += a.Length; }
        return r;
    }

    private byte[] ObjectKey(int num, int gen)
    {
        if (_v5) return _key;
        var input = new List<byte>(_key);
        input.Add((byte)(num & 0xFF));
        input.Add((byte)((num >> 8) & 0xFF));
        input.Add((byte)((num >> 16) & 0xFF));
        input.Add((byte)(gen & 0xFF));
        input.Add((byte)((gen >> 8) & 0xFF));
        if (_aes) { input.Add(0x73); input.Add(0x41); input.Add(0x6C); input.Add(0x54); } // "sAlT"
        using var md5 = MD5.Create();
        byte[] h = md5.ComputeHash(input.ToArray());
        int len = Math.Min(_key.Length + 5, 16);
        return h[..len];
    }

    private byte[] DecryptBytes(byte[] data, int num, int gen)
    {
        if (data.Length == 0) return data;
        byte[] key = ObjectKey(num, gen);
        if (_aes)
        {
            if (data.Length < 16) return Array.Empty<byte>();
            try
            {
                using var aes = Aes.Create();
                aes.Key = _v5 ? _key : key;
                aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
                byte[] iv = data[..16];
                aes.IV = iv;
                using var dec = aes.CreateDecryptor();
                byte[] plain = dec.TransformFinalBlock(data, 16, data.Length - 16);
                // Strip PKCS#7 padding.
                if (plain.Length > 0)
                {
                    int pad = plain[^1];
                    if (pad >= 1 && pad <= 16 && pad <= plain.Length) plain = plain[..^pad];
                }
                return plain;
            }
            catch { return Array.Empty<byte>(); }
        }
        return Rc4(key, data);
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var outp = new byte[data.Length];
        int x = 0, y = 0;
        for (int k = 0; k < data.Length; k++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            outp[k] = (byte)(data[k] ^ s[(s[x] + s[y]) & 0xFF]);
        }
        return outp;
    }

    /// <summary>Decrypt all strings and stream data reachable within an object.</summary>
    public PdfObject DecryptObject(PdfObject o, int num, int gen)
    {
        switch (o)
        {
            case PdfString s:
                return new PdfString(DecryptBytes(s.Bytes, num, gen));
            case PdfArray a:
                var na = new PdfArray();
                foreach (var it in a.Items) na.Items.Add(DecryptObject(it, num, gen));
                return na;
            case PdfDict d:
                return DecryptDict(d, num, gen);
            case PdfStream st:
                var nd = (PdfDict)DecryptDict(st.Dict, num, gen);
                byte[] raw = DecryptBytes(st.RawData, num, gen);
                return new PdfStream(nd, raw);
            default:
                return o;
        }
    }

    private PdfObject DecryptDict(PdfDict d, int num, int gen)
    {
        var nd = new PdfDict();
        foreach (var kv in d.Map) nd.Map[kv.Key] = DecryptObject(kv.Value, num, gen);
        return nd;
    }

    /// <summary>Decrypt a stream object's raw data (dict kept as-is except nested strings).</summary>
    public PdfStream DecryptStreamObject(PdfStream st, int num, int gen)
    {
        byte[] raw = DecryptBytes(st.RawData, num, gen);
        return new PdfStream(st.Dict, raw);
    }
}
