using FraudDetector.Core.Data;

namespace FraudDetector.Api;

/// <summary>
/// O que a API recebe. É quase igual ao Transaction do Core, com uma diferença:
/// aqui o valor pode vir em outra moeda. Separar os dois tipos deixa o Core
/// "puro" (sempre na moeda do treino) e concentra as conversões na API.
/// </summary>
public record PredictRequest(
    DateTime TransactionTime,
    string Category,
    double Amount,
    string? Currency,
    string Gender,
    double CustomerLat,
    double CustomerLong,
    DateOnly DateOfBirth,
    double MerchantLat,
    double MerchantLong)
{
    /// <summary>Monta a transação do modelo, com o valor já convertido. Chame só depois de validar.</summary>
    public Transaction ToTransaction(double rateToModelCurrency) => new(
        TransactionTime: TransactionTime,
        Category: Category,
        Amount: Amount * rateToModelCurrency,
        Gender: Gender,
        CustomerLat: CustomerLat,
        CustomerLong: CustomerLong,
        DateOfBirth: DateOfBirth,
        MerchantLat: MerchantLat,
        MerchantLong: MerchantLong);
}
