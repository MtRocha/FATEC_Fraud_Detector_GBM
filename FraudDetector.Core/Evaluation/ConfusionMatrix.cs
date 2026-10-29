namespace FraudDetector.Core.Evaluation;

/// <summary>
/// Os quatro resultados possíveis de um classificador, contados.
///   TruePositives  = fraude, e o modelo alertou        (acerto)
///   FalsePositives = normal, mas o modelo alertou      (alarme falso)
///   FalseNegatives = fraude, e o modelo deixou passar  (fraude perdida)
///   TrueNegatives  = normal, e o modelo deixou passar  (acerto)
/// </summary>
public readonly record struct ConfusionMatrix(
    int TruePositives, int FalsePositives, int FalseNegatives, int TrueNegatives)
{
    public int Total => TruePositives + FalsePositives + FalseNegatives + TrueNegatives;

    /// <summary>Dos alertas que o modelo deu, quantos eram fraude de verdade?</summary>
    public double Precision =>
        TruePositives + FalsePositives == 0 ? 0 : (double)TruePositives / (TruePositives + FalsePositives);

    /// <summary>Das fraudes que existiam, quantas o modelo pegou?</summary>
    public double Recall =>
        TruePositives + FalseNegatives == 0 ? 0 : (double)TruePositives / (TruePositives + FalseNegatives);

    /// <summary>Média harmônica de precisão e recall: só é alta quando as DUAS são altas.</summary>
    public double F1 =>
        Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    /// <summary>Proporção de acertos no total. Enganosa em fraude: "nunca é fraude" já dá mais de 99%.</summary>
    public double Accuracy => Total == 0 ? 0 : (double)(TruePositives + TrueNegatives) / Total;
}
