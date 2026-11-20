using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace Numbering.Revit;

public sealed class App : IExternalApplication
{
    internal static NumberingSession? Session { get; set; }

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            var panel = application.CreateRibbonPanel("Нумерация элементов");
            var commandType = typeof(OpenNumberingCommand);
            panel.AddItem(new PushButtonData("OpenElementNumbering", "Нумерация\nэлементов",
                commandType.Assembly.Location, commandType.FullName!));
            application.Idling += OnIdling;
            application.ViewActivated += OnViewActivated;
            application.SelectionChanged += OnSelectionChanged;
            application.ControlledApplication.DocumentClosed += OnDocumentClosed;
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
            return Result.Succeeded;
        }
        catch (Exception error)
        {
            AddinLog.Write("Регистрация кнопки", error);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.Idling -= OnIdling;
        application.ViewActivated -= OnViewActivated;
        application.SelectionChanged -= OnSelectionChanged;
        application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
        application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        Session?.Close();
        return Result.Succeeded;
    }

    private static void OnIdling(object? sender, IdlingEventArgs args) => Session?.OnIdling(args);
    private static void OnViewActivated(object? sender, ViewActivatedEventArgs args) => Session?.ObserveDocument();
    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs args) => Session?.OnSelectionChanged();
    private static void OnDocumentClosed(object? sender, DocumentClosedEventArgs args) => Session?.OnDocumentClosed();
    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs args) =>
        Session?.OnDocumentChanged(args.GetDocument());
}

[Transaction(TransactionMode.Manual)]
public sealed class OpenNumberingCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            if (App.Session?.IsClosed == true)
                App.Session.Dispose();

            if (App.Session == null)
            {
                var session = new NumberingSession(commandData.Application, () => App.Session = null);
                App.Session = session;
                try
                {
                    session.ObserveDocument();
                    session.Show();
                }
                catch
                {
                    session.Close();
                    App.Session = null;
                    throw;
                }
            }
            else
            {
                App.Session.Show();
            }

            return Result.Succeeded;
        }
        catch (Exception error)
        {
            AddinLog.Write("Открытие окна", error);
            message = "Не удалось открыть окно нумерации. Подробности в журнале: " + AddinLog.Path;
            return Result.Failed;
        }
    }
}
