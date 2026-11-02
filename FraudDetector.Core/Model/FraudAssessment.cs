namespace FraudDetector.Core.Model;

/// <summary>
/// A decisão final sobre uma transação. São três, e não duas:
///   Approve — o modelo não viu risco e a transação está dentro do que ele conhece;
///   Review  — o modelo não viu risco, mas a transação tem algo que ele NUNCA viu no treino,
///             então a opinião dele não é confiável: pede-se uma confirmação (ex.: ao titular);
///   Block   — a probabilidade de fraude passou do corte.
/// </summary>
public enum FraudDecision { Approve, Review, Block }

/// <summary>Resultado completo da avaliação: probabilidade, decisão e os motivos em linguagem humana.</summary>
public record FraudAssessment(
    double Probability,
    double Threshold,
    FraudDecision Decision,
    IReadOnlyList<string> Reasons)
{
    /// <summary>Código curto usado pela API e pelas páginas: "aprovada", "revisao" ou "bloqueada".</summary>
    public string DecisionCode => Decision switch
    {
        FraudDecision.Block => "bloqueada",
        FraudDecision.Review => "revisao",
        _ => "aprovada",
    };
}

/// <summary>Menor e maior valor que uma feature teve nos dados de treino.</summary>
public record FeatureRange(double Min, double Max)
{
    public bool Contains(double value) => value >= Min && value <= Max;
}
