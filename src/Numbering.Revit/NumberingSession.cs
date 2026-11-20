using System.Collections.Concurrent;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Numbering.Core;

namespace Numbering.Revit;

internal sealed class NumberingSession : IExternalEventHandler, IDisposable
{
    private readonly UIApplication _app;
    private readonly NumberingWindowHost _window;
    private readonly ExternalEvent _externalEvent;
    private readonly ConcurrentQueue<Action> _requests = new();
    private readonly Action _onClosed;

    private Document? _document;
    private string? _documentTitle;
    private CategoryDefinition _category;
    private IReadOnlyList<ElementData>? _loadedRows;
    private ModelReader? _reader;
    private Stopwatch? _loadTime;
    private bool _reload;
    private bool _readSelection;
    private bool _writing;
    private volatile bool _disposed;

    private NumberingWriter? _writer;

    public NumberingSession(UIApplication app, Action onClosed)
    {
        _app = app;
        _onClosed = onClosed;
        var settings = SettingsStore.Load(out var settingsError);
        _category = CategoryDefinition.All.FirstOrDefault(item => item.Key == settings.Category)
            ?? CategoryDefinition.All[0];

        _externalEvent = ExternalEvent.Create(this);
        try
        {
            _window = new NumberingWindowHost(settings, settingsError,
                category => Queue(() => ChangeCategory(category)),
                preview => Queue(() => BeginWrite(preview)));
        }
        catch
        {
            _externalEvent.Dispose();
            throw;
        }
    }

    public bool IsClosed => _window.IsClosed;
    public void Show() => _window.Activate();

    public void Close()
    {
        _window.Close();
        Dispose();
    }

    public string GetName() => "Применение нумерации элементов";

    public void Execute(UIApplication app)
    {
        if (_disposed || _window.IsClosed)
            return;

        try
        {
            ObserveDocument();
            while (!_disposed && !_window.IsClosed && _requests.TryDequeue(out var request))
                request();
        }
        catch (Exception error)
        {
            FailWrite(error);
        }
    }

    private void Queue(Action request)
    {
        if (_disposed)
            return;

        _requests.Enqueue(request);
        TrySignalRequests();
    }

    private bool TrySignalRequests()
    {
        try
        {
            var result = _externalEvent.Raise();
            if (result is ExternalEventRequest.Accepted or ExternalEventRequest.Pending)
                return true;

            AddinLog.Write($"Revit не принял запрос нумерации: {result}.");
        }
        catch (Exception error)
        {
            if (!_disposed)
                AddinLog.Write("Сигнал Revit", error);
        }

        return false;
    }

    private void BeginWrite(Preview preview)
    {
        if (_writer != null)
            return;

        try
        {
            var document = ValidateWrite(preview, checkSelection: true);
            _writer = new NumberingWriter(document, _category, preview);
        }
        catch (StalePreviewException)
        {
            FinishWrite("Предпросмотр устарел. Проверьте данные и снова нажмите «Применить».");
        }
        catch (Exception error)
        {
            FailWrite(error);
        }
    }

    private Document ValidateWrite(Preview preview, bool checkSelection)
    {
        if (_document == null || !_document.IsValidObject || _reload || _reader != null
            || !ReferenceEquals(preview.Elements, _loadedRows)
            || !Equals(_document, _app.ActiveUIDocument?.Document))
            throw new StalePreviewException();

        if (_document.IsReadOnly)
            throw new ReadOnlyModelException();

        if (checkSelection && preview.Selection is long[] expected
            && !new HashSet<long>(expected).SetEquals(GetSelection(_app)))
            throw new StalePreviewException();

        return _document;
    }

    public void OnIdling(IdlingEventArgs args)
    {
        if (_window.IsClosed)
        {
            if (_window.StartupFailed)
                TaskDialog.Show("Нумерация элементов", "Окно не открылось. Подробности в журнале: " + AddinLog.Path);
            Dispose();
            return;
        }

        try
        {
            ObserveDocument();
            // Retry the signal if Revit could not accept it from the window thread.
            if (!_requests.IsEmpty && !_externalEvent.IsPending && !TrySignalRequests())
            {
                Close();
                TaskDialog.Show("Нумерация элементов",
                    "Revit не принял запрос. Откройте окно нумерации снова. Журнал: " + AddinLog.Path);
                return;
            }
            if (_writer != null)
            {
                ProcessWrite(_writer);
                if (_writer != null || _reload)
                    args.SetRaiseWithoutDelay();
                return;
            }

            if (_document == null)
                return;

            if (_reload)
            {
                _loadTime = Stopwatch.StartNew();
                _reader = new ModelReader(_document, _category);
                _reload = false;
                args.SetRaiseWithoutDelay();
                return;
            }

            if (_reader != null)
            {
                if (!_reader.ReadNextSlice())
                {
                    args.SetRaiseWithoutDelay();
                    return;
                }

                var rows = _reader.Rows;
                long[] selection = GetSelection(_app);
                _loadedRows = rows;
                var category = _category;
                _window.Post(vm =>
                {
                    if (vm.Category == category)
                        vm.SetElements(rows, selection);
                });
                AddinLog.Write($"Revit {_app.Application.VersionNumber}. Чтение {_category.Key}: "
                    + $"{rows.Count} элементов, всего {_loadTime!.Elapsed.TotalMilliseconds:F1} мс, "
                    + $"макс. порция {_reader.MaxSliceMs:F1} мс.");
                _reader = null;
                _readSelection = false;
            }
            else if (_readSelection)
            {
                _readSelection = false;
                long[] selection = GetSelection(_app);
                _window.Post(vm => vm.SetSelection(selection));
            }
        }
        catch (Exception error)
        {
            AddinLog.Write("Загрузка активной модели", error);
            _reader = null;
            _loadedRows = null;
            _reload = false;
            string? title = _documentTitle;
            _window.Post(vm =>
            {
                vm.BeginLoad(title, "Не удалось загрузить модель. Подробности в журнале.");
                vm.SetReport("Ошибка загрузки", "Журнал: " + AddinLog.Path);
            });
        }
    }

    private void ProcessWrite(NumberingWriter writer)
    {
        try
        {
            ValidateWrite(writer.Preview, checkSelection: false);
            bool prepared = writer.PrepareNextSlice();
            if (!prepared)
            {
                int count = writer.PreparedCount;
                int total = writer.TargetCount;
                _window.Post(vm =>
                {
                    vm.ApplyProgress = (int)(100L * count / total);
                    vm.Status = $"Подготовка к записи: {count} из {total}.";
                });
                return;
            }

            ValidateWrite(writer.Preview, checkSelection: true);
            _writing = true;
            ApplyReport? report;
            try
            {
                report = writer.TryWrite(
                    (count, total) => _window.Post(vm =>
                    {
                        int percent = (int)(100L * count / total);
                        vm.ApplyProgress = percent;
                        vm.Status = $"Запись номеров: {percent}% ({count} из {total}).";
                    }),
                    () => _window.Post(vm =>
                    {
                        vm.ApplyProgress = -1;
                        vm.Status = "Завершение попытки записи в Revit…";
                    }));
            }
            finally
            {
                _writing = false;
            }

            if (report != null)
                FinishWrite(report.Title, report.Details);
            else
            {
                writer.PrepareRetry();
                _window.Post(vm =>
                {
                    vm.ApplyProgress = 0;
                    vm.Status = "Подготовка к повторной записи…";
                });
            }
        }
        catch (StalePreviewException)
        {
            FinishWrite("Предпросмотр устарел. Проверьте данные и снова нажмите «Применить».");
        }
        catch (Exception error)
        {
            FailWrite(error);
        }
    }

    private static long[] GetSelection(UIApplication app) =>
        app.ActiveUIDocument?.Selection.GetElementIds().Select(ElementIds.Read).ToArray() ?? Array.Empty<long>();

    public void ObserveDocument()
    {
        var current = _app.ActiveUIDocument?.Document;
        if (current != null && (!current.IsValidObject || current.IsFamilyDocument))
            current = null;

        if (Equals(current, _document))
            return;

        bool interrupted = _writer != null;
        bool replacingDocument = _document != null;
        CancelWrite();
        _document = current;
        _documentTitle = current?.Title;
        if (interrupted)
            _window.Post(vm => vm.SetReport("Активный проект изменился. Запись отменена.", ""));
        else if (replacingDocument)
            _window.Post(vm => vm.SetReport("", ""));
        Reload();
    }

    private void Reload()
    {
        _reader = null;
        _loadedRows = null;
        _readSelection = false;
        _reload = _document != null;
        string? title = _documentTitle;
        string status = _reload ? "Загрузка элементов…" : "Откройте проект Revit.";
        _window.Post(vm => vm.BeginLoad(title, status));
    }

    private void ChangeCategory(CategoryDefinition category)
    {
        _category = category;
        _window.Post(vm => vm.SetReport("", ""));
        Reload();
    }

    public void OnDocumentClosed()
    {
        if (_document != null && !_document.IsValidObject)
            ObserveDocument();
    }

    public void OnDocumentChanged(Document document)
    {
        if (_writing || !Equals(_document, document))
            return;

        if (_writer != null)
            FinishWrite("Модель изменилась. Проверьте обновлённый предпросмотр.");
        else
            Reload();
    }

    public void OnSelectionChanged()
    {
        _readSelection = true;
        _window.Post(vm =>
        {
            if (vm.ScopeIndex == 1)
                vm.InvalidatePreview();
        });
    }

    private void FinishWrite(string title, string? details = null)
    {
        Reload();
        CancelWrite();
        _window.Post(vm => vm.SetReport(title, details ?? ""));
    }

    private void FailWrite(Exception error)
    {
        AddinLog.Write("Применение нумерации", error);
        string message = error is ReadOnlyModelException
            ? "Активная модель доступна только для чтения."
            : "Не удалось записать номера. Подробности в журнале.";
        FinishWrite(message, "Журнал: " + AddinLog.Path);
    }

    private void CancelWrite()
    {
        _writer = null;
        _window.Post(vm => vm.IsApplying = false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _externalEvent.Dispose();
        CancelWrite();
        _onClosed();
    }

    private sealed class ReadOnlyModelException : Exception
    {
    }
}
