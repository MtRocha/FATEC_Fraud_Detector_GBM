using System.Text.Json.Serialization;

namespace FraudDetector.Core.Model;

/// <summary>
/// Um nó da árvore. Pode ser de dois tipos:
///  - nó de decisão: tem uma pergunta ("feature &lt;= threshold?") e dois filhos;
///  - folha: não tem filhos e guarda o valor previsto.
/// </summary>
public class TreeNode
{
    /// <summary>Índice da feature usada na pergunta (-1 em folhas).</summary>
    public int FeatureIndex { get; set; } = -1;

    /// <summary>Valor de corte: quem tem feature &lt;= Threshold vai para a esquerda.</summary>
    public double Threshold { get; set; }

    public TreeNode? Left { get; set; }
    public TreeNode? Right { get; set; }

    /// <summary>Média do alvo das amostras que chegaram a este nó (é a previsão, se for folha).</summary>
    public double Value { get; set; }

    /// <summary>Quantas amostras de treino chegaram a este nó.</summary>
    public int SampleCount { get; set; }

    [JsonIgnore] // calculado a partir dos filhos: não precisa ir para o arquivo
    public bool IsLeaf => Left is null || Right is null;
}
