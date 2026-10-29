using System.Text;

namespace FraudDetector.Core.Data;

/// <summary>
/// Divide uma linha de CSV em campos, respeitando aspas.
/// Um Split(',') simples quebraria "fraud_Rippin, Kub and Mann" em dois campos.
/// </summary>
public static class CsvParser
{
    public static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool insideQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (insideQuotes)
            {
                if (c == '"')
                {
                    // Duas aspas seguidas ("") dentro de um campo representam uma aspa literal.
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        insideQuotes = false; // fechou o campo entre aspas
                    }
                }
                else
                {
                    current.Append(c); // aqui a vírgula é texto comum
                }
            }
            else if (c == '"')
            {
                insideQuotes = true;
            }
            else if (c == ',')
            {
                // Vírgula fora de aspas: terminou um campo.
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString()); // último campo (não termina com vírgula)
        return fields;
    }
}
