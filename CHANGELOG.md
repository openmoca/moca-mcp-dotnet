# Changelog

All notable changes to moca-mcp for .NET (`OpenMoca.Mcp`). The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.7.0] - 2026-11-15

### Added (the creator's terms: moca-spec package spec §4.14, Reader contract §15.4)

- The server applies the creator's terms to everything it returns: `moca_search` results, `moca_get_node`, the nodes
  `moca_structure` gives, and resources (listed and read). By default it treats the client as a third party's model
  with no conditions, as the contract asks of a server with no configuration. `--destination provider|host|device`
  and `--conditions noTraining,noRetention` say where the model runs and what its provider meets
  (`MocaMcpOptions.Terms`). A search that left results out says so in `termsNote`; a node left out says why.
- `moca_package_info` gives the package's terms as written, their problems and who stated them; full records carry
  each node's resolved terms, licence and copyright.

### Added (hardening)

- `--require-signature` (`MocaMcpOptions.RequireSignature`): serve only packages with a valid package attestation
  under the trust root. The server suggests it when a trust root is given without it.
- Bounded answers: a record's text is cut at 100,000 bytes (`MaxTextBytes`) and marked `truncated`; an answer stops
  adding records past 400,000 bytes (`MaxResponseBytes`) and says `truncated`. `moca_structure`'s `nodes` gives 20
  at a time, with `limit` (up to 100), `offset` and `nextOffset`. Resources are listed 100 at a time, with a cursor.
- A memory budget in bytes, `--max-loaded-mb` (default 1,000; `MocaMcpOptions.MaxLoadedBytes`), beside the page
  budget: each package counts its files plus about 30 KB a page. A call that names packages now stops loading at the
  budget too, apart from the first package it needs.
- Versioned ids: `moca_get_node` with an id that names no version gives the newest loaded version that may be
  served, by Semantic Versioning precedence, and lists the others in `otherVersions`. Before, it gave whichever
  version loaded first.

### Changed

- Built on the 0.7.0 .NET Reader (moca-spec 0.7.0): media descriptions,
  alternates and refined time ranges in citation records, binding by
  composition, and bounded work for compositions.
- Sidecar indexes beside a package are found as `<name>.mocai` and
  `<name>.<model>.mocai`, a Zip archive or a folder (package spec §12.1).
  The earlier names, `.moca.idx` and `.moca.idx.zip`, are still found, after
  the new one. When several are beside a package, the server tries each until one binds, so another package's
  index whose name matches is passed over.
- CI packs OpenMoca.Reader 0.7.0 from moca-reader-dotnet at a pinned commit
  until it is on NuGet, and pins moca-spec to its release-0.7.0 head. The expected answers follow the 0.7.0
  examples and Reader (credits, alternative labels, `license` in records, and 0.7.0 scores).

## [0.6.0] - 2026-11-11

### Added (Sigstore)

- Sigstore attestations verify offline against the trust root's
  `sigstore.trustedRoot`, through OpenMoca.Reader.Sigstore.

### Changed (reviews, moca-spec ADR-0017)

- Records carry `trust.reviewStatus` and each review's `packageVerified`, from
  the 0.6.0 Reader. The recorded answers gain these fields; nothing else
  changes.

### Changed (audience and relations, moca-spec ADR-0015 and ADR-0016)

- Every record the server hands out (search, node lookup, resource list and
  read) passes the audience rule and, unless the host opted in, the default
  retrieval policy. Under `--audience`, pages with no label are left out
  unless `--allow-unlabelled`; a malformed label is never served.
- `include_all` is no longer a `moca_search` argument: the assistant cannot ask
  for excluded content. The host starts the server with `--include-excluded`.
- `--allow-unsigned-relations` applies relations from unsigned packages.

### Changed

- Built on the 0.6.0 .NET Reader. `supersedes` and `conflictsWith` take effect
  only for packages signed by a trust-root key whose `ids` cover the target
  (package spec §7.2). The recorded answers now show the policy-corpus 2.0.0
  example as signed; what the searches return is unchanged.

## [0.5.0] - Not published

The first release.

### Added

- An MCP server over stdio for MOCA packages: `moca_list_packages`, `moca_search`,
  `moca_structure`, `moca_get_node` and the `moca://node/` resources, on the
  .NET MOCA Reader. Lexical search, through a sidecar index when one is bound.
- The same command line: packages and `package=sidecar`, `--trust-root`,
  `--members`, `--audience` and `--overlay`.
- Every tool annotated as read-only, idempotent and closed-world. Input is
  checked against each tool's schema before anything runs.
- Self-contained single-file builds for Windows, macOS and Linux (x64 and
  Arm64), unsigned at first, and a .NET tool, `OpenMoca.Mcp`.
- A library folder (`--library`): packed and unpacked packages found from
  their manifests alone, with sidecar indexes bound by name (folders, or Zips
  ending `.moca.idx.zip`); loaded and
  verified on first use and kept in memory; `--watch`, `--preload`, and limits
  (`--max-packages`, `--max-depth`, `--max-loaded-pages`). A package that
  supersedes a loaded one loads with it; a composed package finds its members
  in the library.
- `packages` on `moca_search` and `moca_structure`, to ask about some packages;
  `moca_list_packages` lists every package, loaded or not.
- `moca_package_info`: a package's manifest, signers, attestations, members and
  the Reader's findings.
- `OpenMoca.Mcp.Server`, the server as a library: `IPackageSource`,
  `MocaMcpOptions` (with search hooks), `MocaMcpServer`, and `AddMocaMcp` for
  dependency injection.
- Search by meaning: `mode` on `moca_search` (lexical, semantic, hybrid;
  hybrid by default where possible) with MOCA's four standard embedding
  models run locally, and `moca-mcp models list | add`. Vectors match
  `moca-index --model` exactly.
- `server.json` for the MCP Registry, as `io.github.openmoca/moca-mcp`, also
  packed in the NuGet package as `.mcp/server.json`.
- Expected answers in `tests/parity`, checked on every build, and a scale
  benchmark in `bench/`.
