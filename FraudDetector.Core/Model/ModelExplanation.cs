namespace FraudDetector.Core.Model;

/// <summary>Uma pergunta feita por uma árvore e a resposta para esta transação.</summary>
public record PathStep(string Feature, double Threshold, double Value, bool GoesLeft); // GoesLeft = "sim, é menor ou igual"

/// <summary>O caminho de uma transação dentro de uma árvore, até a folha.</summary>
public record TreeTrace(int Index, IReadOnlyList<PathStep> Path, double LeafValue, double Contribution);

/// <summary>Uma verificação do território conhecido.</summary>
public record TerritoryCheck(string Feature, double Value, double Min, double Max, bool Inside, bool Applies);

/// <summary>
/// Tudo o que é preciso para contar, passo a passo, como o modelo chegou à decisão:
/// as features, o ponto de partida, a contribuição de cada árvore, a soma, a sigmoide
/// e o território conhecido.
/// </summary>
public record ModelExplanation(
    IReadOnlyList<string> FeatureNames,
    double[] Features,
    double InitialScore,
    double LearningRate,
    double[] Contributions,           // quanto cada árvore somou ao score, na ordem do treino
    double FinalScore,
    double Probability,
    double Threshold,
    double ThresholdScore,            // o corte convertido para a escala do score
    TreeNode FirstTree,               // a árvore 1 inteira, para desenhar
    TreeTrace FirstTreeTrace,         // o caminho desta transação na árvore 1
    IReadOnlyList<TreeTrace> TopTrees, // as 5 árvores que mais mexeram no score
    IReadOnlyList<TerritoryCheck> Territory,
    FraudAssessment Assessment);
