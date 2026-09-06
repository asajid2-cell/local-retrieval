using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexLocalRetrieval.Core.Services;

public static class SelectionRaceGuard
{
    public static string? Resolve(
        string? capturedId,
        long capturedRevision,
        long currentRevision,
        string? currentId,
        IEnumerable<string> availableIds)
    {
        if (currentRevision != capturedRevision) return currentId;
        if (capturedId is null) return null;
        return availableIds.Any(id => string.Equals(id, capturedId, StringComparison.OrdinalIgnoreCase))
            ? capturedId
            : null;
    }
}
