namespace FraudDetector.Api;

/// <summary>Uma compra já avaliada, do jeito que o painel a exibe.</summary>
public record FeedEntry(
    long Id,
    DateTime ReceivedAtUtc,
    string Category,
    double Amount,
    string Currency,
    double AmountInModelCurrency,
    int Hour,
    double Age,
    double DistanceKm,
    string Gender,
    double FraudProbability,
    bool IsFraud,
    double Threshold);

/// <summary>O que o painel recebe a cada consulta.</summary>
public record FeedSnapshot(IReadOnlyList<FeedEntry> Entries, long Total, long Blocked, long LastId);

/// <summary>
/// Guarda na memória as últimas compras avaliadas, para o painel mostrar.
/// É o primeiro pedaço da API que tem ESTADO: até aqui, cada requisição era independente.
///
/// Como várias requisições chegam ao mesmo tempo (uma sala inteira pagando),
/// todo acesso à lista passa por um "lock": só uma thread mexe nela por vez.
/// Por estar na memória, o histórico some quando a API reinicia. Para uma demonstração, basta.
/// </summary>
public class TransactionFeed
{
    private const int Capacity = 200; // guarda só as mais recentes, para a memória não crescer sem limite

    private readonly object _lock = new();
    private readonly List<FeedEntry> _entries = [];
    private long _lastId;
    private long _total;
    private long _blocked;

    /// <summary>Registra uma compra e devolve a versão com o número (Id) atribuído.</summary>
    public FeedEntry Add(FeedEntry entry)
    {
        lock (_lock)
        {
            FeedEntry numbered = entry with { Id = ++_lastId };
            _entries.Add(numbered);
            if (_entries.Count > Capacity)
                _entries.RemoveAt(0);

            _total++;
            if (numbered.IsFraud)
                _blocked++;

            return numbered;
        }
    }

    /// <summary>Compras com Id maior que <paramref name="afterId"/> (o painel pede só as que ainda não viu).</summary>
    public FeedSnapshot Since(long afterId)
    {
        lock (_lock)
        {
            List<FeedEntry> news = _entries.Where(e => e.Id > afterId).ToList();
            return new FeedSnapshot(news, _total, _blocked, _lastId);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _lastId = 0;
            _total = 0;
            _blocked = 0;
        }
    }
}
