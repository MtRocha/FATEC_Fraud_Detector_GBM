using System.Diagnostics;
using FraudDetector;
using FraudDetector.Core.Data;
using FraudDetector.Core.Evaluation;
using FraudDetector.Core.Features;
using FraudDetector.Core.Model;

Console.WriteLine("=== FraudDetector ===");
Console.WriteLine("1 - Inspecionar a base (leitura do CSV + features)");
Console.WriteLine("2 - Árvore: exemplo feito à mão (6 transações)");
Console.WriteLine("3 - Árvore: dados reais (amostra de 200 mil transações)");
Console.WriteLine("4 - Boosting: exemplo feito à mão (6 transações)");
Console.WriteLine("5 - Boosting: dados reais (treina em 200 mil, valida em 100 mil)");
Console.WriteLine("6 - TREINAR O MODELO FINAL (base inteira) e salvar");
Console.Write("Escolha: ");

switch (Console.ReadLine()?.Trim())
{
    case "1": InspectData(); break;
    case "2": TreeToyExample(); break;
    case "3": TreeOnRealData(); break;
    case "4": BoostingToyExample(); break;
    case "5": BoostingOnRealData(); break;
    case "6": TrainCommand.Run(); break;
    default: Console.WriteLine("Opção inválida."); break;
}

// ----------------------------------------------------------------------
// 1 - Lê o CSV inteiro e mostra um resumo
// ----------------------------------------------------------------------
static void InspectData()
{
    string path = FindDataFile("fraudTrain.csv");
    Console.WriteLine($"Lendo {path} ...");
    var stopwatch = Stopwatch.StartNew();

    int total = 0, frauds = 0;
    double fraudAmount = 0, normalAmount = 0;

    foreach (LabeledTransaction item in TransactionCsvReader.Read(path))
    {
        // Mostra as features da primeira transação, para conferirmos a transformação.
        if (total == 0)
        {
            double[] features = FeatureExtractor.Extract(item.Transaction);
            Console.WriteLine("Features da primeira transação:");
            for (int i = 0; i < features.Length; i++)
                Console.WriteLine($"  [{i,2}] {FeatureExtractor.FeatureNames[i],-20} = {features[i]:0.####}");
            Console.WriteLine();
        }

        total++;
        if (item.IsFraud)
        {
            frauds++;
            fraudAmount += item.Transaction.Amount;
        }
        else
        {
            normalAmount += item.Transaction.Amount;
        }
    }

    stopwatch.Stop();

    Console.WriteLine($"Transações:        {total:N0}");
    Console.WriteLine($"Fraudes:           {frauds:N0} ({100.0 * frauds / total:F2}%)");
    Console.WriteLine($"Valor médio fraude: {fraudAmount / frauds:F2}");
    Console.WriteLine($"Valor médio normal: {normalAmount / (total - frauds):F2}");
    Console.WriteLine($"Tempo de leitura:  {stopwatch.Elapsed.TotalSeconds:F1}s");
}

// ----------------------------------------------------------------------
// 2 - O mesmo exemplo que fizemos à mão: 6 transações, 1 feature (valor)
// ----------------------------------------------------------------------
static void TreeToyExample()
{
    double[][] x = [[20], [45], [60], [300], [450], [900]];
    double[] y = [0, 0, 0, 1, 0, 1];

    var tree = new RegressionTree(maxDepth: 1);
    tree.Fit(x, y);

    Console.WriteLine();
    Console.WriteLine(tree.Describe(["valor"]));
    Console.WriteLine($"Previsão para valor 50:  {tree.Predict([50]):0.####}");
    Console.WriteLine($"Previsão para valor 700: {tree.Predict([700]):0.####}");
}

// ----------------------------------------------------------------------
// 3 - Uma única árvore tentando prever fraude (alvo 0/1) em dados reais
// ----------------------------------------------------------------------
static void TreeOnRealData()
{
    const int sampleSize = 200_000;
    string path = FindDataFile("fraudTrain.csv");
    Console.WriteLine($"Lendo as primeiras {sampleSize:N0} transações de {path} ...");

    var features = new List<double[]>(sampleSize);
    var targets = new List<double>(sampleSize);

    foreach (LabeledTransaction item in TransactionCsvReader.Read(path).Take(sampleSize))
    {
        features.Add(FeatureExtractor.Extract(item.Transaction));
        targets.Add(item.IsFraud ? 1 : 0);
    }

    var stopwatch = Stopwatch.StartNew();
    var tree = new RegressionTree(maxDepth: 3, minSamplesLeaf: 50);
    tree.Fit(features.ToArray(), targets.ToArray());
    stopwatch.Stop();

    Console.WriteLine($"Árvore treinada em {stopwatch.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine("(o valor de cada folha é a proporção de fraudes entre as amostras dela)");
    Console.WriteLine();
    Console.WriteLine(tree.Describe(FeatureExtractor.FeatureNames));
}

// ----------------------------------------------------------------------
// 4 - Uma única rodada de boosting no exemplo feito à mão
// ----------------------------------------------------------------------
static void BoostingToyExample()
{
    double[][] x = [[20], [45], [60], [300], [450], [900]];
    double[] y = [0, 0, 0, 1, 0, 1];

    var model = new GradientBoostingClassifier(treeCount: 1, learningRate: 0.5, maxDepth: 1, minSamplesLeaf: 1,
        l2Regularization: 0); // sem regularização, para bater com a conta feita à mão
    model.Fit(x, y);

    Console.WriteLine();
    Console.WriteLine($"Score inicial: {model.InitialScore:0.####} " +
                      $"(probabilidade {GradientBoostingClassifier.Sigmoid(model.InitialScore):0.####})");
    Console.WriteLine();
    Console.WriteLine("Árvore 1 (valores das folhas já em unidades de score):");
    Console.WriteLine(model.Trees[0].Describe(["valor"]));
    Console.WriteLine($"Probabilidade para valor 50:  {model.PredictProbability([50]):0.####}");
    Console.WriteLine($"Probabilidade para valor 700: {model.PredictProbability([700]):0.####}");
}

// ----------------------------------------------------------------------
// 5 - Boosting em dados reais, com validação em dados que o modelo não viu
// ----------------------------------------------------------------------
static void BoostingOnRealData()
{
    const int trainSize = 200_000;
    const int validSize = 100_000;

    string path = FindDataFile("fraudTrain.csv");
    Console.WriteLine($"Lendo {trainSize + validSize:N0} transações de {path} ...");

    var features = new List<double[]>(trainSize + validSize);
    var targets = new List<double>(trainSize + validSize);
    foreach (LabeledTransaction item in TransactionCsvReader.Read(path).Take(trainSize + validSize))
    {
        features.Add(FeatureExtractor.Extract(item.Transaction));
        targets.Add(item.IsFraud ? 1 : 0);
    }

    // As primeiras 200 mil treinam o modelo; as 100 mil seguintes ficam escondidas dele
    // e servem para medir se ele aprendeu de verdade ou só decorou o treino.
    double[][] trainX = features.Take(trainSize).ToArray();
    double[] trainY = targets.Take(trainSize).ToArray();
    double[][] validX = features.Skip(trainSize).ToArray();
    double[] validY = targets.Skip(trainSize).ToArray();

    var model = new GradientBoostingClassifier(treeCount: 50, learningRate: 0.2, maxDepth: 3, minSamplesLeaf: 50);

    Console.WriteLine("Treinando 50 árvores (pode levar alguns minutos)...");
    Console.WriteLine();
    Console.WriteLine("Rodada | Log-loss treino | Log-loss validação");

    var stopwatch = Stopwatch.StartNew();
    model.Fit(trainX, trainY, (round, trainLoss) =>
    {
        if (round != 1 && round % 5 != 0)
            return true; // true = continuar treinando

        double validLoss = 0;
        for (int i = 0; i < validX.Length; i++)
            validLoss += GradientBoostingClassifier.LogLoss(validY[i], model.PredictProbability(validX[i]));
        validLoss /= validX.Length;

        Console.WriteLine($"{round,6} | {trainLoss,15:0.00000} | {validLoss,18:0.00000}");
        return true;
    });
    stopwatch.Stop();

    Console.WriteLine();
    Console.WriteLine($"Treino concluído em {stopwatch.Elapsed.TotalSeconds:F0}s");

    // Probabilidade de fraude de cada transação da validação (calculada uma única vez).
    double[] probabilities = validX.Select(model.PredictProbability).ToArray();

    Console.WriteLine();
    Console.WriteLine($"Validação: {validX.Length:N0} transações, {validY.Count(v => v == 1):N0} fraudes");
    Console.WriteLine();
    Console.WriteLine("Corte | Detectadas | Perdidas | Alarmes falsos | Precisão | Recall |    F1");

    foreach (double threshold in new[] { 0.05, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 })
    {
        ConfusionMatrix m = ClassificationMetrics.AtThreshold(probabilities, validY, threshold);
        Console.WriteLine(
            $"{threshold,5:0.00} | {m.TruePositives,10:N0} | {m.FalseNegatives,8:N0} | {m.FalsePositives,14:N0} | " +
            $"{m.Precision,8:P1} | {m.Recall,6:P1} | {m.F1,5:0.000}");
    }

    ConfusionMatrix atHalf = ClassificationMetrics.AtThreshold(probabilities, validY, 0.5);
    Console.WriteLine();
    Console.WriteLine($"Acurácia no corte 0,50: {atHalf.Accuracy:P2}");
    Console.WriteLine($"Acurácia de um modelo que sempre diz 'normal': {1 - validY.Average():P2}");
    Console.WriteLine($"AUC-PR: {ClassificationMetrics.AveragePrecision(probabilities, validY):0.0000}");
}

static string FindDataFile(string fileName) => DataFiles.Find(fileName);
