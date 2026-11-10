using System.IO.Compression;
using Google.Protobuf;
using HdrHistogram;
using LoadTest.Metrics;

namespace LoadTest.Protocol;

/// <summary>
/// Serializzazione degli istogrammi sul filo. Formato VARINT_DEFLATE_V1 = identico a Reports.HistogramCodec v1
/// (1 byte di versione + Deflate di coppie valore/conteggio in varint), così il controller può anche salvare i byte così come arrivano.
/// Duplicato di proposito finché non si decide dove vive il codec (G6: proposta di spostarlo in LoadTest.Metrics).
/// Il decoder tratta i dati come non fidati: limita il numero di coppie e rifiuta conteggi non validi.
/// </summary>
public static class WireHistogram
{
    private const byte FormatVersion = 1;
    private const int MaxPairs = 200_000;   // 3 cifre su 1 h in µs: ~70.000 bucket possibili

    public static HistogramFormat DefaultFormat => HistogramFormat.VarintDeflateV1;

    public static ByteString Encode(HistogramBase histogram, HistogramFormat format)
    {
        if (histogram.TotalCount == 0) return ByteString.Empty;
        if (format != HistogramFormat.VarintDeflateV1)
            throw new NotSupportedException($"formato istogramma {format} non supportato in scrittura");

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
        return ByteString.CopyFrom(ms.ToArray());
    }

    public static LongHistogram Decode(HistogramFormat format, ByteString data)
    {
        var h = HistogramSettings.Create();
        switch (format)
        {
            case HistogramFormat.None:
                if (!data.IsEmpty) throw new InvalidDataException("istogramma presente con formato 'none'");
                return h;
            case HistogramFormat.VarintDeflateV1:
                break;
            case HistogramFormat.HdrV2Compressed:
                throw new NotSupportedException("formato istogramma HDR V2 compresso non ancora supportato");
            default:
                throw new InvalidDataException($"formato istogramma {(int)format} sconosciuto");
        }

        if (data.IsEmpty) return h;
        var bytes = data.ToByteArray();
        if (bytes[0] != FormatVersion) throw new InvalidDataException($"versione formato istogramma {bytes[0]} non supportata");

        using var ms = new MemoryStream(bytes, 1, bytes.Length - 1);
        using var z = new DeflateStream(ms, CompressionMode.Decompress);
        var pairs = 0;
        while (TryReadVarint(z, out var value))
        {
            if (!TryReadVarint(z, out var count)) throw new InvalidDataException("istogramma troncato");
            if (++pairs > MaxPairs) throw new InvalidDataException("istogramma con troppe coppie");
            if (count > long.MaxValue) throw new InvalidDataException("conteggio istogramma non valido");
            if (count > 0) h.RecordValueWithCount(Math.Min((long)Math.Min(value, (ulong)long.MaxValue), HistogramSettings.MaxMicros), (long)count);
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
