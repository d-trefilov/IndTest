using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Numbering.Core;

namespace Numbering.Revit;

internal sealed class NumberingViewModel : INotifyPropertyChanged
{
    private readonly NumberingSettings _settings;
    private readonly ApplyAction _apply;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private CategoryDefinition _category;
    private string _prefix;
    private string _pattern;
    private string _search = "";
    private int _scopeIndex;
    private bool _applying;
    private string _documentTitle = "Нет активного проекта";
    private string _status = "Откройте проект Revit.";
    private string _templateError = "";
    private string _reportTitle = "";
    private string _reportDetails = "";
    private IReadOnlyList<TableRow> _visibleRows = Array.Empty<TableRow>();
    private IReadOnlyList<ElementData>? _elements;
    private Task<IReadOnlyList<ElementData>>? _spatialOrder;
    private long[] _selection = Array.Empty<long>();
    private Preview? _preview;
    private CancellationTokenSource? _calculation;

    public NumberingViewModel(NumberingSettings settings)
    {
        _settings = settings;
        _category = Categories.FirstOrDefault(item => item.Key == settings.Category) ?? Categories[0];
        var preset = settings.ForCategory(_category);
        _prefix = preset.Prefix!;
        _pattern = preset.Template!;
        _scopeIndex = settings.SelectedOnly ? 1 : 0;
        _apply = new ApplyAction(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? CategoryChanged;
    public event Action? SettingsChanged;
    public event Action<Preview>? ApplyRequested;

    public IReadOnlyList<CategoryDefinition> Categories => CategoryDefinition.All;

    public CategoryDefinition Category
    {
        get => _category;
        set
        {
            if (value == null || value == _category)
                return;

            BeginLoad(DocumentTitle, "Загрузка элементов…");
            _category = value;
            var preset = _settings.ForCategory(value);
            _prefix = preset.Prefix!;
            _pattern = preset.Template!;
            Changed(nameof(Category));
            Changed(nameof(Prefix));
            Changed(nameof(Pattern));
            TemplateError = "";
            SavePreset();
            CategoryChanged?.Invoke();
        }
    }

    public string Prefix
    {
        get => _prefix;
        set
        {
            if (Set(ref _prefix, value))
            {
                SavePreset();
                Recalculate();
            }
        }
    }

    public string Pattern
    {
        get => _pattern;
        set
        {
            if (Set(ref _pattern, value))
            {
                SavePreset();
                Recalculate();
            }
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
                Recalculate();
        }
    }

    public int ScopeIndex
    {
        get => _scopeIndex;
        set
        {
            if ((value == 0 || value == 1) && Set(ref _scopeIndex, value))
            {
                _settings.SelectedOnly = value == 1;
                SettingsChanged?.Invoke();
                Recalculate();
            }
        }
    }

    public bool IsApplying
    {
        get => _applying;
        set
        {
            if (Set(ref _applying, value))
                _apply.Refresh();
        }
    }

    public string DocumentTitle
    {
        get => _documentTitle;
        private set => Set(ref _documentTitle, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string TemplateError
    {
        get => _templateError;
        private set => Set(ref _templateError, value);
    }

    public string ReportTitle
    {
        get => _reportTitle;
        private set => Set(ref _reportTitle, value);
    }

    public string ReportDetails
    {
        get => _reportDetails;
        private set => Set(ref _reportDetails, value);
    }

    public IReadOnlyList<TableRow> VisibleRows
    {
        get => _visibleRows;
        private set => Set(ref _visibleRows, value);
    }

    public ICommand ApplyCommand => _apply;

    public void BeginLoad(string? documentTitle, string status)
    {
        _elements = null;
        _spatialOrder = null;
        InvalidatePreview();
        VisibleRows = Array.Empty<TableRow>();
        DocumentTitle = documentTitle ?? "Нет активного проекта";
        Status = status;
    }

    public void SetElements(IReadOnlyList<ElementData> elements, long[] selection)
    {
        _elements = elements;
        _selection = selection;
        _spatialOrder = Task.Run(() => SpatialOrder.Sort(elements));
        Recalculate();
    }

    public void SetSelection(long[] selection)
    {
        _selection = selection;
        if (ScopeIndex == 1)
            Recalculate();
    }

    public void SetReport(string title, string details)
    {
        ReportDetails = details;
        ReportTitle = title;
    }

    public void InvalidatePreview()
    {
        _calculation?.Cancel();
        _calculation?.Dispose();
        _calculation = null;
        _preview = null;
        _apply.Refresh();
    }

    private void SavePreset()
    {
        _settings.Category = Category.Key;
        if (NumberTemplate.TryParse(Pattern, out _, out _))
        {
            _settings.Presets ??= new Dictionary<string, NumberingPreset>();
            _settings.Presets[Category.Key] = new NumberingPreset { Prefix = Prefix, Template = Pattern };
        }

        SettingsChanged?.Invoke();
    }

    private void Recalculate()
    {
        InvalidatePreview();
        NumberTemplate.TryParse(Pattern, out var template, out var error);
        TemplateError = error ?? "";
        if (_elements == null || _spatialOrder == null)
            return;

        // Removing stale values immediately is important when the template is invalid.
        if (template == null && VisibleRows.Count > 0)
            VisibleRows = VisibleRows.Select(row => new TableRow(row.Element, null)).ToArray();

        Status = "Обновление предпросмотра…";
        _calculation = new CancellationTokenSource();
        _ = CalculateAsync(_elements, _spatialOrder, template, Prefix, Search,
            ScopeIndex == 1 ? _selection : null, _calculation.Token);
    }

    private async Task CalculateAsync(
        IReadOnlyList<ElementData> elements, Task<IReadOnlyList<ElementData>> spatialOrder,
        NumberTemplate? template, string prefix, string query, long[]? selection, CancellationToken token)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var order = await spatialOrder.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var preview = await Task.Run(() =>
            {
                var visible = elements.Where(row => ElementSearch.Matches(row, query)).ToArray();
                token.ThrowIfCancellationRequested();
                var scope = NumberingPlanner.GetScope(elements, visible, selection);
                IReadOnlyDictionary<long, NumberProposal> proposals = template == null
                    ? new Dictionary<long, NumberProposal>()
                    : NumberingPlanner.Create(elements, order, scope, template, prefix);

                var table = visible.Select(row =>
                {
                    proposals.TryGetValue(row.Id, out var proposal);
                    return new TableRow(row, proposal);
                }).ToArray();
                int visibleSelected = selection == null ? 0
                    : NumberingPlanner.GetScope(visible, visible, selection).Count;
                int hidden = selection == null ? 0 : scope.Count - visibleSelected;
                return new Preview(elements, table, proposals, selection, scope.Count, hidden);
            }, token).ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() => Publish(preview, template != null),
                DispatcherPriority.Normal, token);
            if (elements.Count >= 10000)
                AddinLog.Write($"Предпросмотр: {elements.Count} элементов, {clock.Elapsed.TotalMilliseconds:F1} мс.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
            {
                AddinLog.Write("Расчёт предпросмотра", error);
                if (!_dispatcher.HasShutdownStarted)
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                            Status = "Не удалось рассчитать номера. Подробности в журнале.";
                    });
            }
        }
    }

    private void Publish(Preview preview, bool templateValid)
    {
        _preview = preview;
        VisibleRows = preview.Table;
        Status = $"В таблице {preview.Table.Count} из {preview.Elements.Count}. В охвате: {preview.ScopeCount}."
            + (preview.Hidden > 0 ? $" Скрыто поиском: {preview.Hidden}." : "")
            + (templateValid ? $" К записи: {preview.ReadyCount}. Конфликты: {preview.ConflictCount}. "
                + $"Пропуски: {preview.SkippedCount}." : "");
        _apply.Refresh();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        Changed(name);
        return true;
    }

    private void Changed(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class ApplyAction : ICommand
    {
        private readonly NumberingViewModel _owner;

        public ApplyAction(NumberingViewModel owner) => _owner = owner;

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => !_owner.IsApplying && _owner._preview?.ReadyCount > 0;

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                _owner.IsApplying = true;
                _owner.Status = "Подготовка к записи…";
                _owner.SetReport("", "");
                _owner.ApplyRequested?.Invoke(_owner._preview!);
            }
        }

        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class TableRow
{
    public TableRow(ElementData element, NumberProposal? proposal)
    {
        Element = element;
        Proposal = proposal;
    }

    public ElementData Element { get; }
    public NumberProposal? Proposal { get; }
    public bool HasIssue => Proposal?.Status is ProposalStatus.Conflict or ProposalStatus.Skipped;

    public string Status => Proposal?.Status switch
    {
        ProposalStatus.Ready => "К записи",
        ProposalStatus.Unchanged => "Без изменений",
        _ => Proposal?.Reason ?? ""
    };
}

internal sealed class Preview
{
    public Preview(IReadOnlyList<ElementData> elements, IReadOnlyList<TableRow> table,
        IReadOnlyDictionary<long, NumberProposal> proposals, long[]? selection, int scopeCount, int hidden)
    {
        Elements = elements;
        Table = table;
        Proposals = proposals;
        Selection = selection;
        ScopeCount = scopeCount;
        Hidden = hidden;
        ReadyCount = proposals.Values.Count(item => item.Status == ProposalStatus.Ready);
        ConflictCount = proposals.Values.Count(item => item.Status == ProposalStatus.Conflict);
        SkippedCount = proposals.Values.Count(item => item.Status == ProposalStatus.Skipped);
    }

    public IReadOnlyList<ElementData> Elements { get; }
    public IReadOnlyList<TableRow> Table { get; }
    public IReadOnlyDictionary<long, NumberProposal> Proposals { get; }
    public long[]? Selection { get; }
    public int ScopeCount { get; }
    public int Hidden { get; }
    public int ReadyCount { get; }
    public int ConflictCount { get; }
    public int SkippedCount { get; }
}
