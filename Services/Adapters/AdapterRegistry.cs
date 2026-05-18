namespace Ripple.Services.Adapters;

/// <summary>
/// Immutable in-memory registry of loaded adapters, keyed by canonical name
/// and alias. Constructed once at startup via <see cref="LoadDefault"/>
/// (Program.cs) — embedded adapters plus any YAMLs in the external
/// directory (<see cref="DefaultExternalDirectory"/>), where external
/// overrides embedded. <see cref="LoadEmbedded"/> (embedded-only) exists
/// for tests and is not the runtime entrypoint.
///
/// Lookup is case-insensitive on the shell family name, matching the
/// existing ConsoleManager.NormalizeShellFamily convention (bash, pwsh, cmd,
/// zsh, powershell, etc.).
/// </summary>
public sealed class AdapterRegistry
{
    private readonly Dictionary<string, Adapter> _byName;

    /// <summary>
    /// Process-wide default registry, initialized once at startup by
    /// Program.cs. Read by ConsoleWorker / ConsoleManager to look up the
    /// adapter for a shell without plumbing the registry through
    /// constructors. Null until Program.cs calls SetDefault.
    /// </summary>
    public static AdapterRegistry? Default { get; private set; }

    /// <summary>
    /// Load report that accompanied the <see cref="Default"/> registry.
    /// Captured alongside so the list_adapters MCP tool can surface
    /// parse errors, collisions, and external-over-embedded overrides
    /// without re-running the load. Null until Program.cs calls
    /// SetDefault.
    /// </summary>
    public static LoadReport? DefaultReport { get; private set; }

    public static void SetDefault(AdapterRegistry registry, LoadReport? report = null)
    {
        Default = registry;
        DefaultReport = report;
    }

    private AdapterRegistry(Dictionary<string, Adapter> byName)
    {
        _byName = byName;
    }

    /// <summary>
    /// Default external adapter directory: ~/.ripple/adapters. User-dropped
    /// YAMLs here override embedded adapters of the same name, so a user
    /// iterating on a local adapter doesn't need to rebuild ripple.
    /// </summary>
    public static string DefaultExternalDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ripple",
            "adapters");

    /// <summary>
    /// Load all adapters shipped with the ripple binary. Parse errors for
    /// individual adapters are surfaced via the returned LoadReport, not
    /// thrown.
    /// </summary>
    public static (AdapterRegistry Registry, LoadReport Report) LoadEmbedded()
        => LoadFrom(AdapterLoader.LoadEmbedded());

    /// <summary>
    /// Load embedded adapters plus any YAMLs found in the user-scoped
    /// external adapter directory. External adapters override embedded
    /// adapters of the same name (users can edit a local pwsh.yaml without
    /// forking the repo); name clashes among external adapters, or between
    /// external and embedded aliases, surface as collisions in LoadReport.
    /// </summary>
    public static (AdapterRegistry Registry, LoadReport Report) LoadDefault()
    {
        var embedded = AdapterLoader.LoadEmbedded();
        var external = AdapterLoader.LoadFromDirectory(DefaultExternalDirectory);
        return LoadFrom(embedded, external);
    }

    private static (AdapterRegistry Registry, LoadReport Report) LoadFrom(params AdapterLoader.LoadResult[] sources)
    {
        var byName = new Dictionary<string, Adapter>(StringComparer.OrdinalIgnoreCase);
        var loadedOrigins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collisions = new List<Collision>();
        var overrides = new List<string>();
        var parseErrors = new List<AdapterLoader.ParseError>();

        foreach (var source in sources)
        {
            parseErrors.AddRange(source.Errors);

            foreach (var loaded in source.Adapters)
            {
                var adapter = loaded.Adapter;
                var originLabel = loaded.Source == AdapterLoader.AdapterSource.External
                    ? $"external:{Path.GetFileName(loaded.Origin)}"
                    : $"embedded:{loaded.Origin}";

                // External adapters override embedded ones of the same name
                // even when the embedded one is already registered under
                // aliases — the user's intent is "replace the built-in".
                if (byName.TryGetValue(adapter.Name, out var existing))
                {
                    var existingOrigin = loadedOrigins.GetValueOrDefault(adapter.Name, "?");
                    if (loaded.Source == AdapterLoader.AdapterSource.External &&
                        existingOrigin.StartsWith("embedded:"))
                    {
                        RemoveAdapter(byName, loadedOrigins, existing);
                        overrides.Add($"external adapter '{adapter.Name}' from {loaded.Origin} overrode {existingOrigin}");
                    }
                    else
                    {
                        // A collision is user-actionable whenever an external
                        // YAML is involved on either side: the user dropped
                        // the duplicate in ~/.ripple/adapters and can fix it
                        // by renaming or removing. Pure embedded-vs-embedded
                        // collisions can only happen on a broken ripple
                        // build, so the user cannot fix them — keep those
                        // AI-facing via list_adapters only.
                        bool userActionable =
                            loaded.Source == AdapterLoader.AdapterSource.External ||
                            existingOrigin.StartsWith("external:");
                        collisions.Add(new Collision(userActionable,
                            $"adapter name '{adapter.Name}' from {originLabel} collides with existing registration ({existingOrigin})"));
                        continue;
                    }
                }

                byName[adapter.Name] = adapter;
                loadedOrigins[adapter.Name] = originLabel;

                // Stamp load provenance onto the adapter itself so the
                // list_adapters MCP tool (and any other consumer that
                // holds an Adapter reference) can surface where the
                // adapter came from without needing access to the
                // registry's private origin map.
                adapter.Source = loaded.Source == AdapterLoader.AdapterSource.External
                    ? "external" : "embedded";

                if (adapter.Aliases != null)
                {
                    foreach (var alias in adapter.Aliases)
                    {
                        if (byName.TryGetValue(alias, out var aliasExisting) && !ReferenceEquals(aliasExisting, adapter))
                        {
                            var existingOrigin2 = loadedOrigins.GetValueOrDefault(alias, "?");
                            bool userActionable =
                                loaded.Source == AdapterLoader.AdapterSource.External ||
                                existingOrigin2.StartsWith("external:");
                            collisions.Add(new Collision(userActionable,
                                $"alias '{alias}' for adapter '{adapter.Name}' ({originLabel}) collides with existing registration ({existingOrigin2})"));
                            continue;
                        }
                        byName[alias] = adapter;
                        loadedOrigins[alias] = originLabel;
                    }
                }
            }
        }

        var registry = new AdapterRegistry(byName);
        var distinctLoaded = byName.Values.Distinct()
            .Select(a => $"{a.Name}({loadedOrigins.GetValueOrDefault(a.Name, "?").Split(':')[0]})")
            .ToList();
        var report = new LoadReport(
            Loaded: distinctLoaded,
            ParseErrors: parseErrors,
            Collisions: collisions,
            Overrides: overrides);

        return (registry, report);
    }

    private static void RemoveAdapter(
        Dictionary<string, Adapter> byName,
        Dictionary<string, string> origins,
        Adapter adapter)
    {
        var keysToRemove = byName.Where(kv => ReferenceEquals(kv.Value, adapter)).Select(kv => kv.Key).ToList();
        foreach (var key in keysToRemove)
        {
            byName.Remove(key);
            origins.Remove(key);
        }
    }

    /// <summary>
    /// Look up an adapter by shell family name or alias. Returns null if
    /// no adapter matches. Name comparison is case-insensitive.
    /// </summary>
    public Adapter? Find(string name)
        => _byName.TryGetValue(name, out var adapter) ? adapter : null;

    public IReadOnlyCollection<Adapter> All => _byName.Values.Distinct().ToList();

    public int Count => _byName.Values.Distinct().Count();

    /// <summary>
    /// A name- or alias-level collision surfaced during load. <see cref="IsUserActionable"/>
    /// is true when a file in the user's external adapter directory is
    /// involved on at least one side of the clash — the user can fix it
    /// by renaming or removing that YAML. Pure embedded-vs-embedded
    /// collisions cannot happen in a correctly shipped ripple build, so
    /// those are flagged false and kept off the silent-mode stderr.
    /// </summary>
    public record Collision(bool IsUserActionable, string Message);

    public record LoadReport(
        IReadOnlyList<string> Loaded,
        IReadOnlyList<AdapterLoader.ParseError> ParseErrors,
        IReadOnlyList<Collision> Collisions,
        IReadOnlyList<string> Overrides)
    {
        public bool HasErrors => ParseErrors.Count > 0 || Collisions.Count > 0;

        /// <summary>
        /// True when at least one parse error or collision involves an
        /// external YAML (the user's own ~/.ripple/adapters file).
        /// Those are the issues the user can act on, so silent-mode
        /// (MCP server / worker) emits only this subset on stderr —
        /// embedded-only failures are ripple bugs the user cannot fix,
        /// and firing them at every AI prompt is noise with no path to
        /// resolution. The AI can still inspect everything via the
        /// list_adapters MCP tool.
        /// </summary>
        public bool HasUserActionableIssues =>
            ParseErrors.Any(e => e.Source == AdapterLoader.AdapterSource.External)
            || Collisions.Any(c => c.IsUserActionable);

        public string Summary()
        {
            var parts = new List<string>
            {
                $"{Loaded.Count} loaded ({string.Join(", ", Loaded)})"
            };
            if (Overrides.Count > 0)
                parts.Add($"{Overrides.Count} override(s): {string.Join("; ", Overrides)}");
            if (ParseErrors.Count > 0)
                parts.Add($"{ParseErrors.Count} parse error(s): {string.Join("; ", ParseErrors.Select(e => $"{e.Resource}: {e.Error}"))}");
            if (Collisions.Count > 0)
                parts.Add($"{Collisions.Count} collision(s): {string.Join("; ", Collisions.Select(c => c.Message))}");
            return string.Join(" | ", parts);
        }

        /// <summary>
        /// Compact user-actionable summary, used by silent-mode stderr
        /// when at least one external-involved issue fired. Leaves out
        /// the happy-path "N loaded" roll-up and the noise-level
        /// embedded-only issues.
        /// </summary>
        public string UserActionableSummary()
        {
            var parts = new List<string>();
            var userParseErrors = ParseErrors
                .Where(e => e.Source == AdapterLoader.AdapterSource.External)
                .ToList();
            var userCollisions = Collisions.Where(c => c.IsUserActionable).ToList();
            if (userParseErrors.Count > 0)
                parts.Add($"{userParseErrors.Count} external parse error(s): {string.Join("; ", userParseErrors.Select(e => $"{e.Resource}: {e.Error}"))}");
            if (userCollisions.Count > 0)
                parts.Add($"{userCollisions.Count} external collision(s): {string.Join("; ", userCollisions.Select(c => c.Message))}");
            return string.Join(" | ", parts);
        }
    }
}
