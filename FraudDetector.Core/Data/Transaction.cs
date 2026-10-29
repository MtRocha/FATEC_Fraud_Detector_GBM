namespace FraudDetector.Core.Data;

/// <summary>
/// Uma transação "crua": só os campos que decidimos usar.
/// É exatamente isso que a API vai receber no futuro.
/// </summary>
public record Transaction(
    DateTime TransactionTime,
    string Category,
    double Amount,
    string Gender,
    double CustomerLat,
    double CustomerLong,
    DateOnly DateOfBirth,
    double MerchantLat,
    double MerchantLong);

/// <summary>
/// Transação + resposta conhecida (fraude ou não). Só existe no treino e na avaliação;
/// na API a resposta é justamente o que queremos prever.
/// </summary>
public record LabeledTransaction(Transaction Transaction, bool IsFraud);
