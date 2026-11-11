# moca-mcp: the MOCA MCP server

<!-- mcp-name: io.github.openmoca/moca-mcp -->

The MCP server for [MOCA](https://openmoca.org) knowledge packages, in one
download with nothing else to install. Connect a `.moca` file to Claude
Desktop, Claude Code, VS Code, Cursor or any MCP client, and ask questions
about it with citations you can check.

It is built on the [.NET MOCA Reader](https://github.com/openmoca/moca-reader-dotnet#readme).
Every result is a
[citation record](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#7-citation-records):
the text plus its package, version, digest, signature status, reviews,
freshness and evidence.

## Install

**Download** the file for your computer from the
[latest release](https://github.com/openmoca/moca-mcp-dotnet/releases/latest)
and unzip it:

| Computer | File |
| --- | --- |
| Windows (Intel or AMD) | `moca-mcp-win-x64.zip` |
| Windows on Arm | `moca-mcp-win-arm64.zip` |
| Mac with Apple silicon | `moca-mcp-osx-arm64.zip` |
| Mac with an Intel processor | `moca-mcp-osx-x64.zip` |
| Linux (x64 or Arm) | `moca-mcp-linux-x64.zip`, `moca-mcp-linux-arm64.zip` |

It holds one program, `moca-mcp` (`moca-mcp.exe` on Windows). It needs no
.NET, Node.js or anything else installed.

**Or, with .NET 10:** `dotnet tool install -g OpenMoca.Mcp`.

### The first run: unsigned builds

The builds are not code-signed yet, so your computer warns you the first time.
Signing is planned.

- **macOS** says the developer cannot be verified. In Finder, right-click
  `moca-mcp`, choose **Open**, then **Open** again. Or, in Terminal:
  `xattr -d com.apple.quarantine /path/to/moca-mcp`.
- **Windows** SmartScreen says it protected your PC. Choose **More info**,
  then **Run anyway**.

Each release lists the SHA-256 of every file, so you can check what you
downloaded.

## Connect a package

**Claude Desktop** keeps its servers in `claude_desktop_config.json`. To open
it, go to **Settings**, then **Extensions** (under **Desktop app**). At the
bottom of that page, choose **Manage**, then **Edit Config** under
**Local MCP servers**. The file is in `~/Library/Application Support/Claude/`
on a Mac and `%APPDATA%\Claude\` on Windows.

Add the server, save, and quit and restart Claude. This serves every package
in a folder (see [A folder of packages](#a-folder-of-packages)):

```json
{
  "mcpServers": {
    "moca": {
      "command": "/path/to/moca-mcp",
      "args": ["--library", "/path/to/packages",
               "--trust-root", "/path/to/trust-root.json",
               "--audience", "public"]
    }
  }
}
```

- If the file has no `mcpServers` yet, add it as shown, inside the outer
  `{ }` and beside any settings already there, with a comma between them.
- To serve one package, put its path in place of `"--library", "/path/to/packages"`.
- Use full paths. On Windows, the command is `moca-mcp.exe`, and each `\` is
  written twice: `"C:\\Users\\you\\moca\\moca-mcp.exe"`.

Other clients take the same command and arguments. For Claude Code:

```sh
claude mcp add acme-support -- /path/to/moca-mcp /path/to/acme-support.moca --trust-root /path/to/trust-root.json
```

Then ask: "Using acme-support, how long do customers have to ask for a
refund? Cite the page."

ChatGPT and Claude.ai on the web call MCP servers from their own cloud, so
they cannot reach a server on your computer: they need a hosted MCP server.

## A folder of packages

Keep your packages in one folder, packed (`.moca`) or unpacked, and serve the
whole folder:

```sh
moca-mcp --library ~/moca --trust-root ~/moca/trust-root.json
```

- **Only the manifests are read at start**, so a folder of hundreds of
  packages starts at once. Each package is read and verified the first time a
  question needs it, then kept in memory for the session.
- **Ask about one package at a time.** The assistant lists the packages with
  `moca_list_packages`, then names the ones it wants in `moca_search`.
- **Indexes are found by name.** `acme.moca.idx`, or `acme.<model>.moca.idx`,
  beside `acme.moca` or the `acme` folder, is used for that package. Either can
  be a folder, or a Zip with `.zip` on the end, as downloaded.
- **Versions are handled for you.** When a package supersedes another, both
  load together, so the older one is always reported as superseded.
- **Composed packages find their members** in the same folder.
- `--watch` picks up packages you add, change or remove while it runs.

## Search by meaning

With a standard embedding model installed, packages whose index was built with
it are searched by words and meaning together (hybrid): a question finds the
page that answers it even when they share no words.

```sh
moca-mcp models list                       # the standard models, and which are installed
moca-mcp models add bge-small-en-v1.5      # download once, checked against its pinned SHA-256
```

| Model | Size | Best for |
| --- | --- | --- |
| `bge-small-en-v1.5` | 33 MB | English, a good default |
| `all-MiniLM-L6-v2` | 22 MB | English, the smallest and fastest |
| `nomic-embed-text-v1.5` | 131 MB | English, long pages |
| `multilingual-e5-small` | 117 MB | Many languages, French included |

Models go in `~/.moca/models` (or `--models`, or `MOCA_MODELS`) and load the
first time a search needs them. Build an index with the same model using
`moca-index build <package> -o <package>.moca.idx --model bge-small-en-v1.5`
and put it beside the package. A package whose model is not installed is
still searched by words, and `moca_list_packages` names the model to add.

## Usage

```sh
moca-mcp [package...] [--library folders...] [--trust-root file] [--audience public,internal] [options]
```

| Option | Does |
| --- | --- |
| `<package>` | A package folder or `.moca` file, loaded at start. Write `package=index` to search through a sidecar index |
| `--library <folders...>` | Folders of packages, loaded on first use |
| `--trust-root <file>` | Verifies publisher and review attestations. Without it, nothing is reported as signed or reviewed, and no package can supersede or contest another |
| `--audience <list>` | The audiences served, such as `public`. The assistant cannot widen it. Pages with no audience label are left out |
| `--allow-unlabelled` | With `--audience`, also serve pages that have no audience label |
| `--include-excluded` | Also serve out-of-force, superseded and deprecated content, flagged as such |
| `--allow-unsigned-relations` | Apply `supersedes` and `conflictsWith` from unsigned packages. Only for a library of your own packages |
| `--members <folders...>` | Where a composed package's members are, if not in the library |
| `--overlay <layer=file...>` | Structure overlays, as the `application` or `organisation` layer |
| `--watch` | Follow changes to the library folders |
| `--preload` | Load the library at start, not on first use |
| `--max-packages <n>` | Most packages a library lists (default 1,000) |
| `--max-depth <n>` | How deep to look in a library folder (default 3) |
| `--models <folder>` | Where the standard embedding models are installed (default `~/.moca/models`) |
| `--max-loaded-pages <n>` | Memory budget: pages kept loaded at once (default 25,000, about 750 MB). The least recently used package is unloaded beyond it |

Invalid packages are skipped and reported on standard error;
`moca_list_packages` says why.

## Tools and resources

| Tool | Returns |
| --- | --- |
| `moca_list_packages` | Every package the server can serve, loaded or not: id, version, title, description; for a loaded package its digest, signature, capabilities, page count and search mode |
| `moca_search` | Citation records, compact by default; `detail: "full"` for whole records. `packages` limits it to some packages; `mode` is `lexical`, `semantic` or `hybrid`. Out-of-force, superseded and deprecated content is left out unless the host starts the server with `--include-excluded`: the assistant cannot ask for it |
| `moca_structure` | Structure answers: `concept`, `requires`, `required_by`, `parts`, `narrower`, `broader`, `related`, `sequence` or `nodes`, each naming its layer |
| `moca_get_node` | One citation record with the page's full text |
| `moca_package_info` | One package's manifest, digest, signers, attestations, members, profiles and the Reader's findings: why it is, or is not, trusted |

Every tool is marked read-only, and no tool takes a file path: the assistant
can only choose among the packages you serve. Input is bounded and checked
before anything runs. Every page of a loaded package is also a resource at
`moca://node/<URL-encoded versioned reference>`. The audience filter and the
default retrieval policy apply to tools and resources alike.

Package text is returned as data inside citation records. Tool descriptions
tell the model to quote and cite it, not follow it
([Reader contract §12](https://github.com/openmoca/moca-spec/blob/main/spec/moca-reader-contract.md#12-handing-content-to-a-model)).

## Build on it

`OpenMoca.Mcp.Server` is the server as a .NET library, for your own host:

```csharp
var moca = await MocaMcpServer.CreateAsync(
    new MocaMcpOptions { Audiences = ["public"] },
    new FolderPackageSource(new FolderPackageSourceOptions { Folders = ["/srv/moca"] }));
var server = McpServer.Create(new StdioServerTransport("moca"), moca.ServerOptions);
moca.Attach(server);
await server.RunAsync();
```

- **Your own embedder:** `MocaMcpOptions.Embedders` takes any Reader
  `IEmbedder`, such as a hosted model through Microsoft.Extensions.AI.
- **Your own package source:** implement `IPackageSource` (list packages from
  their manifests, open one when it is needed, report changes). The server
  verifies every package it opens, wherever it came from.
- **Search hooks:** `MocaMcpOptions.SearchHooks` takes the Reader's ingress
  and egress hooks, which may narrow or reorder a search, never widen it.
- **Dependency injection:** `services.AddMocaMcp(options).WithPackageSource<MySource>()`,
  then `MocaMcpServerFactory`.
- **Logging:** any `ILogger`.

## Privacy Policy

`moca-mcp` collects nothing and sends nothing anywhere.

- **No network calls.** The server reads only the packages, library folders,
  trust root, overlays, sidecar indexes and installed models you name or set
  up. `moca-mcp models add` is the one command that downloads: the model files
  you ask for, from Hugging Face, checked against their pinned SHA-256. It never fetches
  anything a package names, and it has no telemetry, analytics or update
  check.
- **Your questions stay with you.** Searches and the nodes returned go only to
  the MCP client that asked, over stdio on your own machine. The server keeps
  no record of them.
- **Logs are local and about packages only.** It writes to standard error
  which packages it loaded, their digests, and any problems found in them,
  never the queries.

The AI assistant you connect it to has its own privacy terms: what it does
with the text it receives is governed by them, not by this server.

## Develop

Clone [moca-spec](https://github.com/openmoca/moca-spec) beside this repository
(or set `MOCA_SPEC_DIR`), then:

```sh
dotnet build moca-mcp-dotnet.slnx
dotnet test --project tests/OpenMoca.Mcp.Tests
```

**Expected answers.** `tests/parity` holds calls over moca-spec's examples
and the answers every build must give: tool definitions, results, resources
and refusals. The tests start the built server and compare. When a tool or a
parameter changes on purpose, add a case, record the answers, and read the
difference before committing it:

```sh
dotnet test --project tests/OpenMoca.Mcp.Tests -- --filter-method "*Record_expected_answers" --explicit only
```

**Scale.** `bench/` measures the library scan, loading, memory and search
time the default limits rest on; see [bench/README.md](bench/README.md).

The tool and resource definitions are in `src/OpenMoca.Mcp/definitions.json`.

## Release

Push a `vX.Y.Z` tag. The release workflow builds the six self-contained files,
attaches them with their SHA-256 sums to a GitHub release, and publishes the
`OpenMoca.Mcp` .NET tool and the `OpenMoca.Mcp.Server` library to NuGet.

## Licence

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE). "OpenMOCA" is a trade
mark; see the
[trade mark policy](https://github.com/openmoca/moca-spec/blob/main/TRADEMARKS.md).
