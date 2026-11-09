using System.IO.Compression;
using HdrHistogram;
using LoadTest.Metrics;

namespace LoadTest.Reports.Storage;

/// <summary>
/// Serializzazione degli istogrammi per il repository (PROPOSTA-CONTRATTO).
/// Formato v1: 1 byte di versione + Deflate di coppie (valore, conteggio) come varint, solo per i valori registrati.
/// Indipendente dall'API di encoding della libreria HdrHistogram; isolato qui per poterlo sostituire (es. con l'encoding V2 compresso).
/// Il valore ricostruito è quello "equivalente" del bucket: percentili e conteggi si conservano entro la precisione (3 cifre).
/// </summary>
public static class HistogramCodec
{
    private const byte FormatVersion = 1;

    public static byte[] Encode(HistogramBase histogram)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(FormatVersion);
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            foreach (var v in histogram.RecordedValues())
            {
                WriteVarint(z, (ulong)v.ValueIteratedTo);
                WriteVarint(z, (ulong)v.CountAtValueIteratedTo);
            }
        }
        return ms.ToArray();
    }

    public static LongHistogram Decode(byte[]? data)
    {
        var h = HistogramSettings.Create();
        if (data is null || data.Length == 0) return h;
        if (data[0] != FormatVersion) throw new InvalidDataException($"formato istogramma {data[0]} non supportato");

        using var ms = new MemoryStream(data, 1, data.Length - 1);
        using var z = new DeflateStream(ms, CompressionMode.Decompress);
        while (TryReadVarint(z, out var value))
        {
            if (!TryReadVarint(z, out var count)) throw new InvalidDataException("istogramma troncato");
            if (count > 0) h.RecordValueWithCount(Math.Min((long)value, HistogramSettings.MaxMicros), (long)count);
        }
        return h;
    }

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }

    private static bool TryReadVarint(Stream s, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (true)
        {
            var b = s.ReadByte();
            if (b < 0)
            {
                if (shift == 0) return false;
                throw new InvalidDataException("istogramma troncato");
            }
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("varint non valido");
        }
    }
}
