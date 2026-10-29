using System.Text;

namespace FraudDetector.Core.Model;

/// <summary>
/// Árvore de regressão: aprende a prever um NÚMERO (não uma classe).
/// No gradient boosting, esse número será o "erro que falta corrigir" de cada transação.
/// </summary>
public class RegressionTree
{
    /// <summary>Profundidade máxima: quantas perguntas seguidas a árvore pode fazer.</summary>
    public int MaxDepth { get; }

    /// <summary>Mínimo de amostras em cada folha. Evita folhas criadas para 1 ou 2 casos (decoreba).</summary>
    public int MinSamplesLeaf { get; }

    public TreeNode? Root { get; private set; }

    public RegressionTree(int maxDepth = 3, int minSamplesLeaf = 1)
    {
        MaxDepth = maxDepth;
        MinSamplesLeaf = Math.Max(1, minSamplesLeaf);
    }

    /// <summary>
    /// Treina a árvore. x[i] é o vetor de features da amostra i; y[i] é o alvo dela.
    /// </summary>
    public void Fit(double[][] x, double[] y)
    {
        if (x.Length == 0 || x.Length != y.Length)
            throw new ArgumentException("x e y precisam ter o mesmo tamanho (e não podem ser vazios).");

        // A árvore nunca copia os dados: cada nó guarda só os ÍNDICES das linhas que chegaram nele.
        int[] allRows = new int[x.Length];
        for (int i = 0; i < allRows.Length; i++)
            allRows[i] = i;

        Root = BuildNode(x, y, allRows, depth: 0);
    }

    /// <summary>Previsão: desce a árvore respondendo às perguntas até chegar a uma folha.</summary>
    public double Predict(double[] features) => FindLeaf(features).Value;

    /// <summary>Devolve a folha em que a amostra cai (o boosting usa isso para ajustar o valor das folhas).</summary>
    public TreeNode FindLeaf(double[] features)
    {
        TreeNode node = Root ?? throw new InvalidOperationException("A árvore ainda não foi treinada.");

        while (!node.IsLeaf)
            node = features[node.FeatureIndex] <= node.Threshold ? node.Left! : node.Right!;

        return node;
    }

    // ------------------------------------------------------------------
    // Construção recursiva
    // ------------------------------------------------------------------

    private TreeNode BuildNode(double[][] x, double[] y, int[] rows, int depth)
    {
        double sum = 0;
        foreach (int r in rows)
            sum += y[r];

        // Todo nó nasce como folha: prevê a média do alvo das suas amostras.
        var node = new TreeNode { Value = sum / rows.Length, SampleCount = rows.Length };

        // Critérios de parada: profundidade máxima ou amostras insuficientes para dividir.
        if (depth >= MaxDepth || rows.Length < 2 * MinSamplesLeaf)
            return node;

        Split? best = FindBestSplit(x, y, rows, sum);
        if (best is null)
            return node; // nenhum corte melhora a previsão: continua folha

        // Aplica o corte: separa as linhas entre esquerda (<=) e direita (>).
        var leftRows = new List<int>(rows.Length);
        var rightRows = new List<int>(rows.Length);
        foreach (int r in rows)
        {
            if (x[r][best.Value.FeatureIndex] <= best.Value.Threshold)
                leftRows.Add(r);
            else
                rightRows.Add(r);
        }

        node.FeatureIndex = best.Value.FeatureIndex;
        node.Threshold = best.Value.Threshold;

        // Recursão: cada filho repete o mesmo processo só com as suas linhas.
        node.Left = BuildNode(x, y, leftRows.ToArray(), depth + 1);
        node.Right = BuildNode(x, y, rightRows.ToArray(), depth + 1);
        return node;
    }

    private readonly record struct Split(int FeatureIndex, double Threshold, double Gain);

    /// <summary>
    /// Testa todos os cortes possíveis em todas as features e devolve o de maior ganho.
    ///
    /// Ganho = (somaEsq² / nEsq) + (somaDir² / nDir) − (somaTotal² / nTotal)
    ///
    /// Essa conta é equivalente a "quanto o erro quadrático diminui com o corte",
    /// mas só precisa de somas e contagens, o que permite avaliar cada corte em O(1).
    /// </summary>
    private Split? FindBestSplit(double[][] x, double[] y, int[] rows, double totalSum)
    {
        int n = rows.Length;
        int featureCount = x[rows[0]].Length;
        double parentScore = totalSum * totalSum / n;

        Split? best = null;
        double bestGain = 1e-12; // exige ganho real (evita cortes por ruído numérico)

        var values = new double[n];
        var targets = new double[n];

        for (int f = 0; f < featureCount; f++)
        {
            // 1) Copia a feature f e o alvo das linhas deste nó.
            for (int i = 0; i < n; i++)
            {
                values[i] = x[rows[i]][f];
                targets[i] = y[rows[i]];
            }

            // 2) Ordena pelo valor da feature (o alvo acompanha a ordenação).
            Array.Sort(values, targets);

            // 3) Varre da esquerda para a direita. A cada passo, uma amostra
            //    "muda de lado" e só precisamos atualizar a soma da esquerda.
            double leftSum = 0;
            for (int i = 0; i < n - 1; i++)
            {
                leftSum += targets[i];

                // Valores iguais não podem ser separados por um corte.
                if (values[i] == values[i + 1])
                    continue;

                int leftCount = i + 1;
                int rightCount = n - leftCount;
                if (leftCount < MinSamplesLeaf || rightCount < MinSamplesLeaf)
                    continue;

                double rightSum = totalSum - leftSum;
                double gain = leftSum * leftSum / leftCount
                            + rightSum * rightSum / rightCount
                            - parentScore;

                if (gain > bestGain)
                {
                    bestGain = gain;
                    // O corte fica no ponto médio entre os dois valores vizinhos.
                    best = new Split(f, (values[i] + values[i + 1]) / 2, gain);
                }
            }
        }

        return best;
    }

    // ------------------------------------------------------------------
    // Versão rápida: treino sobre dados agrupados em faixas (histogramas)
    // ------------------------------------------------------------------

    /// <summary>
    /// Mesma árvore, mesmo cálculo de ganho, mas sem ordenar nada:
    /// os candidatos a corte são só os limites das faixas (até 254 por feature).
    /// A árvore resultante guarda limites REAIS, então Predict continua recebendo os valores originais.
    /// </summary>
    public void Fit(BinnedData data, double[] y)
    {
        if (data.RowCount == 0 || data.RowCount != y.Length)
            throw new ArgumentException("Os dados e y precisam ter o mesmo tamanho (e não podem ser vazios).");

        int[] allRows = new int[data.RowCount];
        for (int i = 0; i < allRows.Length; i++)
            allRows[i] = i;

        Root = BuildNode(data, y, allRows, depth: 0);
    }

    private TreeNode BuildNode(BinnedData data, double[] y, int[] rows, int depth)
    {
        double sum = 0;
        foreach (int r in rows)
            sum += y[r];

        var node = new TreeNode { Value = sum / rows.Length, SampleCount = rows.Length };

        if (depth >= MaxDepth || rows.Length < 2 * MinSamplesLeaf)
            return node;

        BinSplit? best = FindBestSplit(data, y, rows, sum);
        if (best is null)
            return node;

        // Separa as linhas comparando o número da faixa (um byte), não o valor original.
        byte[] column = data.Bins[best.Value.FeatureIndex];
        int splitBin = best.Value.Bin;

        var leftRows = new List<int>(rows.Length);
        var rightRows = new List<int>(rows.Length);
        foreach (int r in rows)
        {
            if (column[r] <= splitBin)
                leftRows.Add(r);
            else
                rightRows.Add(r);
        }

        // "faixa <= splitBin" é o mesmo que "valor <= limite superior da faixa splitBin".
        node.FeatureIndex = best.Value.FeatureIndex;
        node.Threshold = data.UpperBounds[best.Value.FeatureIndex][splitBin];

        node.Left = BuildNode(data, y, leftRows.ToArray(), depth + 1);
        node.Right = BuildNode(data, y, rightRows.ToArray(), depth + 1);
        return node;
    }

    private readonly record struct BinSplit(int FeatureIndex, int Bin, double Gain);

    private BinSplit? FindBestSplit(BinnedData data, double[] y, int[] rows, double totalSum)
    {
        int n = rows.Length;
        double parentScore = totalSum * totalSum / n;

        // Cada feature acha o seu melhor corte de forma independente (em paralelo);
        // no final comparamos os campeões.
        var bestPerFeature = new BinSplit?[data.FeatureCount];

        Parallel.For(0, data.FeatureCount, f =>
        {
            byte[] column = data.Bins[f];
            int binCount = data.UpperBounds[f].Length + 1;
            if (binCount < 2)
                return; // feature com um único valor: não há o que cortar

            // 1) Monta o histograma: UMA passada pelas linhas, somando o alvo de cada faixa.
            var sums = new double[binCount];
            var counts = new int[binCount];
            foreach (int r in rows)
            {
                int bin = column[r];
                sums[bin] += y[r];
                counts[bin]++;
            }

            // 2) Varre as faixas da esquerda para a direita (no máximo 254 candidatos).
            //    É a mesma varredura da versão exata, só que sobre faixas em vez de linhas.
            BinSplit? best = null;
            double bestGain = 1e-12;
            double leftSum = 0;
            int leftCount = 0;

            for (int bin = 0; bin < binCount - 1; bin++)
            {
                leftSum += sums[bin];
                leftCount += counts[bin];

                if (counts[bin] == 0)
                    continue; // faixa vazia neste nó: o corte seria igual ao anterior

                int rightCount = n - leftCount;
                if (leftCount < MinSamplesLeaf)
                    continue;
                if (rightCount < MinSamplesLeaf)
                    break;

                double rightSum = totalSum - leftSum;
                double gain = leftSum * leftSum / leftCount
                            + rightSum * rightSum / rightCount
                            - parentScore;

                if (gain > bestGain)
                {
                    bestGain = gain;
                    best = new BinSplit(f, bin, gain);
                }
            }

            bestPerFeature[f] = best;
        });

        BinSplit? winner = null;
        foreach (BinSplit? candidate in bestPerFeature)
        {
            if (candidate is null)
                continue;
            if (winner is null || candidate.Value.Gain > winner.Value.Gain)
                winner = candidate;
        }

        return winner;
    }

    // ------------------------------------------------------------------
    // Visualização
    // ------------------------------------------------------------------

    /// <summary>Devolve a árvore em texto, para conseguirmos "ler" o que ela aprendeu.</summary>
    public string Describe(IReadOnlyList<string>? featureNames = null)
    {
        if (Root is null)
            return "(árvore não treinada)";

        var sb = new StringBuilder();
        DescribeNode(Root, featureNames, indent: "", sb);
        return sb.ToString();
    }

    private static void DescribeNode(TreeNode node, IReadOnlyList<string>? names, string indent, StringBuilder sb)
    {
        if (node.IsLeaf)
        {
            sb.AppendLine($"{indent}=> valor {node.Value:0.####}  ({node.SampleCount:N0} amostras)");
            return;
        }

        string name = names is not null ? names[node.FeatureIndex] : $"feature[{node.FeatureIndex}]";

        sb.AppendLine($"{indent}se {name} <= {node.Threshold:0.####}:");
        DescribeNode(node.Left!, names, indent + "    ", sb);
        sb.AppendLine($"{indent}senão:");
        DescribeNode(node.Right!, names, indent + "    ", sb);
    }
}
