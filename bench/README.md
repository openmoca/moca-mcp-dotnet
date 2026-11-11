# Scale benchmark

`OpenMoca.Mcp.Scale` generates synthetic packages (unsigned, about 300 words a page) and measures what the server's
default limits rest on: the library scan, a package's first load, memory per loaded package, and search time.

```sh
dotnet run -c Release --project bench/OpenMoca.Mcp.Scale -- [work folder]
```

Results on 2026-11-04 (Apple M-series, 18 cores, .NET 10), after the .NET Reader began keeping term counts:

| Measure | 100 pages | 1,000 pages | 5,000 pages |
| --- | --- | --- | --- |
| Read and verify | 70 ms | 50 ms | 281 ms |
| First search (counts every page) | 34 ms | 200 ms | 366 ms |
| Each later search | 0.3 ms | 3.9 ms | 8.6 ms |
| Memory held | 5 MB | 27 MB | 145 MB (about 30 KB a page) |

Scanning a library reads only each `moca.json`: 1,000 packages in 16 ms.
