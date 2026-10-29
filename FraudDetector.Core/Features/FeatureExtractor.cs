using FraudDetector.Core.Data;

namespace FraudDetector.Core.Features;

/// <summary>
/// Transforma uma transação crua em um vetor de números (features),
/// que é o único formato que o modelo entende.
///
/// REGRA DE OURO: treino e API precisam usar exatamente esta mesma classe.
/// Se a ordem ou o cálculo de uma feature mudar, o modelo já treinado deixa de valer.
/// </summary>
public static class FeatureExtractor
{
    // A população da cidade (city_pop) ficou de fora de propósito: cada cidade tem um valor
    // único, então ela funcionava quase como um identificador do cliente, e a escala é a dos EUA.

    // As 14 categorias da base, em ordem fixa. Cada uma vira uma coluna 0/1.
    private static readonly string[] Categories =
    [
        "entertainment", "food_dining", "gas_transport", "grocery_net", "grocery_pos",
        "health_fitness", "home", "kids_pets", "misc_net", "misc_pos",
        "personal_care", "shopping_net", "shopping_pos", "travel",
    ];

    /// <summary>Categorias que o modelo conhece (a API usa para validar a entrada).</summary>
    public static IReadOnlyList<string> KnownCategories => Categories;

    // Quantidade de features numéricas que vêm antes das colunas de categoria.
    private const int NumericFeatureCount = 6;

    /// <summary>Nome de cada posição do vetor (útil para depurar e explicar o modelo).</summary>
    public static IReadOnlyList<string> FeatureNames { get; } =
    [
        "amount", "hour", "day_of_week", "age", "distance_km", "is_male",
        .. Categories.Select(c => "cat_" + c),
    ];

    /// <summary>Tamanho do vetor de features (6 numéricas + 14 categorias = 20).</summary>
    public static int FeatureCount => FeatureNames.Count;

    /// <summary>Ponto de entrada: uma transação entra, um vetor de 20 números sai.</summary>
    public static double[] Extract(Transaction t)
    {
        var features = new double[FeatureCount];

        features[0] = t.Amount;
        features[1] = HourOfDay(t.TransactionTime);
        features[2] = DayOfWeek(t.TransactionTime);
        features[3] = AgeInYears(t.DateOfBirth, t.TransactionTime);
        features[4] = HaversineKm(t.CustomerLat, t.CustomerLong, t.MerchantLat, t.MerchantLong);
        features[5] = IsMale(t.Gender);

        EncodeCategory(t.Category, features);

        return features;
    }

    /// <summary>Hora da compra, de 0 a 23.</summary>
    public static double HourOfDay(DateTime time) => time.Hour;

    /// <summary>Dia da semana: 0 = domingo ... 6 = sábado.</summary>
    public static double DayOfWeek(DateTime time) => (int)time.DayOfWeek;

    /// <summary>Idade do cliente em anos completos na data da compra.</summary>
    public static double AgeInYears(DateOnly birth, DateTime at)
    {
        int age = at.Year - birth.Year;

        // Se ainda não fez aniversário neste ano, desconta 1.
        bool birthdayStillToCome =
            at.Month < birth.Month || (at.Month == birth.Month && at.Day < birth.Day);
        if (birthdayStillToCome)
            age--;

        return age;
    }

    /// <summary>1 para masculino, 0 para feminino.</summary>
    public static double IsMale(string gender) => gender == "M" ? 1 : 0;

    /// <summary>
    /// Distância em km entre dois pontos da Terra (fórmula de haversine).
    /// Não dá para usar Pitágoras direto porque latitude/longitude são ângulos
    /// sobre uma esfera: 1 grau de longitude vale menos km perto dos polos.
    /// </summary>
    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;

        double phi1 = ToRadians(lat1);
        double phi2 = ToRadians(lat2);
        double deltaPhi = ToRadians(lat2 - lat1);
        double deltaLambda = ToRadians(lon2 - lon1);

        double a = Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2)
                 + Math.Cos(phi1) * Math.Cos(phi2)
                 * Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2);

        return 2 * earthRadiusKm * Math.Asin(Math.Sqrt(a));
    }

    /// <summary>
    /// One-hot encoding: liga (1) só a coluna da categoria da transação.
    /// Usar um código único (0, 1, 2...) criaria uma ordem falsa entre categorias.
    /// Categoria desconhecida deixa todas as colunas em 0.
    /// </summary>
    private static void EncodeCategory(string category, double[] features)
    {
        int index = Array.IndexOf(Categories, category);
        if (index >= 0)
            features[NumericFeatureCount + index] = 1;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
