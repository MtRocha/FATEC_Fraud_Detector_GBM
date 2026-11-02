using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FraudDetector.Core.Data;
using FraudDetector.Core.Features;

namespace FraudDetector.Core.Model;

/// <summary>
/// O modelo pronto para uso: tudo (e somente) o que é preciso para PREVER.
/// É o que gravamos em JSON no fim do treino e o que a API carrega.
///
/// Repare no que NÃO está aqui: dados de treino, faixas dos histogramas, resíduos.
/// Prever é só: palpite inicial + soma das árvores → sigmoide → comparar com o corte.
/// </summary>
public class FraudModel
{
    /// <summary>Versão do formato do arquivo, para detectar arquivos antigos no futuro.</summary>
    public int FormatVersion { get; set; } = 2;

    public DateTime TrainedAtUtc { get; set; }

    /// <summary>
    /// Nomes das features, na ordem usada no treino. Ao carregar, conferimos se batem
    /// com o FeatureExtractor atual: se a ordem mudou, o modelo não serve mais.
    /// </summary>
    public List<string> FeatureNames { get; set; } = [];

    public double InitialScore { get; set; }
    public double LearningRate { get; set; }

    /// <summary>Corte: probabilidade a partir da qual a transação é tratada como fraude.</summary>
    public double Threshold { get; set; } = 0.5;

    /// <summary>A raiz de cada árvore, na ordem em que foram treinadas.</summary>
    public List<TreeNode> Trees { get; set; } = [];

    /// <summary>
    /// Menor e maior valor de cada feature nos dados de treino (mesma ordem de FeatureNames).
    /// É o "território conhecido" do modelo: fora dele, as árvores não têm base para opinar.
    /// Fica nulo em modelos antigos, salvos antes desta proteção existir.
    /// </summary>
    public List<FeatureRange>? FeatureRanges { get; set; }

    public static FraudModel FromTrained(
        GradientBoostingClassifier classifier, double threshold, List<FeatureRange> featureRanges) => new()
    {
        TrainedAtUtc = DateTime.UtcNow,
        FeatureNames = [.. FeatureExtractor.FeatureNames],
        InitialScore = classifier.InitialScore,
        LearningRate = classifier.LearningRate,
        Threshold = threshold,
        Trees = classifier.Trees.Select(tree => tree.Root!).ToList(),
        FeatureRanges = featureRanges,
    };

    /// <summary>Calcula o mínimo e o máximo de cada coluna dos dados de treino.</summary>
    public static List<FeatureRange> ComputeRanges(double[][] x)
    {
        int featureCount = x[0].Length;
        var min = new double[featureCount];
        var max = new double[featureCount];
        Array.Fill(min, double.MaxValue);
        Array.Fill(max, double.MinValue);

        foreach (double[] row in x)
        {
            for (int f = 0; f < featureCount; f++)
            {
                if (row[f] < min[f]) min[f] = row[f];
                if (row[f] > max[f]) max[f] = row[f];
            }
        }

        return Enumerable.Range(0, featureCount).Select(f => new FeatureRange(min[f], max[f])).ToList();
    }

    /// <summary>Probabilidade de fraude a partir do vetor de features já calculado.</summary>
    public double PredictProbability(double[] features)
    {
        double score = InitialScore;

        foreach (TreeNode root in Trees)
        {
            TreeNode node = root;
            while (!node.IsLeaf)
                node = features[node.FeatureIndex] <= node.Threshold ? node.Left! : node.Right!;

            score += LearningRate * node.Value;
        }

        return GradientBoostingClassifier.Sigmoid(score);
    }

    /// <summary>Probabilidade de fraude de uma transação crua (é o que a API vai chamar).</summary>
    public double PredictProbability(Transaction transaction) =>
        PredictProbability(FeatureExtractor.Extract(transaction));

    public bool IsFraud(Transaction transaction) => Assess(transaction).Decision == FraudDecision.Block;

    // ------------------------------------------------------------------
    // Avaliação completa: modelo + proteção do "território conhecido"
    // ------------------------------------------------------------------

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Avalia uma transação crua e devolve probabilidade, decisão e motivos.</summary>
    public FraudAssessment Assess(Transaction transaction, double? threshold = null) =>
        Assess(FeatureExtractor.Extract(transaction), threshold);

    public FraudAssessment Assess(double[] features, double? threshold = null)
    {
        double cut = threshold ?? Threshold;
        double probability = PredictProbability(features);
        var reasons = new List<string>();

        if (probability >= cut)
            reasons.Add($"Probabilidade de fraude de {probability.ToString("P1", PtBr)}, acima do corte de {cut.ToString("P0", PtBr)}.");

        // A árvore só sabe perguntar "é maior que X?". Um valor nunca visto no treino cai
        // na última faixa conhecida e é tratado como se fosse o maior valor que ela viu.
        // Por isso, fora do território conhecido, a probabilidade do modelo não é confiável.
        List<string> unknown = OutsideKnownTerritory(features);
        reasons.AddRange(unknown);

        FraudDecision decision =
            probability >= cut ? FraudDecision.Block :
            unknown.Count > 0 ? FraudDecision.Review :
            FraudDecision.Approve;

        return new FraudAssessment(probability, cut, decision, reasons);
    }

    /// <summary>
    /// Lista o que a transação tem de diferente de tudo que o modelo viu no treino.
    /// Só olhamos as features contínuas; hora, dia da semana, gênero e categoria têm
    /// valores fixos que sempre aparecem no treino.
    /// </summary>
    public List<string> OutsideKnownTerritory(double[] features)
    {
        var reasons = new List<string>();
        List<FeatureRange>? ranges = FeatureRanges;
        if (ranges is null)
            return reasons; // modelo antigo, salvo sem as faixas

        string Money(double v) => "US$ " + v.ToString("N2", PtBr);
        string Km(double v) => v.ToString("N0", PtBr) + " km";

        void Check(string name, Func<double, FeatureRange, string> message)
        {
            int i = FeatureNames.IndexOf(name);
            if (i < 0 || i >= ranges.Count) return;
            if (!ranges[i].Contains(features[i]))
                reasons.Add(message(features[i], ranges[i]));
        }

        Check("amount", (v, r) => v > r.Max
            ? $"Valor de {Money(v)} acima do maior visto no treino ({Money(r.Max)})."
            : $"Valor de {Money(v)} abaixo do menor visto no treino ({Money(r.Min)}).");

        Check("age", (v, r) =>
            $"Titular com {v:0} anos, fora da faixa vista no treino ({r.Min:0} a {r.Max:0} anos).");

        // Em compras online, o "local do comércio" não diz onde o cartão está: a distância não vale.
        bool online = FeatureNames
            .Select((name, i) => (name, i))
            .Any(x => x.name.StartsWith("cat_") && x.name.EndsWith("_net") && features[x.i] == 1);

        if (!online)
            Check("distance_km", (v, r) =>
                $"Compra presencial a {Km(v)} da casa do titular; no treino, a maior distância foi de {Km(r.Max)}.");

        return reasons;
    }

    // ------------------------------------------------------------------
    // Gravação e leitura
    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Folhas não têm filhos: não gravamos "Left": null / "Right": null.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static FraudModel Load(string path)
    {
        FraudModel model = JsonSerializer.Deserialize<FraudModel>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Arquivo de modelo inválido: {path}");

        if (!model.FeatureNames.SequenceEqual(FeatureExtractor.FeatureNames))
            throw new InvalidDataException(
                "As features do modelo salvo não batem com as do FeatureExtractor atual. Treine o modelo de novo.");

        return model;
    }
}
