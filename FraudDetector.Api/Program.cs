using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

// Se o modelo não carregar, a API NÃO derruba o processo: ela sobe e responde a qualquer
// rota com o motivo do problema. Num servidor (Azure, IIS), um erro na inicialização vira
// só "HTTP Error 500.30", sem dizer o que aconteceu; assim, o motivo aparece na tela.
FraudModel? model = null;
string? modelError = null;
try
{
    string modelPath = modelCandidates.FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException(
            $"Modelo não encontrado. Procurei em: {string.Join(" e ", modelCandidates)}. " +
            "Rode a opção 6 do console (treinar o modelo final) e confira se o arquivo foi junto na publicação.");
    model = FraudModel.Load(modelPath);
}
catch (Exception ex)
{
    modelError = $"{ex.GetType().Name}: {ex.Message}";
}

// O registro existe mesmo sem modelo (os endpoints precisam dele para serem montados),
// mas nunca é usado nesse caso: o bloqueio logo abaixo responde antes.
builder.Services.AddSingleton<FraudModel>(_ => model ?? throw new InvalidOperationException(modelError));

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

// Registro de uso do serviço, para o monitor (/monitor.html). Grava em arquivos na pasta logs/.
builder.Services.AddSingleton<RequestLog>();

// Swagger: o ApiExplorer descobre os endpoints, e o SwaggerGen monta o documento OpenAPI com eles.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (modelError is not null)
{
    app.Logger.LogCritical("A API subiu SEM modelo: {Error}", modelError);

    // Toda requisição recebe 503 com a explicação, em vez de a aplicação cair.
    app.Use(async (HttpContext context, RequestDelegate _) =>
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            status = "modelo indisponível",
            error = modelError,
            searchedPaths = modelCandidates,
            contentRoot = app.Environment.ContentRootPath,
            baseDirectory = AppContext.BaseDirectory,
        });
    });
}

// ----------------------------------------------------------------------
// Registro de uso: mede e grava cada chamada às rotas do serviço.
// Um "middleware" é um trecho que envolve TODAS as requisições: ele roda antes do
// endpoint (lê o corpo, liga o cronômetro) e depois dele (vê o status e grava o registro).
// ----------------------------------------------------------------------
app.Use(async (context, next) =>
{
    string path = context.Request.Path.Value ?? "";
    bool shouldLog = path == "/predict" || path == "/health"
                     || (path == "/feed" && HttpMethods.IsDelete(context.Request.Method));
    if (!shouldLog)
    {
        await next(); // páginas, Swagger e a consulta do painel a cada segundo não entram no relatório
        return;
    }

    string? body = null;
    if (HttpMethods.IsPost(context.Request.Method))
    {
        // O corpo é um fluxo que só pode ser lido uma vez; EnableBuffering permite ler aqui e de novo no endpoint.
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;
        if (body.Length > 4000) body = body[..4000] + "…";
    }

    var stopwatch = Stopwatch.StartNew();
    bool crashed = false;
    try
    {
        await next();
    }
    catch
    {
        crashed = true;
        throw;
    }
    finally
    {
        stopwatch.Stop();
        string? userAgent = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent)) userAgent = null;
        string? clientId = context.Request.Headers["X-Client-Id"].ToString();
        if (string.IsNullOrWhiteSpace(clientId)) clientId = null;
        else if (clientId.Length > 60) clientId = clientId[..60];

        // O endpoint /predict deixa o resultado em context.Items para o registro aproveitar.
        var assessment = context.Items["assessment"] as FraudAssessment;
        var errors = context.Items["errors"] as Dictionary<string, string[]>;

        context.RequestServices.GetRequiredService<RequestLog>().Add(new RequestLogEntry(
            Id: 0, // o registro atribui o número
            TimestampUtc: DateTime.UtcNow,
            Method: context.Request.Method,
            Path: path,
            Query: context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null,
            StatusCode: crashed ? 500 : context.Response.StatusCode,
            DurationMs: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
            ClientIp: ClientInfo.Ip(context),
            ClientId: clientId,
            Device: ClientInfo.Device(userAgent),
            Origin: ClientInfo.Origin(context.Request.Headers.Referer.ToString(), userAgent),
            UserAgent: userAgent,
            RequestBody: body,
            Decision: assessment?.DecisionCode,
            FraudProbability: assessment is null ? null : Math.Round(assessment.Probability, 6),
            Reasons: assessment?.Reasons,
            Errors: errors is { Count: > 0 } ? errors.SelectMany(e => e.Value).ToList() : null));
    }
});

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

// GET /feed/12/explain — refaz a previsão da compra nº 12 guardando cada passo,
// para a página explicar.html contar como o modelo decidiu.
app.MapGet("/feed/{id:long}/explain", (long id, TransactionFeed feed, FraudModel m) =>
{
    FeedEntry? entry = feed.Find(id);
    if (entry?.Source is null)
        return Results.NotFound(new { error = "Compra não encontrada. O painel guarda só as 200 mais recentes, e só na memória." });

    double[] features = FeatureExtractor.Extract(entry.Source);
    return Results.Ok(new { purchase = entry, explanation = m.Explain(features, entry.Threshold) });
})
.WithName("Explain")
.WithSummary("Explica, passo a passo, como o modelo avaliou uma compra do painel.");

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
    knownTerritoryGuard = m.FeatureRanges is not null, // false = modelo antigo, sem a proteção
})
.WithName("Health")
.WithSummary("Confirma que a API está no ar e mostra o modelo carregado.");

// POST /predict — recebe uma transação crua e devolve a probabilidade de fraude.
// O corte pode ser trocado por requisição: POST /predict?threshold=0.3
app.MapPost("/predict", async (
    PredictRequest request, FraudModel m, ExchangeRateService rates, TransactionFeed feed,
    HttpContext http, double? threshold, CancellationToken cancellationToken) =>
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
    {
        http.Items["errors"] = errors; // para o registro de uso
        return Results.ValidationProblem(errors);
    } // resposta 400 com a lista de problemas

    // Converte o pedido da API para a transação que o modelo entende (valor já na moeda do treino).
    Transaction transaction = request.ToTransaction(rate.Rate);

    // Aqui acontece tudo o que construímos: features → soma das árvores → sigmoide,
    // mais a checagem de que a transação está dentro do que o modelo conhece.
    FraudAssessment assessment = m.Assess(transaction, cut);
    http.Items["assessment"] = assessment; // para o registro de uso
    double probability = assessment.Probability;

    // Registra a compra para o painel, com os números que ajudam a explicar a decisão.
    FeedEntry logged = feed.Add(new FeedEntry(
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
        IsFraud: assessment.Decision == FraudDecision.Block,
        Threshold: cut,
        Decision: assessment.DecisionCode,
        Reasons: assessment.Reasons,
        Source: transaction));

    return Results.Ok(new PredictionResponse(
        FraudProbability: Math.Round(probability, 6),
        IsFraud: assessment.Decision == FraudDecision.Block,
        Threshold: cut,
        Decision: assessment.DecisionCode,
        Reasons: assessment.Reasons,
        AmountInModelCurrency: Math.Round(transaction.Amount, 2),
        ModelCurrency: rates.ModelCurrency,
        ExchangeRate: rate.Rate,
        ExchangeRateSource: rate.Source,
        FeedId: logged.Id));
})
.WithName("Predict")
.WithSummary("Calcula a probabilidade de fraude de uma transação.")
.WithDescription("""
    Recebe a transação crua, calcula as 20 features e soma as árvores do modelo.
    O parâmetro `threshold` é opcional: sem ele, vale o corte gravado no modelo.

    A decisão (`decision`) pode ser `aprovada`, `bloqueada` (probabilidade acima do corte) ou
    `revisao`: a transação tem valores que o modelo nunca viu no treino (valor, idade ou distância
    de uma compra presencial), então a probabilidade dele não é confiável e pede-se confirmação.
    `reasons` explica a decisão.

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

// ----------------------------------------------------------------------
// Monitor de uso (/monitor.html). Protegido por chave, porque mostra IPs e os dados
// enviados por quem usa o serviço. A chave vai no cabeçalho X-Monitor-Key.
// ----------------------------------------------------------------------
var monitor = app.MapGroup("/monitor")
    .ExcludeFromDescription() // não aparece no Swagger público
    .AddEndpointFilter(async (ctx, next) =>
    {
        string expected = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Monitor:AccessKey"] ?? "";
        string given = ctx.HttpContext.Request.Headers["X-Monitor-Key"].ToString();

        if (expected.Length == 0)
            return Results.Problem("Defina Monitor:AccessKey no appsettings.json para usar o monitor.", statusCode: 503);

        // FixedTimeEquals compara sem "atalhos", para o tempo de resposta não revelar a chave aos poucos.
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(given)))
            return Results.Unauthorized();

        return await next(ctx);
    });

// GET /monitor/requests?after=N — chamadas novas e indicadores do dia (o monitor consulta a cada 2 segundos).
monitor.MapGet("/requests", (RequestLog log, long? after) => log.Snapshot(after ?? 0));

// GET /monitor/report.csv?de=2026-11-01&ate=2026-11-02 — relatório completo, lido dos arquivos.
monitor.MapGet("/report.csv", (RequestLog log, DateOnly? de, DateOnly? ate) =>
{
    DateOnly start = de ?? log.Today();
    DateOnly end = ate ?? start;
    if (end < start) (start, end) = (end, start);
    if (end.DayNumber - start.DayNumber > 92)
        return Results.Problem("Escolha um período de até 3 meses.", statusCode: 400);

    byte[] csv = log.ToCsv(log.ReadRange(start, end));
    return Results.File(csv, "text/csv; charset=utf-8", $"relatorio-{start:yyyy-MM-dd}-a-{end:yyyy-MM-dd}.csv");
});

app.Run();

/// <summary>Resposta do /predict.</summary>
public record PredictionResponse(
    double FraudProbability,
    bool IsFraud,
    double Threshold,
    string Decision,               // "aprovada", "revisao" ou "bloqueada"
    IReadOnlyList<string> Reasons, // motivos da decisão, em linguagem humana
    double AmountInModelCurrency, // o valor que o modelo realmente avaliou, depois da conversão
    string ModelCurrency,
    double ExchangeRate,          // quanto valia 1 unidade da moeda enviada, na moeda do modelo
    string ExchangeRateSource,    // "api", "cache", "sem conversão" ou um aviso de fallback
    long FeedId);                 // número da compra no painel (usado para abrir a explicação)
