namespace LoadTest.Controller.Packaging;

/// <summary>Divide un CSV fra gli agenti (rig.deployment con partition: true). L'intestazione va a tutti; le righe dati si ripartiscono secondo le quote.</summary>
public static class CsvPartitioner
{
    public static byte[][] Slice(byte[] data, IReadOnlyList<double> shares)
    {
        var n = shares.Count;
        var outs = Enumerable.Range(0, n).Select(_ => new MemoryStream()).ToArray();
        var records = SplitRecords(data);
        if (records.Count > 0)
        {
            foreach (var o in outs) o.Write(data, records[0].Start, records[0].Length);
            var counts = new long[n];
            for (var k = 1; k < records.Count; k++)
            {
                // assegna la riga all'agente più "in difetto" rispetto alla sua quota
                var best = 0;
                var bestDeficit = double.NegativeInfinity;
                for (var i = 0; i < n; i++)
                {
                    var deficit = shares[i] * k - counts[i];
                    if (deficit > bestDeficit) { bestDeficit = deficit; best = i; }
                }
                counts[best]++;
                outs[best].Write(data, records[k].Start, records[k].Length);
            }
        }
        return outs.Select(o => o.ToArray()).ToArray();
    }

    /// <summary>Un record termina con '\n' fuori dalle virgolette (le righe a capo dentro un campo tra virgolette restano nello stesso record).</summary>
    private static List<(int Start, int Length)> SplitRecords(byte[] d)
    {
        var list = new List<(int, int)>();
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i < d.Length; i++)
        {
            if (d[i] == (byte)'"') inQuotes = !inQuotes;
            else if (d[i] == (byte)'\n' && !inQuotes)
            {
                list.Add((start, i + 1 - start));
                start = i + 1;
            }
        }
        if (start < d.Length) list.Add((start, d.Length - start));
        return list;
    }
}
