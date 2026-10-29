namespace FraudDetector.Core.Model;

/// <summary>
/// Os dados de treino depois de agrupados em faixas (bins).
/// Em vez do valor exato (ex.: 107,23), guardamos só o NÚMERO DA FAIXA em que ele cai (ex.: 142).
/// </summary>
public class BinnedData
{
    /// <summary>Faixa de cada valor, organizada por coluna: Bins[feature][linha]. Um byte por valor.</summary>
    public required byte[][] Bins { get; init; }

    /// <summary>
    /// Limite superior de cada faixa: a faixa b da feature f contém os valores &lt;= UpperBounds[f][b]
    /// (e maiores que o limite da faixa anterior). A última faixa não tem limite.
    /// </summary>
    public required double[][] UpperBounds { get; init; }

    public int FeatureCount => Bins.Length;
    public int RowCount => Bins.Length == 0 ? 0 : Bins[0].Length;
}

/// <summary>
/// Agrupa cada feature em até 255 faixas. É feito UMA vez, antes do treino.
/// Depois disso, nenhuma árvore precisa ordenar os dados de novo.
/// </summary>
public static class FeatureBinner
{
    public const int DefaultMaxBins = 255; // cabe em um byte

    public static BinnedData Bin(double[][] x, int maxBins = DefaultMaxBins)
    {
        if (maxBins < 2 || maxBins > 255)
            throw new ArgumentOutOfRangeException(nameof(maxBins), "Use entre 2 e 255 faixas.");

        int n = x.Length;
        int featureCount = x[0].Length;

        var bins = new byte[featureCount][];
        var upperBounds = new double[featureCount][];

        // Cada feature é independente das outras, então processamos várias ao mesmo tempo.
        Parallel.For(0, featureCount, f =>
        {
            var column = new double[n];
            for (int i = 0; i < n; i++)
                column[i] = x[i][f];

            var sorted = (double[])column.Clone();
            Array.Sort(sorted);

            double[] bounds = ComputeUpperBounds(sorted, maxBins);

            var binned = new byte[n];
            for (int i = 0; i < n; i++)
                binned[i] = (byte)FindBin(bounds, column[i]);

            upperBounds[f] = bounds;
            bins[f] = binned;
        });

        return new BinnedData { Bins = bins, UpperBounds = upperBounds };
    }

    /// <summary>
    /// Define os limites das faixas de uma feature (recebe os valores já ordenados).
    ///  - Poucos valores distintos (ex.: hora, colunas 0/1): uma faixa por valor, sem perda nenhuma.
    ///  - Muitos valores distintos (ex.: valor da compra): faixas com quantidades parecidas de
    ///    amostras (quantis). Onde há muitos dados, as faixas ficam mais estreitas.
    /// </summary>
    private static double[] ComputeUpperBounds(double[] sorted, int maxBins)
    {
        int n = sorted.Length;

        int distinct = 1;
        for (int i = 1; i < n; i++)
            if (sorted[i] != sorted[i - 1])
                distinct++;

        var bounds = new List<double>();

        if (distinct <= maxBins)
        {
            // Um limite entre cada par de valores distintos vizinhos (no ponto médio).
            for (int i = 1; i < n; i++)
                if (sorted[i] != sorted[i - 1])
                    bounds.Add((sorted[i - 1] + sorted[i]) / 2);
        }
        else
        {
            for (int k = 1; k < maxBins; k++)
            {
                // Posição que deixaria k/maxBins das amostras à esquerda.
                int pos = (int)((long)k * n / maxBins);

                // Valores iguais não podem ficar em faixas diferentes: avança até o valor mudar.
                while (pos > 0 && pos < n && sorted[pos] == sorted[pos - 1])
                    pos++;

                if (pos <= 0 || pos >= n)
                    continue;

                double bound = (sorted[pos - 1] + sorted[pos]) / 2;
                if (bounds.Count == 0 || bound > bounds[^1])
                    bounds.Add(bound);
            }
        }

        return bounds.ToArray();
    }

    /// <summary>
    /// Número da faixa de um valor: a primeira cujo limite superior é >= valor (busca binária).
    /// Se o valor passa de todos os limites, cai na última faixa.
    /// </summary>
    public static int FindBin(double[] upperBounds, double value)
    {
        int low = 0, high = upperBounds.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (value <= upperBounds[middle])
                high = middle;
            else
                low = middle + 1;
        }
        return low;
    }
}
