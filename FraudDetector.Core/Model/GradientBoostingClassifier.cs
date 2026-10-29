namespace FraudDetector.Core.Model;

/// <summary>
/// Gradient boosting para classificação binária (fraude / não fraude).
///
/// O modelo é uma SOMA:  score = scoreInicial + taxa × árvore1 + taxa × árvore2 + ...
/// O score é um número qualquer (de −∞ a +∞); a sigmoide o converte em probabilidade (0 a 1).
/// Cada árvore nova é treinada para prever o ERRO que a soma das anteriores ainda comete.
/// </summary>
public class GradientBoostingClassifier
{
    /// <summary>Quantas árvores (rodadas de correção) treinar.</summary>
    public int TreeCount { get; }

    /// <summary>Taxa de aprendizado: fração da correção de cada árvore que realmente aplicamos.</summary>
    public double LearningRate { get; }

    public int MaxDepth { get; }
    public int MinSamplesLeaf { get; }

    /// <summary>
    /// Regularização L2: número somado ao divisor do valor da folha.
    /// Segura folhas cujo divisor é quase zero (evita valores gigantes).
    /// </summary>
    public double L2Regularization { get; }

    /// <summary>
    /// Limite do valor de uma folha, em módulo. Nenhuma árvore sozinha pode
    /// mexer no score mais do que LearningRate × MaxLeafValue.
    /// </summary>
    public double MaxLeafValue { get; }

    /// <summary>Palpite inicial, igual para todas as transações (taxa geral de fraude, em score).</summary>
    public double InitialScore { get; private set; }

    public List<RegressionTree> Trees { get; } = [];

    public GradientBoostingClassifier(
        int treeCount = 100, double learningRate = 0.1, int maxDepth = 3, int minSamplesLeaf = 50,
        double l2Regularization = 1.0, double maxLeafValue = 5.0)
    {
        TreeCount = treeCount;
        LearningRate = learningRate;
        MaxDepth = maxDepth;
        MinSamplesLeaf = minSamplesLeaf;
        L2Regularization = l2Regularization;
        MaxLeafValue = maxLeafValue;
    }

    /// <summary>
    /// Treina o modelo. y[i] é 1 (fraude) ou 0 (normal).
    /// onRound, se informado, é chamado ao fim de cada rodada com (número da rodada, log-loss de treino).
    /// Se ele devolver false, o treino para ali (parada antecipada).
    /// </summary>
    public void Fit(double[][] x, double[] y, Func<int, double, bool>? onRound = null)
    {
        int n = x.Length;
        if (n == 0 || n != y.Length)
            throw new ArgumentException("x e y precisam ter o mesmo tamanho (e não podem ser vazios).");

        Trees.Clear();

        // Agrupa cada feature em faixas, UMA única vez. Todas as árvores reutilizam o resultado.
        BinnedData binned = FeatureBinner.Bin(x);

        // PASSO 0 — palpite inicial: a taxa geral de fraude, convertida para score (log-odds).
        double fraudRate = Math.Clamp(y.Average(), 1e-6, 1 - 1e-6);
        InitialScore = Math.Log(fraudRate / (1 - fraudRate));

        // Score atual de cada transação. Começa igual para todas e vai sendo corrigido.
        var scores = new double[n];
        Array.Fill(scores, InitialScore);

        var residuals = new double[n];
        var leafOfRow = new TreeNode[n];

        for (int round = 1; round <= TreeCount; round++)
        {
            // PASSO 1 — erro atual de cada transação: resposta certa − probabilidade prevista.
            //   fraude prevista com 10%  → resíduo +0,90 (precisa subir muito)
            //   normal prevista com 10%  → resíduo −0,10 (precisa descer um pouco)
            for (int i = 0; i < n; i++)
                residuals[i] = y[i] - Sigmoid(scores[i]);

            // PASSO 2 — treina uma árvore cujo ALVO é o erro (não mais o 0/1).
            var tree = new RegressionTree(MaxDepth, MinSamplesLeaf);
            tree.Fit(binned, residuals);

            // PASSO 3 — recalcula o valor de cada folha.
            // A árvore guardou a média do erro, que está em "unidades de probabilidade".
            // Mas a soma do modelo é feita em "unidades de score". A conversão é:
            //     valor da folha = soma(erros) / (soma(p × (1 − p)) + L2)
            // Essa conversão é uma APROXIMAÇÃO que só vale para passos pequenos. Quando
            // p está muito perto de 0 ou de 1, o divisor fica minúsculo e o valor explode.
            // Por isso somamos L2 ao divisor e limitamos o resultado a ±MaxLeafValue.
            var errorSum = new Dictionary<TreeNode, double>();
            var weightSum = new Dictionary<TreeNode, double>();

            for (int i = 0; i < n; i++)
            {
                TreeNode leaf = tree.FindLeaf(x[i]);
                leafOfRow[i] = leaf;

                double p = y[i] - residuals[i]; // a probabilidade que gerou este resíduo
                errorSum[leaf] = errorSum.GetValueOrDefault(leaf) + residuals[i];
                weightSum[leaf] = weightSum.GetValueOrDefault(leaf) + p * (1 - p);
            }

            foreach (TreeNode leaf in errorSum.Keys)
            {
                double value = errorSum[leaf] / (weightSum[leaf] + L2Regularization);
                leaf.Value = Math.Clamp(value, -MaxLeafValue, MaxLeafValue);
            }

            // PASSO 4 — aplica a correção, multiplicada pela taxa de aprendizado.
            for (int i = 0; i < n; i++)
                scores[i] += LearningRate * leafOfRow[i].Value;

            Trees.Add(tree);

            if (onRound is not null)
            {
                double loss = 0;
                for (int i = 0; i < n; i++)
                    loss += LogLoss(y[i], Sigmoid(scores[i]));
                if (!onRound(round, loss / n))
                    break;
            }
        }
    }

    /// <summary>Score bruto: palpite inicial + soma das correções de todas as árvores.</summary>
    public double PredictScore(double[] features)
    {
        double score = InitialScore;
        foreach (RegressionTree tree in Trees)
            score += LearningRate * tree.Predict(features);
        return score;
    }

    /// <summary>Probabilidade de fraude, entre 0 e 1.</summary>
    public double PredictProbability(double[] features) => Sigmoid(PredictScore(features));

    /// <summary>Converte um score qualquer em probabilidade: 0 → 50%, positivo → acima, negativo → abaixo.</summary>
    public static double Sigmoid(double score) => 1.0 / (1.0 + Math.Exp(-score));

    /// <summary>
    /// Log-loss de uma previsão: penaliza pouco quem erra por pouco e MUITO quem erra com convicção.
    /// É a medida de erro que o boosting está, na prática, minimizando.
    /// </summary>
    public static double LogLoss(double y, double probability)
    {
        double p = Math.Clamp(probability, 1e-15, 1 - 1e-15);
        return -(y * Math.Log(p) + (1 - y) * Math.Log(1 - p));
    }
}
