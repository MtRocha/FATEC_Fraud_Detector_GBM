using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FraudDetector.Api;

/// <summary>Uma chamada ao serviço: quem chamou, o que enviou, o que recebeu e quanto demorou.</summary>
public record RequestLogEntry(
    long Id,
    DateTime TimestampUtc,
    string Method,
    string Path,
    string? Query,
    int StatusCode,
    double DurationMs,
    string ClientIp,
    string? ClientId,          // cabeçalho opcional X-Client-Id, para integradores se identificarem
    string Device,             // ex.: "Chrome · Android", "curl"
    string Origin,             // ex.: "Página de pagamento", "Swagger", "Ferramenta de API"
    string? UserAgent,
    string? RequestBody,       // o JSON recebido (só em POST)
    string? Decision,          // "aprovada", "revisao" ou "bloqueada" (só no /predict com sucesso)
    double? FraudProbability,
    IReadOnlyList<string>? Reasons,
    IReadOnlyList<string>? Errors) // mensagens de validação, quando a resposta foi 400
{
    /// <summary>Como o cliente aparece no monitor: o nome que ele informou ou IP + dispositivo.</summary>
    public string ClientKey => ClientId ?? $"{ClientIp} · {Device}";
}

public record ClientSummary(string Client, string ClientIp, string Device, string Origin, int Requests, int Blocked, DateTime LastSeenUtc);

public record MonitorStats(
    string Today,
    int RequestsToday,
    int PredictionsToday,
    int ApprovedToday,
    int ReviewToday,
    int BlockedToday,
    int ErrorsToday,
    int RequestsLast5Min,
    int ActiveClientsLastHour,
    double AvgDurationMs,
    IReadOnlyList<ClientSummary> Clients);

public record MonitorSnapshot(IReadOnlyList<RequestLogEntry> Entries, long LastId, MonitorStats Stats);

/// <summary>
/// Registro de uso do serviço. Cada chamada vira uma linha em um arquivo por dia
/// (logs/requests-AAAA-MM-DD.jsonl, um JSON por linha) e também fica na memória,
/// para o monitor responder rápido. Ao reiniciar, a API relê os arquivos recentes.
///
/// JSON Lines foi escolhido por ser o formato mais simples que sobrevive a reinícios:
/// só acrescentamos linhas no fim do arquivo, sem precisar de banco de dados.
/// </summary>
public class RequestLog
{
    private const int MemoryCapacity = 5000; // chamadas mais recentes mantidas na memória
    private const int DaysToReload = 7;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _lock = new();
    private readonly List<RequestLogEntry> _entries = [];
    private readonly string _directory;
    private readonly TimeZoneInfo _timeZone;
    private readonly ILogger<RequestLog> _logger;
    private long _lastId;

    public RequestLog(IConfiguration configuration, IWebHostEnvironment environment, ILogger<RequestLog> logger)
    {
        _logger = logger;
        _directory = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath, configuration["Monitor:LogDirectory"] ?? "logs"));
        Directory.CreateDirectory(_directory);

        // Os dias do relatório seguem o fuso de quem monitora, não o do servidor (que costuma estar em UTC).
        try { _timeZone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Monitor:TimeZone"] ?? "America/Sao_Paulo"); }
        catch (TimeZoneNotFoundException) { _timeZone = TimeZoneInfo.Local; }

        ReloadRecentFiles();
    }

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, _timeZone);
    public DateOnly Today() => DateOnly.FromDateTime(ToLocal(DateTime.UtcNow));

    private string FileFor(DateOnly day) => Path.Combine(_directory, $"requests-{day:yyyy-MM-dd}.jsonl");

    /// <summary>Registra uma chamada (atribui o número) e grava no arquivo do dia.</summary>
    public RequestLogEntry Add(RequestLogEntry entry)
    {
        lock (_lock)
        {
            RequestLogEntry numbered = entry with { Id = ++_lastId };
            _entries.Add(numbered);
            if (_entries.Count > MemoryCapacity)
                _entries.RemoveAt(0);

            try
            {
                DateOnly day = DateOnly.FromDateTime(ToLocal(numbered.TimestampUtc));
                File.AppendAllText(FileFor(day), JsonSerializer.Serialize(numbered, Json) + "\n", Encoding.UTF8);
            }
            catch (IOException ex)
            {
                // Sem disco disponível, o monitor continua funcionando só com a memória.
                _logger.LogWarning(ex, "Não foi possível gravar o registro da requisição {Id}.", numbered.Id);
            }

            return numbered;
        }
    }

    /// <summary>Chamadas depois da de número <paramref name="afterId"/> (no máximo as 200 mais recentes) e os indicadores.</summary>
    public MonitorSnapshot Snapshot(long afterId)
    {
        lock (_lock)
        {
            List<RequestLogEntry> news = _entries.Where(e => e.Id > afterId).TakeLast(200).ToList();
            return new MonitorSnapshot(news, _lastId, ComputeStats());
        }
    }

    private MonitorStats ComputeStats()
    {
        DateTime now = DateTime.UtcNow;
        DateOnly today = Today();
        List<RequestLogEntry> todayEntries = _entries
            .Where(e => DateOnly.FromDateTime(ToLocal(e.TimestampUtc)) == today)
            .ToList();

        List<ClientSummary> clients = todayEntries
            .GroupBy(e => e.ClientKey)
            .Select(g =>
            {
                RequestLogEntry last = g.MaxBy(e => e.TimestampUtc)!;
                return new ClientSummary(g.Key, last.ClientIp, last.Device, last.Origin,
                    g.Count(), g.Count(e => e.Decision == "bloqueada"), last.TimestampUtc);
            })
            .OrderByDescending(c => c.LastSeenUtc)
            .Take(20)
            .ToList();

        List<RequestLogEntry> recent = _entries.TakeLast(100).ToList();

        return new MonitorStats(
            Today: today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            RequestsToday: todayEntries.Count,
            PredictionsToday: todayEntries.Count(e => e.Decision is not null),
            ApprovedToday: todayEntries.Count(e => e.Decision == "aprovada"),
            ReviewToday: todayEntries.Count(e => e.Decision == "revisao"),
            BlockedToday: todayEntries.Count(e => e.Decision == "bloqueada"),
            ErrorsToday: todayEntries.Count(e => e.StatusCode >= 400),
            RequestsLast5Min: _entries.Count(e => now - e.TimestampUtc <= TimeSpan.FromMinutes(5)),
            ActiveClientsLastHour: _entries.Where(e => now - e.TimestampUtc <= TimeSpan.FromHours(1))
                                           .Select(e => e.ClientKey).Distinct().Count(),
            AvgDurationMs: recent.Count == 0 ? 0 : Math.Round(recent.Average(e => e.DurationMs), 1),
            Clients: clients);
    }

    /// <summary>Lê dos arquivos todas as chamadas entre duas datas (inclusive), para o relatório.</summary>
    public List<RequestLogEntry> ReadRange(DateOnly from, DateOnly to)
    {
        var result = new List<RequestLogEntry>();
        for (DateOnly day = from; day <= to; day = day.AddDays(1))
            result.AddRange(ReadFile(FileFor(day)));
        return result;
    }

    private IEnumerable<RequestLogEntry> ReadFile(string path)
    {
        if (!File.Exists(path))
            yield break;

        string[] lines;
        lock (_lock) { lines = File.ReadAllLines(path, Encoding.UTF8); } // evita ler no meio de uma gravação

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            RequestLogEntry? entry = null;
            try { entry = JsonSerializer.Deserialize<RequestLogEntry>(line, Json); }
            catch (JsonException) { /* linha corrompida (ex.: queda de energia no meio da gravação): ignora */ }
            if (entry is not null)
                yield return entry;
        }
    }

    private void ReloadRecentFiles()
    {
        DateOnly today = Today();
        var loaded = new List<RequestLogEntry>();
        for (int back = DaysToReload - 1; back >= 0; back--)
            loaded.AddRange(ReadFile(FileFor(today.AddDays(-back))));

        _entries.AddRange(loaded.OrderBy(e => e.Id).TakeLast(MemoryCapacity));
        _lastId = loaded.Count == 0 ? 0 : loaded.Max(e => e.Id);
    }

    // ------------------------------------------------------------------
    // Relatório em CSV (abre direto no Excel em português)
    // ------------------------------------------------------------------

    public byte[] ToCsv(IEnumerable<RequestLogEntry> entries)
    {
        CultureInfo pt = CultureInfo.GetCultureInfo("pt-BR");
        static string Cell(string? value)
        {
            value ??= "";
            // Ponto e vírgula é o separador que o Excel em português espera; aspas protegem o resto.
            return value.Contains(';') || value.Contains('"') || value.Contains('\n')
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        var sb = new StringBuilder();
        sb.AppendLine("id;data_hora;metodo;rota;status;duracao_ms;ip;cliente_id;dispositivo;origem;decisao;probabilidade;motivos;erros;corpo_da_requisicao");
        foreach (RequestLogEntry e in entries)
        {
            sb.AppendLine(string.Join(';',
                e.Id.ToString(CultureInfo.InvariantCulture),
                ToLocal(e.TimestampUtc).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                e.Method,
                Cell(e.Path + e.Query),
                e.StatusCode.ToString(CultureInfo.InvariantCulture),
                e.DurationMs.ToString("0.0", pt),
                e.ClientIp,
                Cell(e.ClientId),
                Cell(e.Device),
                Cell(e.Origin),
                e.Decision ?? "",
                e.FraudProbability?.ToString("0.000000", pt) ?? "",
                Cell(e.Reasons is null ? null : string.Join(" | ", e.Reasons)),
                Cell(e.Errors is null ? null : string.Join(" | ", e.Errors)),
                Cell(e.RequestBody?.ReplaceLineEndings(" "))));
        }

        // BOM no início: faz o Excel reconhecer o UTF-8 (acentos) sem configuração.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }
}

/// <summary>Descobre, a partir da requisição, de onde e de que tipo de aparelho ela veio.</summary>
public static class ClientInfo
{
    /// <summary>
    /// IP de quem chamou. Hospedada na internet, a API costuma ficar atrás de um proxy,
    /// que informa o IP original no cabeçalho X-Forwarded-For. Atenção: esse cabeçalho
    /// pode ser forjado por quem chama direto; serve para monitorar, não para autenticar.
    /// </summary>
    public static string Ip(HttpContext context)
    {
        string forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',')[0].Trim();

        System.Net.IPAddress? address = context.Connection.RemoteIpAddress;
        if (address is null) return "desconhecido";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    private static string? Tool(string ua) =>
        ua.Contains("curl/", StringComparison.OrdinalIgnoreCase) ? "curl" :
        ua.Contains("PostmanRuntime", StringComparison.OrdinalIgnoreCase) ? "Postman" :
        ua.Contains("insomnia", StringComparison.OrdinalIgnoreCase) ? "Insomnia" :
        ua.Contains("python", StringComparison.OrdinalIgnoreCase) ? "Python" :
        null;

    /// <summary>Resumo legível do User-Agent, ex.: "Chrome · Android".</summary>
    public static string Device(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "desconhecido";
        string? tool = Tool(userAgent);
        if (tool is not null) return tool;

        string os =
            userAgent.Contains("Android") ? "Android" :
            userAgent.Contains("iPhone") ? "iPhone" :
            userAgent.Contains("iPad") ? "iPad" :
            userAgent.Contains("Windows") ? "Windows" :
            userAgent.Contains("Macintosh") ? "Mac" :
            userAgent.Contains("Linux") ? "Linux" : "outro sistema";

        string browser =
            userAgent.Contains("Edg/") ? "Edge" :
            userAgent.Contains("OPR/") ? "Opera" :
            userAgent.Contains("Firefox/") ? "Firefox" :
            userAgent.Contains("Chrome/") ? "Chrome" :
            userAgent.Contains("Safari/") ? "Safari" : "outro cliente";

        return $"{browser} · {os}";
    }

    /// <summary>De onde veio a chamada: uma das páginas da API, o Swagger ou um programa externo.</summary>
    public static string Origin(string? referer, string? userAgent)
    {
        if (!string.IsNullOrWhiteSpace(referer))
        {
            if (referer.Contains("/pagar.html")) return "Página de pagamento";
            if (referer.Contains("/swagger")) return "Swagger";
            if (referer.Contains("/painel.html")) return "Painel";
            if (referer.Contains("/monitor.html")) return "Monitor";
            return "Outro site";
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            if (Tool(userAgent) is not null) return "Ferramenta de API";
            if (userAgent.Contains("Mozilla/")) return "Navegador (acesso direto)";
        }
        return "Cliente de API";
    }
}
