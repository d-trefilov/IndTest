namespace Numbering.Core;

public static class ConflictChecker
{
    public static IReadOnlyDictionary<long, NumberProposal> Check(
        IReadOnlyList<ElementData> categoryRows, IReadOnlyDictionary<long, NumberProposal> proposals,
        IReadOnlyDictionary<long, string>? rejected = null)
    {
        var result = proposals.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (rejected != null)
        {
            foreach (var rejection in rejected)
            {
                if (IsReady(rejection.Key))
                    result[rejection.Key] = new NumberProposal(
                        result[rejection.Key].Value, ProposalStatus.Skipped, rejection.Value);
            }
        }

        var duplicateTargets = result
            .Where(pair => pair.Value.Status is ProposalStatus.Ready or ProposalStatus.Unchanged)
            .GroupBy(pair => pair.Value.Value!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .ToArray();

        foreach (var group in duplicateTargets)
        {
            var ids = group.Select(pair => pair.Key).OrderBy(id => id).Take(2).ToArray();
            foreach (var pair in group.Where(pair => pair.Value.Status == ProposalStatus.Ready))
            {
                long otherId = ids[0] == pair.Key ? ids[1] : ids[0];
                Conflict(pair.Key, $"Такой же новый номер предложен для ID {otherId}.");
            }
        }

        var readyByNumber = result.Where(pair => pair.Value.Status == ProposalStatus.Ready)
            .ToDictionary(pair => pair.Value.Value!.Trim(), pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        var currentById = categoryRows.ToDictionary(row => row.Id, row => row.CurrentValue.Trim());
        var holders = new Queue<long>(categoryRows.Where(row => !IsReady(row.Id)).Select(row => row.Id));

        // A skipped row keeps its old number, possibly blocking another write in turn.
        while (holders.Count > 0)
        {
            long holderId = holders.Dequeue();
            string current = currentById[holderId];
            if (current.Length == 0
                || !readyByNumber.TryGetValue(current, out long candidateId)
                || candidateId == holderId
                || !IsReady(candidateId))
                continue;

            Conflict(candidateId, $"Номер занят элементом ID {holderId}.");
            holders.Enqueue(candidateId);
        }

        return result;

        bool IsReady(long id) => result.TryGetValue(id, out var proposal) && proposal.Status == ProposalStatus.Ready;

        void Conflict(long id, string reason) =>
            result[id] = new NumberProposal(result[id].Value, ProposalStatus.Conflict, reason);
    }
}
