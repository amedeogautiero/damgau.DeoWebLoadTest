namespace LoadTest.Controller.Rig;

public static class IntegerSplit
{
    /// <summary>Ripartisce <paramref name="total"/> in interi secondo le quote (metodo del resto maggiore: la somma è esatta).</summary>
    public static int[] LargestRemainder(int total, IReadOnlyList<double> fractions)
    {
        var n = fractions.Count;
        var result = new int[n];
        if (n == 0 || total <= 0) return result;
        var sum = fractions.Sum();
        if (sum <= 0) return result;
        var rem = new (int Index, double Frac)[n];
        var used = 0;
        for (var i = 0; i < n; i++)
        {
            var exact = total * (fractions[i] / sum);
            result[i] = (int)Math.Floor(exact);
            used += result[i];
            rem[i] = (i, exact - result[i]);
        }
        foreach (var r in rem.OrderByDescending(x => x.Frac).ThenBy(x => x.Index).Take(total - used)) result[r.Index]++;
        return result;
    }

    /// <summary>Come <see cref="LargestRemainder"/> ma con un tetto per voce (0 = nessun tetto): l'eccesso va alle altre voci in proporzione.
    /// Se tutte sono al tetto la somma resta sotto il totale.</summary>
    public static int[] Allocate(int total, IReadOnlyList<double> shares, IReadOnlyList<int> caps)
    {
        var n = shares.Count;
        var result = new int[n];
        var fixedAtCap = new bool[n];
        var remaining = total;
        while (remaining > 0)
        {
            var free = Enumerable.Range(0, n).Where(i => !fixedAtCap[i]).ToList();
            if (free.Count == 0) break;
            var alloc = LargestRemainder(remaining, free.Select(i => shares[i]).ToList());
            var overflow = false;
            for (var k = 0; k < free.Count; k++)
            {
                var i = free[k];
                if (caps[i] > 0 && alloc[k] > caps[i])
                {
                    result[i] = caps[i];
                    remaining -= caps[i];
                    fixedAtCap[i] = true;
                    overflow = true;
                }
            }
            if (overflow) continue;
            for (var k = 0; k < free.Count; k++) result[free[k]] = alloc[k];
            break;
        }
        return result;
    }
}
