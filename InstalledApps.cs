using System.IO;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace RadialMenu1;

internal static class InstalledApps
{
    public static Task<RunningApp[]> ReadAsync()
    {
        var result = new TaskCompletionSource<RunningApp[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Read()); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static RunningApp[] Read() => WithFolder<RunningApp[]>(folder =>
    {
        object items = folder.Items();
        var apps = new List<RunningApp>();
        try
        {
            dynamic list = items;
            int count = list.Count;
            for (int index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = list.Item(index);
                    if (item == null) continue;
                    dynamic app = item;
                    string name = app.Name;
                    string path = app.Path;
                    string? id = app.ExtendedProperty("System.AppUserModel.ID") as string;
                    string? executable = app.ExtendedProperty("System.Link.TargetParsingPath") as string;
                    bool fromShell = !string.IsNullOrWhiteSpace(id);
                    string target = fromShell ? RunningApp.PackagedPrefix + id : path;
                    if (string.IsNullOrWhiteSpace(name) || (!fromShell && !File.Exists(path))) continue;
                    string details = File.Exists(executable) ? executable! : "Меню «Пуск»";
                    apps.Add(new RunningApp(name, details, target, true, fromShell));
                }
                catch (Exception ex) when (ex is COMException or RuntimeBinderException)
                {
                    // Windows может удалить ярлык, пока список загружается.
                }
                finally { Release(item); }
            }
        }
        finally { Release(items); }

        return apps.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .DistinctBy(app => app.Target, StringComparer.OrdinalIgnoreCase).ToArray();
    });

    public static string? ExecutableFor(string appId)
    {
        try
        {
            return WithFolder<string?>(folder =>
            {
                object? item = folder.ParseName(appId);
                if (item == null) return null;
                try
                {
                    dynamic app = item;
                    string? arguments = app.ExtendedProperty("System.Link.Arguments") as string;
                    if (arguments?.Contains("://", StringComparison.Ordinal) == true ||
                        arguments?.Contains("--app", StringComparison.OrdinalIgnoreCase) == true) return null;
                    string? executable = app.ExtendedProperty("System.Link.TargetParsingPath") as string;
                    return File.Exists(executable) ? executable : null;
                }
                finally { Release(item); }
            });
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidOperationException)
        {
            return null;
        }
    }

    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        object? shell = type != null ? Activator.CreateInstance(type) : null;
        if (shell == null) throw new InvalidOperationException("Проводник Windows недоступен.");
        object? folder = null;
        try
        {
            folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
            if (folder == null) throw new InvalidOperationException("Меню «Пуск» недоступно.");
            return action(folder);
        }
        finally
        {
            Release(folder);
            Release(shell);
        }
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
