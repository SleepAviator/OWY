using Microsoft.Win32;
using System.Text.Json;

namespace OWY.Core;

public sealed record Settings(bool ShowTaskbar=true,bool ShowNetwork=true,bool ShowWatts=true,
    bool LaunchAtSignIn=false,int RangeSeconds=3600)
{
    public static Settings Load(string path,out string? issue)
    {
        issue=null;if(!File.Exists(path))return new();
        try
        {
            var value=JsonSerializer.Deserialize<Settings>(File.ReadAllText(path),Protocol.Json);
            if(value is null || !History.Ranges.Contains(value.RangeSeconds))throw new InvalidDataException("Unsupported settings");
            return value;
        }
        catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {issue="Settings could not be loaded: "+ex.Message;return new();}
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending=path+".tmp";File.WriteAllText(pending,JsonSerializer.Serialize(this,Protocol.Json));File.Move(pending,path,true);
    }
    public void ApplySignIn(string executable)
    {
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(LaunchAtSignIn)key.SetValue("OWY",$"\"{executable}\" --background");else key.DeleteValue("OWY",false);
    }
}
