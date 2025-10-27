
using System.Text.Json;
using GlobalLibrary;
using lib.debug;

namespace Extensions.API.Commands;

public class CommandsAPI : API
{
    internal Dictionary<string, Delegate> commands = new Dictionary<string, Delegate>();
    public CommandsAPI() : base()
    {
        
    }
    public void RegisterCommand(string id, string title, Delegate execute)
    {
        string[] types = execute.Method.GetParameters().Select(p => p.ParameterType.Name).ToArray();
        CommandAddonPackage addonPackage = new CommandAddonPackage() { CommandId = id, CommandName = title, CommandArgTypes = types };
        string json = JsonSerializer.Serialize(addonPackage);
        AddonPackage addonPack = new AddonPackage() { addonPackageData = json, addonPackageType = AddonPackageTypes.command };
        PipeClient.SendPackage(PackageTypes.Addon, addonPack);
        commands.Add(id, execute);
    }

    internal void RunCommand(string id, object[] args)
    {
        if (commands.TryGetValue(id, out Delegate execute))
        {
            execute.DynamicInvoke(args);
        }
        else
        {
            Console.WriteLine("fucked");
        }
    }
}