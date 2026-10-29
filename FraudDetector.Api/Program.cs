using FraudDetector.Api;
using FraudDetector.Core.Data;
using FraudDetector.Core.Features;
using FraudDetector.Core.Model;

var builder = WebApplication.CreateBuilder(args);

// ----------------------------------------------------------------------
// Carrega o modelo UMA vez, na inicialização. Ele fica na memória e é
// compartilhado por todas as requisições (prever não altera o modelo).
// ----------------------------------------------------------------------
// O arquivo é procurado em dois lugares:
//   1. no caminho do appsettings.json (../Models, ao rodar pelo Visual Studio);
//   2. na pasta Models ao lado do executável (onde ele vai parar ao publicar na internet).
string[] modelCandidates =
[
    Path.GetFullPath(Path.Combine(
        builder.Environment.ContentRootPath,
        builder.Configuration["ModelPath"] ?? "../Models/fraud.model.json")),
    Path.Combine(AppContext.BaseDirectory, "Models", "fraud.model.json"),
];

string modelPath = modelCandidates.FirstOrDefault(File.Exists)
    ?? throw new FileNotFoundException(
        $"Modelo não encontrado. Procurei em: {string.Join(" e ", modelCandidates)}. " +
        "Rode a opção 6 do console (treinar o modelo final) primeiro.");

FraudModel model = FraudModel.Load(modelPath);
builder.Services.AddSingleton(model);

// Conversão de moeda: o modelo aprendeu com valores em dólar, então a API busca a cotação do dia.
// O serviço é singleton para que o cache de cotações seja compartilhado entre as requisições.
builder.Services.AddHttpClient(ExchangeRateService.HttpClientName, client =>
{
    client.BaseAddress = new Uri("https://api.frankfurter.dev/");
    client.Timeout = TimeSpan.FromSeconds(3); // câmbio lento não pode travar a previsão
});
builder.Services.AddSingleton<ExchangeRateService>();

// Histórico das compras avaliadas, para o painel ao vivo (singleton: um só para a API inteira).
builder.Services.AddSingleton<TransactionFeed>();

// Swagger: o ApiExplorer descobre os endpoints, e o SwaggerGen monta o documento OpenAPI com eles.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Documento em /swagger/v1/swagger.json e página interativa em /swagger.
app.UseSwagger();
app.UseSwaggerUI(options => options.DocumentTitle = "FraudDetector API");

// A raiz leva direto para a página do Swagger (e não aparece na documentação).
// Páginas da demonstração (pasta wwwroot): /pagar.html no celular e /painel.html no projetor.
app.UseStaticFiles();

// A raiz leva ao painel. O Swagger continua em /swagger.
app.MapGet("/", () => Results.Redirect("/painel.html")).ExcludeFromDescription();

// GET /feed?after=N — compras avaliadas depois da de número N. O painel chama isto a cada segundo.
app.MapGet("/feed", (TransactionFeed feed, long? after) => feed.Since(after ?? 0))
    .WithName("Feed")
    .WithSummary("Compras avaliadas recentemente (usado pelo painel ao vivo).");

// DELETE /feed — zera o painel.
app.MapDelete("/feed", (TransactionFeed feed) =>
{
    feed.Clear();
    return Results.NoContent();
})
.WithName("ClearFeed")
.WithSummary("Apaga o histórico do painel.");

// GET /health — confirma que a API está no ar e mostra qual modelo foi carregado.
app.MapGet("/health", (FraudModel m, ExchangeRateService rates) => new
{
    status = "ok",
    trees = m.Trees.Count,
    features = m.FeatureNames.Count,
    threshold = m.Threshold,
    trainedAtUtc = m.TrainedAtUtc,
    modelCurrency = rates.ModelCurrency,
})
.WithName("Health")
.WithSummary("Confirma que a API está no ar e mostra o modelo carregado.");

// POST /predict — recebe uma transação crua e devolve a probabilidade de fraude.
// O corte pode ser trocado por requisição: POST /predict?threshold=0.3
app.MapPost("/predict", async (
    PredictRequest request, FraudModel m, ExchangeRateService rates, TransactionFeed feed,
    double? threshold, CancellationToken cancellationToken) =>
{
    Dictionary<string, string[]> errors = TransactionValidator.Validate(request);

    double cut = threshold ?? m.Threshold;
    if (cut <= 0 || cut >= 1)
        errors["threshold"] = ["O corte deve estar entre 0 e 1 (exclusivos)."];

    // "await": enquanto a cotação não chega, o servidor fica livre para atender outras requisições.
    ExchangeRateResult? rate = await rates.GetRateAsync(request.Currency, cancellationToken);
    if (rate is null)
        errors["currency"] = ["Moeda não reconhecida (use o código de 3 letras, ex.: BRL), ou o serviço de câmbio está indisponível."];

    if (errors.Count > 0 || rate is null)
        return Results.ValidationProblem(errors); // resposta 400 com a lista de problemas

    // Converte o pedido da API para a transação que o modelo entende (valor já na moeda do treino).
    Transaction transaction = request.ToTransaction(rate.Rate);

    // Aqui acontece tudo o que construímos: features → soma das árvores → sigmoide.
    double probability = m.PredictProbability(transaction);

    // Registra a compra para o painel, com os números que ajudam a explicar a decisão.
    feed.Add(new FeedEntry(
        Id: 0, // o feed atribui o número
        ReceivedAtUtc: DateTime.UtcNow,
        Category: transaction.Category,
        Amount: request.Amount,
        Currency: string.IsNullOrWhiteSpace(request.Currency) ? rates.ModelCurrency : request.Currency.Trim().ToUpperInvariant(),
        AmountInModelCurrency: Math.Round(transaction.Amount, 2),
        Hour: transaction.TransactionTime.Hour,
        Age: FeatureExtractor.AgeInYears(transaction.DateOfBirth, transaction.TransactionTime),
        DistanceKm: Math.Round(FeatureExtractor.HaversineKm(
            transaction.CustomerLat, transaction.CustomerLong, transaction.MerchantLat, transaction.MerchantLong), 1),
        Gender: transaction.Gender,
        FraudProbability: Math.Round(probability, 6),
        IsFraud: probability >= cut,
        Threshold: cut));

    return Results.Ok(new PredictionResponse(
        FraudProbability: Math.Round(probability, 6),
        IsFraud: probability >= cut,
        Threshold: cut,
        AmountInModelCurrency: Math.Round(transaction.Amount, 2),
        ModelCurrency: rates.ModelCurrency,
        ExchangeRate: rate.Rate,
        ExchangeRateSource: rate.Source));
})
.WithName("Predict")
.WithSummary("Calcula a probabilidade de fraude de uma transação.")
.WithDescription("""
    Recebe a transação crua, calcula as 20 features e soma as árvores do modelo.
    O parâmetro `threshold` é opcional: sem ele, vale o corte gravado no modelo.

    - `transactionTime`: hora LOCAL da compra, sem `Z` nem deslocamento de fuso.
    - `currency`: moeda do valor (ex.: `BRL`). Se omitida, vale a moeda do modelo (`USD`).
      A cotação é buscada em uma API pública e guardada em cache por 1 hora.

    Exemplo de corpo (fraude real do fraudTest.csv):

    ```json
    {
      "transactionTime": "2020-06-21T22:32:22",
      "category": "misc_net",
      "amount": 780.52,
      "currency": "USD",
      "gender": "M",
      "customerLat": 42.5545,
      "customerLong": -90.3508,
      "dateOfBirth": "1958-09-10",
      "merchantLat": 42.461127,
      "merchantLong": -91.147148
    }
    ```
    """)
.Produces<PredictionResponse>()
.ProducesValidationProblem();

app.Run();

/// <summary>Resposta do /predict.</summary>
public record PredictionResponse(
    double FraudProbability,
    bool IsFraud,
    double Threshold,
    double AmountInModelCurrency, // o valor que o modelo realmente avaliou, depois da conversão
    string ModelCurrency,
    double ExchangeRate,          // quanto valia 1 unidade da moeda enviada, na moeda do modelo
    string ExchangeRateSource);   // "api", "cache", "sem conversão" ou um aviso de fallback
