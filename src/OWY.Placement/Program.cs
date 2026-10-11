using System.Text.Json;
using OWY.Taskbar;

static class Program
{
    [STAThread] static void Main(string[] args)
    {
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OWY","placement.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var host=new TaskbarHost();
            host.QuitRequested+=TaskbarHost.EndLoop;
            host.Clicked+=()=>{ File.WriteAllText(path,JsonSerializer.Serialize(host.Report));TaskbarHost.EndLoop(); };
            var timer=new System.Threading.Timer(_=>
            {
                File.WriteAllText(path,JsonSerializer.Serialize(host.Report));
                host.RequestQuit();
            },null,TimeSpan.FromSeconds(20),Timeout.InfiniteTimeSpan);
            File.WriteAllText(path,JsonSerializer.Serialize(host.Report));
            TaskbarHost.RunLoop();timer.Dispose();
        }
        catch(Exception ex) { File.WriteAllText(path,JsonSerializer.Serialize(new{Error=ex.GetType().Name,Detail=ex.Message,Stack=ex.StackTrace})); Environment.ExitCode=1; }
    }
}
