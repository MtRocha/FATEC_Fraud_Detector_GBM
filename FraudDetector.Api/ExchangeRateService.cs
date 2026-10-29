using System.Collections.Concurrent;
using System.Text.Json;

namespace FraudDetector.Api;

/// <summary>Taxa de conversão para a moeda do modelo e de onde ela veio.</summary>
public record ExchangeRateResult(double Rate, string Source, DateTime? FetchedAtUtc);

/// <summary>
/// Busca a cotação em uma API pública (Frankfurter, taxas de referência do Banco Central Europeu)
/// para converter o valor da compra para a moeda em que o modelo foi treinado (dólar).
///
/// Chamar um serviço externo no meio de uma previsão traz dois riscos: lentidão e queda.
/// Por isso há três camadas de proteção:
///   1. CACHE: a taxa é guardada por 1 hora; a maioria das previsões nem chega a sair para a internet.
///   2. TIMEOUT: se a API de câmbio não responder em 3 segundos, desistimos (configurado no Program.cs).
///   3. FALLBACK: se ela falhar, usamos a última taxa conhecida ou a taxa fixa do appsettings.json.
/// </summary>
public class ExchangeRateService
{
    public const string HttpClientName = "exchange-rates";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExchangeRateService> _logger;

    // ConcurrentDictionary: várias requisições podem ler e gravar o cache ao mesmo tempo.
    private readonly ConcurrentDictionary<string, (double Rate, DateTime FetchedAtUtc)> _cache = new();
    private readonly Dictionary<string, double> _fallbackRates = new(StringComparer.OrdinalIgnoreCase);

    public string ModelCurrency { get; }

    public ExchangeRateService(
        IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<ExchangeRateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        ModelCurrency = (configuration["Currency:ModelCurrency"] ?? "USD").ToUpperInvariant();

        foreach (IConfigurationSection rate in configuration.GetSection("Currency:FallbackRatesToModelCurrency").GetChildren())
        {
            if (double.TryParse(rate.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0)
                _fallbackRates[rate.Key] = value;
        }
    }

    /// <summary>
    /// Quanto vale 1 unidade de <paramref name="currency"/> na moeda do modelo.
    /// Devolve null se a moeda não existir (ou se a API estiver fora do ar e não houver fallback).
    /// </summary>
    public async Task<ExchangeRateResult?> GetRateAsync(string? currency, CancellationToken cancellationToken = default)
    {
        // Moeda omitida = moeda do modelo: nada a converter.
        if (string.IsNullOrWhiteSpace(currency))
            return new ExchangeRateResult(1.0, "sem conversão", null);

        string code = currency.Trim().ToUpperInvariant();
        if (code == ModelCurrency)
            return new ExchangeRateResult(1.0, "sem conversão", null);

        // Códigos de moeda têm 3 letras (BRL, EUR...). Recusar o resto também evita
        // que um texto qualquer enviado pelo cliente vá parar na URL da chamada externa.
        if (code.Length != 3 || !code.All(char.IsAsciiLetter))
            return null;

        // 1) Cache ainda válido?
        bool hasCached = _cache.TryGetValue(code, out var cached);
        if (hasCached && DateTime.UtcNow - cached.FetchedAtUtc < CacheDuration)
            return new ExchangeRateResult(cached.Rate, "cache", cached.FetchedAtUtc);

        // 2) Busca na API. Resposta esperada:
        //    {"amount":1.0,"base":"BRL","date":"2026-10-02","rates":{"USD":0.19152}}
        try
        {
            HttpClient http = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await http.GetAsync(
                $"v1/latest?base={code}&symbols={ModelCurrency}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                using JsonDocument document = JsonDocument.Parse(json);
                double rate = document.RootElement.GetProperty("rates").GetProperty(ModelCurrency).GetDouble();

                if (rate > 0)
                {
                    DateTime now = DateTime.UtcNow;
                    _cache[code] = (rate, now);
                    return new ExchangeRateResult(rate, "api", now);
                }
            }
            else
            {
                _logger.LogWarning("API de câmbio respondeu {Status} para {Currency}.", (int)response.StatusCode, code);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Falha ao consultar a API de câmbio para {Currency}.", code);
        }

        // 3) A API falhou: última taxa conhecida, depois a taxa fixa do appsettings.json.
        if (hasCached)
            return new ExchangeRateResult(cached.Rate, "cache vencido (API de câmbio indisponível)", cached.FetchedAtUtc);

        if (_fallbackRates.TryGetValue(code, out double fallback))
            return new ExchangeRateResult(fallback, "taxa fixa do appsettings (API de câmbio indisponível)", null);

        return null;
    }
}
