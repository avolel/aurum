using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aurum.App.Infrastructure.Data;

/// <summary>
/// Builds an <see cref="AurumDbContext"/> for the <c>dotnet ef</c> tooling, reading the connection
/// string from the environment or from <c>.env</c>.
/// </summary>
/// <remarks>
/// <b>Design time only.</b> EF prefers this over building the host, so it never runs in production.
/// It exists because <c>dotnet ef</c> on the host never sees <c>.env</c>, which only Compose reads.
/// </remarks>
public sealed class AurumDbContextFactory : IDesignTimeDbContextFactory<AurumDbContext>
{
    private const string ConnectionKey = "ConnectionStrings__Aurum";

    /// <summary>
    /// Host-side override for <see cref="ConnectionKey"/>, read from <c>.env</c>.
    /// </summary>
    /// <remarks>
    /// The compose string says <c>Host=postgres</c>, which fails DNS on the host. Put a
    /// <c>Host=localhost</c> form here for commands that connect. No container reads it.
    /// </remarks>
    private const string DesignTimeConnectionKey = "AURUM_DESIGN_CONNECTION";

    public AurumDbContext CreateDbContext(string[] args)
    {
        var connectionString = ResolveConnectionString();

        var options = new DbContextOptionsBuilder<AurumDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        // The clock is required on purpose (D-7). Migrations never call SaveChanges anyway.
        return new AurumDbContext(options, TimeProvider.System);
    }

    private static string ResolveConnectionString()
    {
        // An explicit export wins, so CI keeps working.
        var exported = Environment.GetEnvironmentVariable(ConnectionKey);
        if (!string.IsNullOrWhiteSpace(exported))
        {
            return exported;
        }

        var env = LoadDotEnv();

        if (env.TryGetValue(DesignTimeConnectionKey, out var designTime)
            && !string.IsNullOrWhiteSpace(designTime))
        {
            return designTime;
        }

        if (env.TryGetValue(ConnectionKey, out var fromFile) && !string.IsNullOrWhiteSpace(fromFile))
        {
            return fromFile;
        }

        throw new InvalidOperationException(
            $"No connection string for design-time tooling. Set {ConnectionKey} in .env at the "
          + $"repository root, or export it. For commands that connect to the database from the "
          + $"host, set {DesignTimeConnectionKey} in .env to a Host=localhost form — .env's "
          + $"{ConnectionKey} says Host=postgres, which only resolves inside the compose network.");
    }

    /// <summary>
    /// Parses the nearest <c>.env</c>, searching upward from the current directory.
    /// </summary>
    /// <remarks>
    /// Upward, so it works from the repository root or a project directory. A missing file is not
    /// an error here; the caller reports it.
    /// </remarks>
    private static Dictionary<string, string> LoadDotEnv()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".env")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return values;
        }

        foreach (var raw in File.ReadAllLines(Path.Combine(directory.FullName, ".env")))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            // First '=' only: a connection string is full of them.
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].TrimEnd();
            var value = line[(separator + 1)..].Trim();

            // Strip one layer of matching quotes, as Compose does.
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return values;
    }
}
