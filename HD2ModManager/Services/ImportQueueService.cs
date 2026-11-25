using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HD2ModCore.Ias;

namespace HD2ModManager.Services
{
    public enum ImportTaskStatus { Queued, Running, Done, Failed }

    public class ImportTaskItem
    {
        public string Path { get; set; } = string.Empty;
        public ImportTaskStatus Status { get; set; } = ImportTaskStatus.Queued;
        public string? Error { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        internal ImportTaskItem[]? DropBatch { get; set; }
    }

    public class ImportQueueService
    {
        private readonly ObservableCollection<ImportTaskItem> _tasks = new();
        public ReadOnlyObservableCollection<ImportTaskItem> Tasks { get; }

        public ImportQueueService()
        {
            Tasks = new ReadOnlyObservableCollection<ImportTaskItem>(_tasks);
        }

        public void Enqueue(params string[] paths)
        {
            var items = paths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => new ImportTaskItem { Path = path }).ToArray();
            if (items.Length == 0) return;
            items[0].DropBatch = items;
            foreach (var item in items)
            {
                _tasks.Add(item);
            }
        }

        internal async IAsyncEnumerable<(ImportTaskItem Item, IasStandardDropPackage? Package)> DequeueImportItemsAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            while (_tasks.FirstOrDefault(item => item.Status == ImportTaskStatus.Queued) is { } first)
            {
                var batch = first.DropBatch ?? [first];
                var paths = batch.Select(item => item.Path).ToArray();
                using var drop = await Task.Run(() => IasStandardDrop.InspectAsync(paths, ct), ct);
                first.DropBatch = null;
                var retained = drop.Items.Select(route => route.Index).ToHashSet();
                for (var i = 0; i < batch.Length; i++)
                {
                    if (!retained.Contains(i)) _tasks.Remove(batch[i]);
                }
                foreach (var route in drop.Items)
                {
                    var item = batch[route.Index];
                    item.Path = route.Path;
                    if (route.Error is { } error) MarkFailed(item, error);
                    else MarkRunning(item);
                    yield return (item, route.Package);
                }
            }
        }

        public void MarkRunning(ImportTaskItem item)
        {
            item.Status = ImportTaskStatus.Running;
        }

        public ImportTaskItem? DequeueNextQueued()
        {
            var item = _tasks.FirstOrDefault(t => t.Status == ImportTaskStatus.Queued);
            if (item != null)
            {
                MarkRunning(item);
            }

            return item;
        }

        public void MarkDone(ImportTaskItem item)
        {
            item.Status = ImportTaskStatus.Done;
            item.Error = null;
        }

        public void MarkFailed(ImportTaskItem item, string error)
        {
            item.Status = ImportTaskStatus.Failed;
            item.Error = error;
        }

        public int CountQueued => _tasks.Count(t => t.Status == ImportTaskStatus.Queued);
        public int CountRunning => _tasks.Count(t => t.Status == ImportTaskStatus.Running);
        public int CountFailed => _tasks.Count(t => t.Status == ImportTaskStatus.Failed);
        public int CountDone => _tasks.Count(t => t.Status == ImportTaskStatus.Done);
    }
}
