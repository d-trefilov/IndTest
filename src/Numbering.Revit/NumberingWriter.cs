using System.Diagnostics;
using Autodesk.Revit.DB;
using Numbering.Core;

namespace Numbering.Revit;

internal sealed class NumberingWriter
{
    private static readonly TimeSpan SliceTime = TimeSpan.FromMilliseconds(12);
    private readonly Document _document;
    private readonly Preview _preview;
    private readonly CategoryDefinition _category;
    private readonly bool _workshared;
    private readonly ElementData[] _targets;
    private readonly Dictionary<long, Parameter> _parameters = new();
    private readonly Dictionary<long, string> _rejected = new();
    private readonly HashSet<string> _warnings = new();
    private readonly Stopwatch _totalTime = Stopwatch.StartNew();
    private int _next;

    public NumberingWriter(Document document, CategoryDefinition category, Preview preview)
    {
        _document = document;
        _preview = preview;
        _category = category;
        _workshared = document.IsWorkshared;
        _targets = preview.Elements.Where(element =>
            preview.Proposals.TryGetValue(element.Id, out var proposal) && proposal.Status == ProposalStatus.Ready).ToArray();
    }

    public bool PrepareNextSlice()
    {
        var clock = Stopwatch.StartNew();
        while (_next < _targets.Length)
        {
            Prepare(_targets[_next++]);
            if (clock.Elapsed >= SliceTime)
                break;
        }

        return _next == _targets.Length;
    }

    private void Prepare(ElementData snapshot)
    {
        if (_rejected.ContainsKey(snapshot.Id))
            return;

        ElementId id = ElementIds.Create(snapshot.Id);
        var element = _document.GetElement(id);
        if (element == null || element.Category == null || ElementIds.Read(element.Category.Id) != (long)_category.Category)
            throw new StalePreviewException();

        var parameter = element.get_Parameter(_category.Parameter);
        if (parameter?.StorageType == StorageType.String && (parameter.AsString() ?? "") != snapshot.CurrentValue)
            throw new StalePreviewException();

        string? issue = ModelReader.ParameterIssue(parameter);
        if (issue == null && _workshared
            && WorksharingUtils.GetCheckoutStatus(_document, id) == CheckoutStatus.OwnedByOtherUser)
            issue = "Элемент занят другим пользователем.";

        if (issue != null)
            _rejected.Add(snapshot.Id, issue);
        else
            _parameters.Add(snapshot.Id, parameter!);
    }

    public void PrepareRetry()
    {
        _parameters.Clear();
        _next = 0;
    }

    // A failed attempt is rolled back before the next Idling callback. Only a successful
    // commit contributes to Undo, even when recoverable failures require another attempt.
    public ApplyReport? TryWrite()
    {
        var proposals = _rejected.Count == 0
            ? _preview.Proposals
            : ConflictChecker.Check(_preview.Elements, _preview.Proposals, _rejected);
        var writes = proposals.Where(pair => pair.Value.Status == ProposalStatus.Ready).ToArray();
        if (writes.Length == 0)
            return CreateReport(proposals, 0);

        var preprocessor = new NumberingFailures(writes.Select(pair => pair.Key), _warnings);
        using var transaction = new Transaction(_document, "Нумерация элементов");
        if (transaction.Start() != TransactionStatus.Started)
            throw new InvalidOperationException("Не удалось начать транзакцию.");

        var options = transaction.GetFailureHandlingOptions();
        options.SetClearAfterRollback(true);
        options.SetForcedModalHandling(true);
        options.SetFailuresPreprocessor(preprocessor);
        transaction.SetFailureHandlingOptions(options);

        var clock = Stopwatch.StartNew();
        int rejectedBefore = _rejected.Count;
        foreach (var pair in writes)
        {
            try
            {
                if (!_parameters[pair.Key].Set(pair.Value.Value!))
                    _rejected.Add(pair.Key, "Revit не принял новое значение параметра.");
            }
            catch (Exception error) when (IsElementWriteError(error))
            {
                _rejected.Add(pair.Key, "Revit отклонил запись параметра.");
                AddinLog.Write($"Запись ID {pair.Key}", error);
            }
        }

        double setMs = clock.Elapsed.TotalMilliseconds;
        if (_rejected.Count != rejectedBefore)
        {
            if (transaction.RollBack() != TransactionStatus.RolledBack)
                throw new InvalidOperationException("Не удалось откатить попытку записи.");

            return null;
        }

        clock.Restart();
        TransactionStatus status = transaction.Commit();
        double commitMs = clock.Elapsed.TotalMilliseconds;
        AddinLog.Write($"Запись: {writes.Length} элементов, Set {setMs:F1} мс, Commit {commitMs:F1} мс, итог {status}.");

        if (status == TransactionStatus.Committed)
            return CreateReport(proposals, writes.Length);

        if (status == TransactionStatus.RolledBack && !preprocessor.HasUnmappedError && preprocessor.Errors.Count > 0)
        {
            foreach (var failure in preprocessor.Errors)
                _rejected[failure.Key] = failure.Value;
            return null;
        }

        return CreateReport(proposals, 0,
            "Revit отклонил транзакцию целиком. Подробности в журнале.");
    }

    private static bool IsElementWriteError(Exception error) =>
        error is Autodesk.Revit.Exceptions.ArgumentException
            or Autodesk.Revit.Exceptions.InvalidOperationException
            or ArgumentException
            or InvalidOperationException
        && error is not Autodesk.Revit.Exceptions.RegenerationFailedException;

    private ApplyReport CreateReport(
        IReadOnlyDictionary<long, NumberProposal> proposals, int written, string? error = null)
    {
        int unchanged = proposals.Values.Count(item => item.Status == ProposalStatus.Unchanged);
        var skips = proposals.Where(pair => pair.Value.Status is ProposalStatus.Skipped or ProposalStatus.Conflict)
            .OrderBy(pair => pair.Key)
            .Select(pair => $"ID {pair.Key}: {pair.Value.Reason}").ToArray();

        var details = new List<string>();
        if (error != null)
            details.Add(error);
        details.AddRange(skips);
        details.AddRange(_warnings.Select(warning => "Предупреждение: " + warning));
        details.Add("Журнал: " + AddinLog.Path);

        string title = $"Записано: {written}. Без изменений: {unchanged}. "
            + $"Пропущено: {skips.Length}. Предупреждений: {_warnings.Count}.";
        if (error != null)
            title = "Операция отменена. " + title;

        AddinLog.Write($"{title} Всего {_totalTime.Elapsed.TotalMilliseconds:F1} мс."
            + (details.Count > 1 ? Environment.NewLine + string.Join(Environment.NewLine, details) : ""));
        return new ApplyReport(title, string.Join(Environment.NewLine, details));
    }
}

internal sealed class NumberingFailures : IFailuresPreprocessor
{
    private readonly HashSet<long> _writeIds;
    private readonly HashSet<string> _warnings;
    private readonly Dictionary<long, string> _errors = new();

    public NumberingFailures(IEnumerable<long> writeIds, HashSet<string> warnings)
    {
        _writeIds = new HashSet<long>(writeIds);
        _warnings = warnings;
    }

    public IReadOnlyDictionary<long, string> Errors => _errors;
    public bool HasUnmappedError { get; private set; }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        bool rollback = false;
        foreach (var failure in accessor.GetFailureMessages())
        {
            var ids = failure.GetFailingElementIds().Select(ElementIds.Read).ToArray();
            string description = failure.GetDescriptionText();
            string detail = ids.Length == 0 ? description : $"ID {string.Join(", ", ids)}: {description}";

            if (failure.GetSeverity() == FailureSeverity.Warning)
            {
                _warnings.Add(detail);
                accessor.DeleteWarning(failure);
                continue;
            }

            rollback = true;
            AddinLog.Write("Ошибка транзакции Revit: " + detail);

            var affected = ids.Where(_writeIds.Contains).ToArray();
            if (affected.Length == 0 || failure.GetSeverity() == FailureSeverity.DocumentCorruption)
                HasUnmappedError = true;
            else
                foreach (long id in affected)
                    _errors[id] = description;
        }

        return rollback ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}

internal sealed class StalePreviewException : Exception
{
}

internal sealed class ApplyReport
{
    public ApplyReport(string title, string details)
    {
        Title = title;
        Details = details;
    }

    public string Title { get; }
    public string Details { get; }
}
