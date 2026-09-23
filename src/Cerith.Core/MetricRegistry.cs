#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Cerith.Core;

public sealed class MetricRegistry
{
    private readonly ConcurrentDictionary<MetricId, MetricDescriptor> _descriptors = new();

    public int Count => _descriptors.Count;

    public bool TryRegister(MetricDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return _descriptors.TryAdd(descriptor.Id, descriptor);
    }

    public bool TryGet(MetricId id, out MetricDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(id);

        return _descriptors.TryGetValue(id, out descriptor);
    }

    public IReadOnlyCollection<MetricDescriptor> GetSnapshot() =>
        Array.AsReadOnly(_descriptors.Values.ToArray());
}
