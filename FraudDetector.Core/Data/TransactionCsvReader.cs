using System.Globalization;

namespace FraudDetector.Core.Data;

/// <summary>
/// Lê os arquivos fraudTrain.csv / fraudTest.csv e devolve as transações já tipadas.
/// </summary>
public static class TransactionCsvReader
{
    // InvariantCulture: garante que "4.97" seja lido como 4,97 e não 497,
    // mesmo com o Windows configurado em português.
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IEnumerable<LabeledTransaction> Read(string path)
    {
        // File.ReadLines lê uma linha por vez (não carrega os 350 MB de uma vez na memória).
        using var lines = File.ReadLines(path).GetEnumerator();

        if (!lines.MoveNext())
            throw new InvalidDataException($"Arquivo vazio: {path}");

        // Localizamos as colunas pelo NOME no cabeçalho, não pela posição.
        // Assim o código não quebra se a ordem das colunas mudar.
        List<string> header = CsvParser.SplitLine(lines.Current);
        int Col(string name)
        {
            int index = header.IndexOf(name);
            if (index < 0)
                throw new InvalidDataException($"Coluna '{name}' não encontrada em {path}");
            return index;
        }

        int iTime = Col("trans_date_trans_time");
        int iCategory = Col("category");
        int iAmount = Col("amt");
        int iGender = Col("gender");
        int iLat = Col("lat");
        int iLong = Col("long");
        int iDob = Col("dob");
        int iMerchLat = Col("merch_lat");
        int iMerchLong = Col("merch_long");
        int iFraud = Col("is_fraud");

        int lineNumber = 1;
        while (lines.MoveNext())
        {
            lineNumber++;
            string line = lines.Current;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            List<string> f = CsvParser.SplitLine(line);
            if (f.Count != header.Count)
                throw new InvalidDataException(
                    $"Linha {lineNumber}: esperados {header.Count} campos, encontrados {f.Count}");

            var transaction = new Transaction(
                TransactionTime: DateTime.ParseExact(f[iTime], "yyyy-MM-dd HH:mm:ss", Inv),
                Category: f[iCategory],
                Amount: double.Parse(f[iAmount], Inv),
                Gender: f[iGender],
                CustomerLat: double.Parse(f[iLat], Inv),
                CustomerLong: double.Parse(f[iLong], Inv),
                DateOfBirth: DateOnly.ParseExact(f[iDob], "yyyy-MM-dd", Inv),
                MerchantLat: double.Parse(f[iMerchLat], Inv),
                MerchantLong: double.Parse(f[iMerchLong], Inv));

            yield return new LabeledTransaction(transaction, f[iFraud] == "1");
        }
    }
}
