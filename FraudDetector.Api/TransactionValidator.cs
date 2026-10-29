using FraudDetector.Core.Features;

namespace FraudDetector.Api;

/// <summary>
/// Confere o pedido antes de entregá-lo ao modelo.
/// O modelo sempre devolve um número, mesmo para dados absurdos; por isso a API precisa
/// recusar entradas inválidas em vez de responder com uma probabilidade sem sentido.
/// </summary>
public static class TransactionValidator
{
    // Faixa de idades que existia nos dados de treino. Fora dela, o modelo estaria chutando.
    private const int MinimumAge = 14;
    private const int MaximumAge = 100;

    public static Dictionary<string, string[]> Validate(PredictRequest r)
    {
        var errors = new Dictionary<string, string[]>();

        // "Kind" revela como a hora foi escrita no JSON:
        //   2026-10-03T23:15:00        → Unspecified (hora local, o que queremos)
        //   2026-10-03T23:15:00Z       → Utc
        //   2026-10-03T23:15:00-03:00  → Local (o .NET já converteu para o fuso do servidor!)
        // O modelo aprendeu com a hora LOCAL da compra, então só o primeiro formato é seguro.
        if (r.TransactionTime.Year < 2000)
            errors["transactionTime"] = ["Informe a data e hora local da transação (ex.: 2026-10-03T23:15:00)."];
        else if (r.TransactionTime.Kind != DateTimeKind.Unspecified)
            errors["transactionTime"] = ["Envie a hora LOCAL da compra, sem 'Z' nem deslocamento de fuso (ex.: 2026-10-03T23:15:00)."];

        if (string.IsNullOrWhiteSpace(r.Category) || !FeatureExtractor.KnownCategories.Contains(r.Category))
            errors["category"] = [$"Categoria desconhecida. Use uma de: {string.Join(", ", FeatureExtractor.KnownCategories)}."];

        if (r.Amount <= 0)
            errors["amount"] = ["O valor deve ser maior que zero."];

        // A moeda é conferida no Program.cs, porque depende da consulta à API de câmbio.

        if (r.Gender is not ("M" or "F"))
            errors["gender"] = ["Use M ou F."];

        if (r.DateOfBirth.Year < 1900)
        {
            errors["dateOfBirth"] = ["Informe a data de nascimento (ex.: 1992-08-14)."];
        }
        else if (!errors.ContainsKey("transactionTime"))
        {
            double age = FeatureExtractor.AgeInYears(r.DateOfBirth, r.TransactionTime);
            if (age < MinimumAge || age > MaximumAge)
                errors["dateOfBirth"] = [$"O titular teria {age} anos na data da compra. O modelo só conhece idades de {MinimumAge} a {MaximumAge}."];
        }

        if (!IsLatitude(r.CustomerLat) || !IsLongitude(r.CustomerLong))
            errors["customerLocation"] = ["customerLat deve estar entre -90 e 90, e customerLong entre -180 e 180."];

        if (!IsLatitude(r.MerchantLat) || !IsLongitude(r.MerchantLong))
            errors["merchantLocation"] = ["merchantLat deve estar entre -90 e 90, e merchantLong entre -180 e 180."];

        return errors;
    }

    private static bool IsLatitude(double value) => value is >= -90 and <= 90;
    private static bool IsLongitude(double value) => value is >= -180 and <= 180;
}
