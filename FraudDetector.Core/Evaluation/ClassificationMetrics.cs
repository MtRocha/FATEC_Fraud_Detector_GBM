namespace FraudDetector.Core.Evaluation;

/// <summary>Métricas para avaliar as probabilidades que o modelo gera.</summary>
public static class ClassificationMetrics
{
    /// <summary>
    /// Conta acertos e erros para um corte (threshold):
    /// probabilidade >= corte vira "fraude", abaixo vira "normal".
    /// </summary>
    public static ConfusionMatrix AtThreshold(double[] probabilities, double[] y, double threshold)
    {
        int tp = 0, fp = 0, fn = 0, tn = 0;

        for (int i = 0; i < y.Length; i++)
        {
            bool predictedFraud = probabilities[i] >= threshold;
            bool isFraud = y[i] == 1;

            if (predictedFraud && isFraud) tp++;
            else if (predictedFraud && !isFraud) fp++;
            else if (!predictedFraud && isFraud) fn++;
            else tn++;
        }

        return new ConfusionMatrix(tp, fp, fn, tn);
    }

    /// <summary>
    /// AUC-PR (average precision): resume a qualidade do modelo em TODOS os cortes de uma vez.
    /// Vai de 0 a 1. Um modelo que chuta ao acaso tira a própria taxa de fraude (aqui, ~0,006).
    ///
    /// Como funciona: ordena as transações da mais suspeita para a menos suspeita e vai
    /// descendo a lista. Cada vez que o recall aumenta, soma (aumento do recall × precisão
    /// naquele ponto). Um modelo bom coloca as fraudes no topo, mantendo a precisão alta.
    /// </summary>
    public static double AveragePrecision(double[] probabilities, double[] y)
    {
        int n = y.Length;
        int totalFrauds = y.Count(v => v == 1);
        if (totalFrauds == 0)
            return 0;

        // Índices ordenados da maior probabilidade para a menor.
        int[] order = Enumerable.Range(0, n).ToArray();
        Array.Sort(order, (a, b) => probabilities[b].CompareTo(probabilities[a]));

        double area = 0;
        int tp = 0, fp = 0;
        double previousRecall = 0;

        int i = 0;
        while (i < n)
        {
            // Transações com a MESMA probabilidade entram juntas: nenhum corte as separa.
            double current = probabilities[order[i]];
            while (i < n && probabilities[order[i]] == current)
            {
                if (y[order[i]] == 1) tp++;
                else fp++;
                i++;
            }

            double recall = (double)tp / totalFrauds;
            double precision = (double)tp / (tp + fp);

            area += (recall - previousRecall) * precision;
            previousRecall = recall;
        }

        return area;
    }
}
