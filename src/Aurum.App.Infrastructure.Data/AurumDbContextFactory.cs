using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aurum.App.Infrastructure.Data;

/// <summary>
/// Builds an <see cref="AurumDbContext"/> for the <c>dotnet ef</c> tooling, reading the connection
/// string from the environment or from <c>.env</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design time only.</b> EF looks for this type before it tries to build the application host,
/// so nothing here runs in production and <c>Program.cs</c> remains the only place the running API
/// resolves configuration.
/// </para>
/// <para>
/// It exists because <c>.env</c> is a Docker Compose convention, not a .NET one: Compose reads the
/// file and injects it as container environment, and <c>dotnet ef</c> on the host never sees it.
/// Before this, every migration command had to be preceded by an <c>export</c> of a value that was
/// already sitting in <c>.env</c> — and the failure when you forgot was a misleading "Unable to
/// resolve service for type 'DbContextOptions&lt;AurumDbContext&gt;'" rather than anything about a
/// connection string.
/// </para>
/// <para>
/// Because EF now uses this instead of the host, <c>--startup-project</c> is no longer needed and
/// <c>Program.cs</c>'s connection-string guard no longer fires during tooling.
/// </para>
/// </remarks>
public sealed class AurumDbContextFactory : IDesignTimeDbContextFactory<AurumDbContext>
{
    private const string ConnectionKey = "ConnectionStrings__Aurum";

    /// <summary>
    /// Host-side override for <see cref="ConnectionKey"/>, read from <c>.env</c>.
    /// </summary>
    /// <remarks>
    /// The compose connection string says <c>Host=postgres</c>, which resolves only inside the
    /// compose network. Commands that actually open a connection — <c>database update</c>,
    /// <c>dbcontext script</c> — run on the host, where that name does not resolve and the error is
    /// a DNS failure rather than anything pointing at this. Put a <c>Host=localhost</c> form under
    /// this key in <c>.env</c> and host-side tooling uses it; compose ignores it, because nothing
    /// in <c>docker-compose.yml</c> maps it into a container.
    /// </remarks>
    private const string DesignTimeConnectionKey = "AURUM_DESIGN_CONNECTION";

    public AurumDbContext CreateDbContext(string[] args)
    {
        var connectionString = ResolveConnectionString();

        var options = new DbContextOptionsBuilder<AurumDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        // The real clock. Migrations do not run SaveChanges, so ApplyAuditFields never fires here;
        // the parameter is required by the context precisely so no construction site can quietly
        // default it (D-7), and a design-time factory is not the place to make an exception.
        return new AurumDbContext(options, TimeProvider.System);
    }

    private static string ResolveConnectionString()
    {
        // An explicit export wins, so CI and any existing muscle memory keep working unchanged.
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
    /// Upward because the working directory depends on how the command was invoked — the repository
    /// root with <c>--project</c>, or the project directory after a <c>cd</c>. Both have to find the
    /// same file. A missing <c>.env</c> is not an error: an exported variable is still a valid way
    /// to run this, and the caller reports the combined failure.
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

            // First '=' only. A connection string is full of them —
            // "Host=postgres;Port=5432" is one value, not a key and three others.
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].TrimEnd();
            var value = line[(separator + 1)..].Trim();

            // Compose strips one layer of matching quotes; match that rather than inventing a
            // second dialect of the same file.
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
