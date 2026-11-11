namespace OpenMoca.Mcp;

/// <summary>One package to serve, and the sidecar index to search it through, if any (<c>path=sidecar</c>).</summary>
internal sealed record PackageEntry(string Package, string? Sidecar);

/// <summary>A structure overlay the host loads over the packages (Reader contract §10).</summary>
internal sealed record OverlayEntry(string Id, string Layer, string Path);

/// <summary>
/// The command line:
/// <c>moca-mcp &lt;packages...&gt; [--trust-root file] [--members dirs...] [--audience list] [--overlay layer=file...]</c>.
/// </summary>
internal sealed record CommandLine
{
    public const string Usage = """
        Usage: moca-mcp [packages...] [options]

        Serve MOCA packages to any MCP client over stdio.

        Arguments:
          packages                   package directories or .moca archives; use path=sidecar to bind a sidecar index

        Library:
          --library <folders...>     folders to find packages in: unpacked packages and .moca files, with their indexes
          --watch                    follow changes to the library folders
          --preload                  load packages when the server starts, not on first use
          --max-packages <n>         most packages a library may list (default 1000)
          --max-depth <n>            how deep to look in a library folder (default 3)
          --max-loaded-pages <n>     memory budget, in pages kept loaded at once (default 25000)

        Search by meaning:
          --models <folder>          where the standard embedding models are installed (default ~/.moca/models)

        Models:
          moca-mcp models list                 the standard models, and which are installed
          moca-mcp models add <model...>       download and check standard models (the only command that downloads)

        Options:
          --trust-root <file>        trust root for verifying package and review attestations
          --members <dirs...>        directories to search for member packages
          --audience <list>          comma-separated audiences this host may serve (default: no filter); content with
                                     no audience label is left out unless --allow-unlabelled is given
          --allow-unlabelled         with --audience, also serve content that has no audience label
          --include-excluded         also serve out-of-force, superseded and deprecated content, flagged as such
          --allow-unsigned-relations apply supersedes and conflictsWith from unsigned packages (only for a library
                                     of your own packages)
          --overlay <layer=file...>  structure overlays the host loads over the packages: application=<file.ttl> or
                                     organisation=<file.ttl>
          -V, --version              output the version number
          -h, --help                 display help for command
        """;

    public IReadOnlyList<PackageEntry> Packages { get; init; } = [];

    public string? TrustRoot { get; init; }

    public IReadOnlyList<string> Members { get; init; } = [];

    /// <summary>The host's audience filter; <c>null</c> serves every audience.</summary>
    public IReadOnlyList<string>? Audiences { get; init; }

    public IReadOnlyList<OverlayEntry> Overlays { get; init; } = [];

    /// <summary>Folders to find packages in.</summary>
    public IReadOnlyList<string> Library { get; init; } = [];

    public bool Watch { get; init; }

    public bool Preload { get; init; }

    /// <summary><c>--allow-unlabelled</c>: with an audience set, also serve content with no audience label.</summary>
    public bool AllowUnlabelled { get; init; }

    /// <summary><c>--include-excluded</c>: also serve content the default retrieval policy leaves out.</summary>
    public bool IncludeExcluded { get; init; }

    /// <summary><c>--allow-unsigned-relations</c>: apply relations from unsigned packages.</summary>
    public bool AllowUnsignedRelations { get; init; }

    public int MaxPackages { get; init; } = 1000;

    public int MaxDepth { get; init; } = 3;

    public int MaxLoadedPages { get; init; } = 25_000;

    /// <summary>The model store; <c>MOCA_MODELS</c>, else <c>~/.moca/models</c>.</summary>
    public string Models { get; init; } = DefaultModels;

    public static string DefaultModels =>
        Environment.GetEnvironmentVariable("MOCA_MODELS") is { Length: > 0 } configured ? configured : OpenMoca.Reader.Onnx.ModelStore.DefaultFolder;

    public bool Help { get; init; }

    public bool Version { get; init; }

    /// <summary>Parses the arguments. Throws <see cref="ArgumentException"/> with a message for the user.</summary>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var packages = new List<PackageEntry>();
        var members = new List<string>();
        var overlays = new List<OverlayEntry>();
        var library = new List<string>();
        bool watch = false, preload = false, allowUnlabelled = false, includeExcluded = false, allowUnsignedRelations = false;
        int maxPackages = 1000, maxDepth = 3, maxLoadedPages = 25_000;
        var models = DefaultModels;
        string? trustRoot = null;
        List<string>? audiences = null;

        // Variadic options (--members, --overlay) take values until the next option, as commander does.
        string? collecting = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    return new CommandLine { Help = true };
                case "-V" or "--version":
                    return new CommandLine { Version = true };
                case "--trust-root":
                    trustRoot = Value(args, ref i, arg);
                    collecting = null;
                    continue;
                case "--audience":
                    audiences = [.. Value(args, ref i, arg).Split(',').Select(a => a.Trim()).Where(a => a.Length > 0)];
                    collecting = null;
                    continue;
                case "--watch":
                    watch = true;
                    collecting = null;
                    continue;
                case "--allow-unlabelled":
                    allowUnlabelled = true;
                    collecting = null;
                    continue;
                case "--include-excluded":
                    includeExcluded = true;
                    collecting = null;
                    continue;
                case "--allow-unsigned-relations":
                    allowUnsignedRelations = true;
                    collecting = null;
                    continue;
                case "--preload":
                    preload = true;
                    collecting = null;
                    continue;
                case "--max-packages":
                    maxPackages = Number(Value(args, ref i, arg), arg);
                    collecting = null;
                    continue;
                case "--max-depth":
                    maxDepth = Number(Value(args, ref i, arg), arg);
                    collecting = null;
                    continue;
                case "--models":
                    models = Value(args, ref i, arg);
                    collecting = null;
                    continue;
                case "--max-loaded-pages":
                    maxLoadedPages = Number(Value(args, ref i, arg), arg);
                    collecting = null;
                    continue;
                case "--members" or "--overlay" or "--library":
                    collecting = arg;
                    if (i + 1 >= args.Count || args[i + 1].StartsWith('-'))
                    {
                        throw new ArgumentException($"option '{arg} <values...>' argument missing");
                    }

                    continue;
                case "--embedder":
                    throw new ArgumentException("--embedder is not supported: install a standard model with `moca-mcp models add`.");
            }

            if (arg.StartsWith('-'))
            {
                throw new ArgumentException($"unknown option '{arg}'");
            }

            if (collecting == "--members")
            {
                members.Add(arg);
            }
            else if (collecting == "--overlay")
            {
                overlays.Add(Overlay(arg, overlays.Count));
            }
            else if (collecting == "--library")
            {
                library.Add(arg);
            }
            else
            {
                var at = arg.IndexOf('=', StringComparison.Ordinal);
                packages.Add(at > 0 ? new PackageEntry(arg[..at], arg[(at + 1)..]) : new PackageEntry(arg, null));
            }
        }

        if (packages.Count == 0 && library.Count == 0)
        {
            throw new ArgumentException("name packages, or a --library folder");
        }

        if (watch && library.Count == 0)
        {
            throw new ArgumentException("--watch needs a --library folder");
        }

        return new CommandLine
        {
            Packages = packages,
            TrustRoot = trustRoot,
            Members = members,
            Audiences = audiences,
            Overlays = overlays,
            Library = library,
            Watch = watch,
            Preload = preload,
            AllowUnlabelled = allowUnlabelled,
            IncludeExcluded = includeExcluded,
            AllowUnsignedRelations = allowUnsignedRelations,
            MaxPackages = maxPackages,
            MaxDepth = maxDepth,
            MaxLoadedPages = maxLoadedPages,
            Models = models,
        };
    }

    private static string Value(IReadOnlyList<string> args, ref int i, string option)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith('-'))
        {
            throw new ArgumentException($"option '{option}' argument missing");
        }

        return args[++i];
    }

    private static int Number(string value, string option) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : throw new ArgumentException($"{option} takes a whole number above 0, not \"{value}\"");

    private static OverlayEntry Overlay(string value, int index)
    {
        var at = value.IndexOf('=', StringComparison.Ordinal);
        var layer = at > 0 ? value[..at] : "";
        if (layer is not ("application" or "organisation"))
        {
            throw new ArgumentException($"--overlay must be application=<file> or organisation=<file>, not \"{value}\"");
        }

        return new OverlayEntry($"{layer}-{index + 1}", layer, value[(at + 1)..]);
    }
}
