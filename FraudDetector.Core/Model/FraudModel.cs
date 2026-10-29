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
    public int FormatVersion { get; set; } = 1;

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

    public static FraudModel FromTrained(GradientBoostingClassifier classifier, double threshold) => new()
    {
        TrainedAtUtc = DateTime.UtcNow,
        FeatureNames = [.. FeatureExtractor.FeatureNames],
        InitialScore = classifier.InitialScore,
        LearningRate = classifier.LearningRate,
        Threshold = threshold,
        Trees = classifier.Trees.Select(tree => tree.Root!).ToList(),
    };

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

    public bool IsFraud(Transaction transaction) => PredictProbability(transaction) >= Threshold;

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
