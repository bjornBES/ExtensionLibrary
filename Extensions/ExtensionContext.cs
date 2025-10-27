
using Extensions.API.Commands;
using Extensions.API.Window;
using GlobalLibrary;

namespace Extensions;

public class ExtensionContext
{
    public ExtensionContext(ExtensionManifest manifest, PipeClient pipeClient, string[] args)
    {
        Commands = new CommandsAPI();
        Commands.PipeClient = pipeClient;
        Window = new WindowAPI();
        Window.PipeClient = pipeClient;
    }

    public CommandsAPI Commands { get; }
    public WindowAPI Window{ get; }

    string ExtensionPath { get; }
}