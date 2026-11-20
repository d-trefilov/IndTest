using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using Numbering.Core;

namespace Numbering.Revit;

[DataContract]
internal sealed class NumberingSettings
{
    public const string DefaultTemplate = "{Префикс}-{Уровень}-{N:000}";

    [DataMember(Name = "category")]
    public string? Category { get; set; }

    [DataMember(Name = "selectedOnly")]
    public bool SelectedOnly { get; set; }

    [DataMember(Name = "presets")]
    public Dictionary<string, NumberingPreset>? Presets { get; set; } = new();

    public NumberingPreset ForCategory(CategoryDefinition category)
    {
        if (Presets != null && Presets.TryGetValue(category.Key, out var preset)
            && preset?.Template != null && NumberTemplate.TryParse(preset.Template, out _, out _))
            return new NumberingPreset { Prefix = preset.Prefix ?? category.DefaultPrefix, Template = preset.Template };

        return new NumberingPreset { Prefix = category.DefaultPrefix, Template = DefaultTemplate };
    }
}

[DataContract]
internal sealed class NumberingPreset
{
    [DataMember(Name = "prefix")]
    public string? Prefix { get; set; }

    [DataMember(Name = "template")]
    public string? Template { get; set; }
}

internal static class SettingsStore
{
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElementNumbering");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    private static DataContractJsonSerializer CreateSerializer() =>
        new(typeof(NumberingSettings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

    public static NumberingSettings Load(out string? message)
    {
        message = null;
        try
        {
            using var stream = File.OpenRead(FilePath);
            return (NumberingSettings?)CreateSerializer().ReadObject(stream)
                ?? throw new SerializationException("Файл настроек не содержит объекта.");
        }
        catch (FileNotFoundException) { return new NumberingSettings(); }
        catch (DirectoryNotFoundException) { return new NumberingSettings(); }
        catch (Exception error) when (error is SerializationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            AddinLog.Write("Чтение настроек", error);
            message = "Настройки не прочитаны. Используются значения по умолчанию.";
            return new NumberingSettings();
        }
    }

    public static void Save(NumberingSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        string tempPath = Path.Combine(DirectoryPath, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = File.Create(tempPath))
                CreateSerializer().WriteObject(stream, settings);

            if (File.Exists(FilePath))
                File.Replace(tempPath, FilePath, null);
            else
                File.Move(tempPath, FilePath);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                AddinLog.Write("Очистка временного файла настроек", error);
            }
        }
    }
}

internal static class AddinLog
{
    private static readonly object Sync = new();
    public static string Path { get; } = System.IO.Path.Combine(SettingsStore.DirectoryPath, "numbering.log");

    public static void Write(string operation, Exception error) => Write(operation + ": " + error);

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                File.AppendAllText(Path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine(error);
        }
    }
}
