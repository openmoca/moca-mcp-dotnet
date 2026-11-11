namespace OpenMoca.Mcp;

/// <summary>
/// Follows changes to library folders. Changes are gathered for a moment, then reported together, so copying a
/// package in gives one report, not one per file.
/// </summary>
internal sealed class LibraryWatcher : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly Timer _timer;
    private readonly Action<IReadOnlyCollection<string>> _report;

    public LibraryWatcher(IReadOnlyList<string> folders, Action<IReadOnlyCollection<string>> report)
    {
        _report = report;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        foreach (var folder in folders.Where(Directory.Exists).Select(Path.GetFullPath))
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += (_, e) => Note(e.FullPath);
            watcher.Changed += (_, e) => Note(e.FullPath);
            watcher.Deleted += (_, e) => Note(e.FullPath);
            watcher.Renamed += (_, e) => { Note(e.OldFullPath); Note(e.FullPath); };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    private void Note(string path)
    {
        lock (_lock)
        {
            _changed.Add(path);
            _timer.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        string[] paths;
        lock (_lock)
        {
            paths = [.. _changed];
            _changed.Clear();
        }

        _report(paths);
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.Dispose();
        }

        _timer.Dispose();
    }
}
