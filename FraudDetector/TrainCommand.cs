using System.Diagnostics;
using System.Globalization;
using FraudDetector.Core.Data;
using FraudDetector.Core.Evaluation;
using FraudDetector.Core.Features;
using FraudDetector.Core.Model;

namespace FraudDetector;

/// <summary>
/// Treina o modelo final na base inteira, escolhe o corte, avalia no teste e salva em JSON.
/// </summary>
public static class TrainCommand
{
    // Hiperparâmetros: os "botões" do treino. Vale experimentar outros valores depois.
    private const int MaxTrees = 500;            // teto de árvores (a parada antecipada costuma parar antes)
    private const double LearningRate = 0.1;
    private const int MaxDepth = 4;
    private const int MinSamplesLeaf = 50;
    private const int Patience = 30;             // rodadas sem melhora na validação antes de parar
    private const double ValidationFraction = 0.2;

    public static void Run()
    {
        // --------------------------------------------------------------
        // 1) Dados: treino e validação saem do fraudTrain.csv
        // --------------------------------------------------------------
        var (x, y) = Load(DataFiles.Find("fraudTrain.csv"));

        // O arquivo está em ordem de data. Treinamos nos primeiros 80% e validamos nos 20% finais:
        // o modelo aprende com o passado e é medido no futuro, como acontecerá em produção.
        int trainCount = (int)(x.Length * (1 - ValidationFraction));
        double[][] trainX = x[..trainCount];
        double[] trainY = y[..trainCount];

        // Território conhecido: o menor e o maior valor de cada feature no treino.
        // Vai junto com o modelo para a API recusar decisões automáticas fora dele.
        List<FeatureRange> ranges = FraudModel.ComputeRanges(x[..trainCount]);
        double[][] validX = x[trainCount..];
        double[] validY = y[trainCount..];

        Console.WriteLine($"Treino:    {trainX.Length:N0} transações, {trainY.Count(v => v == 1):N0} fraudes");
        Console.WriteLine($"Validação: {validX.Length:N0} transações, {validY.Count(v => v == 1):N0} fraudes");
        Console.WriteLine();

        // --------------------------------------------------------------
        // 2) Treino com parada antecipada (early stopping)
        // --------------------------------------------------------------
        var classifier = new GradientBoostingClassifier(MaxTrees, LearningRate, MaxDepth, MinSamplesLeaf);

        // Score de cada transação da validação, atualizado a cada árvore nova
        // (bem mais barato do que recalcular todas as árvores a cada rodada).
        var validScores = new double[validX.Length];
        double bestLoss = double.MaxValue;
        int bestRound = 0;

        Console.WriteLine($"Treinando até {MaxTrees} árvores (para se a validação ficar {Patience} rodadas sem melhorar)");
        Console.WriteLine("Rodada | Log-loss treino | Log-loss validação");

        var stopwatch = Stopwatch.StartNew();
        classifier.Fit(trainX, trainY, (round, trainLoss) =>
        {
            if (round == 1)
                Array.Fill(validScores, classifier.InitialScore);

            RegressionTree newTree = classifier.Trees[^1];
            double validLoss = 0;
            for (int i = 0; i < validX.Length; i++)
            {
                validScores[i] += classifier.LearningRate * newTree.Predict(validX[i]);
                validLoss += GradientBoostingClassifier.LogLoss(
                    validY[i], GradientBoostingClassifier.Sigmoid(validScores[i]));
            }
            validLoss /= validX.Length;

            if (validLoss < bestLoss)
            {
                bestLoss = validLoss;
                bestRound = round;
            }

            if (round == 1 || round % 10 == 0)
                Console.WriteLine($"{round,6} | {trainLoss,15:0.00000} | {validLoss,18:0.00000}");

            // Devolver false interrompe o treino.
            return round - bestRound < Patience;
        });
        stopwatch.Stop();

        // As árvores treinadas depois da melhor rodada não ajudaram na validação: descartamos.
        int trained = classifier.Trees.Count;
        if (trained > bestRound)
            classifier.Trees.RemoveRange(bestRound, trained - bestRound);

        Console.WriteLine();
        Console.WriteLine($"Treinadas {trained} árvores em {stopwatch.Elapsed.TotalSeconds:F0}s.");
        Console.WriteLine($"Melhor rodada na validação: {bestRound} (log-loss {bestLoss:0.00000}). " +
                          $"Modelo final: {classifier.Trees.Count} árvores.");

        // --------------------------------------------------------------
        // 3) Escolha do corte, usando SÓ a validação
        // --------------------------------------------------------------
        double[] validProbabilities = PredictAll(classifier, validX);

        Console.WriteLine();
        Console.WriteLine("Validação, por corte:");
        double bestThreshold = PrintThresholdTable(validProbabilities, validY);
        Console.WriteLine($"AUC-PR na validação: {ClassificationMetrics.AveragePrecision(validProbabilities, validY):0.0000}");

        Console.WriteLine();
        Console.Write($"Corte a usar (Enter = {bestThreshold:0.00}, o de melhor F1): ");
        double threshold = ReadThreshold(Console.ReadLine(), bestThreshold);
        Console.WriteLine($"Corte escolhido: {threshold:0.00}");

        // --------------------------------------------------------------
        // 4) Resultado final no fraudTest.csv, que o modelo nunca viu
        // --------------------------------------------------------------
        Console.WriteLine();
        var (testX, testY) = Load(DataFiles.Find("fraudTest.csv"));
        double[] testProbabilities = PredictAll(classifier, testX);
        ConfusionMatrix m = ClassificationMetrics.AtThreshold(testProbabilities, testY, threshold);

        Console.WriteLine();
        Console.WriteLine($"=== RESULTADO FINAL (teste, corte {threshold:0.00}) ===");
        Console.WriteLine($"Fraudes detectadas:   {m.TruePositives:N0} de {m.TruePositives + m.FalseNegatives:N0}");
        Console.WriteLine($"Fraudes que passaram: {m.FalseNegatives:N0}");
        Console.WriteLine($"Alarmes falsos:       {m.FalsePositives:N0}");
        Console.WriteLine($"Precisão: {m.Precision:P1}   Recall: {m.Recall:P1}   F1: {m.F1:0.000}");
        Console.WriteLine($"AUC-PR:   {ClassificationMetrics.AveragePrecision(testProbabilities, testY):0.0000}");

        // --------------------------------------------------------------
        // 5) Salvar e conferir
        // --------------------------------------------------------------
        string path = DataFiles.ModelPath();
        FraudModel finalModel = FraudModel.FromTrained(classifier, threshold, ranges);

        // Quantas transações do teste cairiam fora do território conhecido (iriam para revisão)?
        Console.WriteLine();
        Console.WriteLine("Território conhecido (valores vistos no treino):");
        foreach (string name in new[] { "amount", "age", "distance_km" })
        {
            int i = FeatureExtractor.FeatureNames.ToList().IndexOf(name);
            Console.WriteLine($"  {name,-12} de {ranges[i].Min:N2} a {ranges[i].Max:N2}");
        }
        int outside = 0, outsideFrauds = 0;
        for (int i = 0; i < testX.Length; i++)
        {
            if (finalModel.OutsideKnownTerritory(testX[i]).Count == 0) continue;
            outside++;
            if (testY[i] == 1) outsideFrauds++;
        }
        Console.WriteLine($"No teste, {outside:N0} transações ficariam fora do território conhecido ({outsideFrauds:N0} fraudes).");

        finalModel.Save(path);

        // Recarrega o arquivo e confere se ele reproduz as mesmas previsões.
        FraudModel loaded = FraudModel.Load(path);
        double maxDifference = 0;
        for (int i = 0; i < Math.Min(1000, testX.Length); i++)
            maxDifference = Math.Max(maxDifference, Math.Abs(loaded.PredictProbability(testX[i]) - testProbabilities[i]));

        Console.WriteLine();
        Console.WriteLine($"Modelo salvo em {path} ({new FileInfo(path).Length / 1024:N0} KB)");
        Console.WriteLine(maxDifference < 1e-9
            ? "Conferência: o modelo recarregado reproduz as mesmas previsões."
            : $"ATENÇÃO: o modelo recarregado difere do treinado (diferença máxima {maxDifference:E2}).");
    }

    private static (double[][] X, double[] Y) Load(string path)
    {
        Console.WriteLine($"Lendo {Path.GetFileName(path)} ...");
        var stopwatch = Stopwatch.StartNew();

        var features = new List<double[]>();
        var targets = new List<double>();
        foreach (LabeledTransaction item in TransactionCsvReader.Read(path))
        {
            features.Add(FeatureExtractor.Extract(item.Transaction));
            targets.Add(item.IsFraud ? 1 : 0);
        }

        Console.WriteLine($"  {features.Count:N0} transações lidas em {stopwatch.Elapsed.TotalSeconds:F0}s");
        return (features.ToArray(), targets.ToArray());
    }

    /// <summary>Probabilidade de cada linha, usando todos os núcleos do processador.</summary>
    private static double[] PredictAll(GradientBoostingClassifier classifier, double[][] x)
    {
        var probabilities = new double[x.Length];
        Parallel.For(0, x.Length, i => probabilities[i] = classifier.PredictProbability(x[i]));
        return probabilities;
    }

    /// <summary>Imprime as métricas de cada corte e devolve o de maior F1.</summary>
    private static double PrintThresholdTable(double[] probabilities, double[] y)
    {
        Console.WriteLine("Corte | Detectadas | Perdidas | Alarmes falsos | Precisão | Recall |    F1");

        double bestThreshold = 0.5, bestF1 = -1;
        for (int step = 1; step <= 18; step++)
        {
            double threshold = step * 0.05; // 0,05 a 0,90
            ConfusionMatrix m = ClassificationMetrics.AtThreshold(probabilities, y, threshold);

            Console.WriteLine(
                $"{threshold,5:0.00} | {m.TruePositives,10:N0} | {m.FalseNegatives,8:N0} | {m.FalsePositives,14:N0} | " +
                $"{m.Precision,8:P1} | {m.Recall,6:P1} | {m.F1,5:0.000}");

            if (m.F1 > bestF1)
            {
                bestF1 = m.F1;
                bestThreshold = threshold;
            }
        }

        return bestThreshold;
    }

    /// <summary>Lê o corte digitado (aceita 0,3 ou 0.3). Entrada vazia ou inválida usa o padrão.</summary>
    private static double ReadThreshold(string? input, double fallback)
    {
        string text = (input ?? "").Trim().Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            && value > 0 && value < 1)
            return value;

        return fallback;
    }
}
