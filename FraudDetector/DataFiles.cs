namespace FraudDetector;

/// <summary>Localiza os arquivos do projeto a partir da pasta do executável (bin/Debug/net10.0).</summary>
public static class DataFiles
{
    /// <summary>Procura DataSet/{fileName} subindo as pastas.</summary>
    public static string Find(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "DataSet", fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Não encontrei DataSet/{fileName}.");
    }

    /// <summary>Pasta da solução (onde está o FraudDetector.slnx).</summary>
    public static string SolutionDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FraudDetector.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>Caminho padrão do modelo treinado.</summary>
    public static string ModelPath() => Path.Combine(SolutionDirectory(), "Models", "fraud.model.json");
}
