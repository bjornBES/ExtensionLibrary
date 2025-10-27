
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using GlobalLibrary;

namespace Extensions;

public abstract class Extension
{
    ExtensionManifest ExtensionManifest;
    private ExtensionContext Context { get; set; }

    PipeClient pipeClient { get; set; }

    public Action<Package> OnReceivePackage;
    public string clientId { get; private set; }

    public void StartExtension(Extension extension, ExtensionManifest manifest, string[] args)
    {
        string extensionPath = args[0];
        string path = extensionPath;
        if (path == null)
        {

        }

        OnReceivePackage += ReceivePackage;

        ExtensionManifest = manifest;
        clientId = manifest.Name;

        pipeClient = new PipeClient("extensionPipe", ExtensionManifest.Name);

        Context = new ExtensionContext(ExtensionManifest, pipeClient, args);
    }

    internal void ReceivePackage(Package package)
    {
        string json = Encoding.UTF8.GetString(package.PackageData);
        PackageTypes packageType = PackageId.FromIdToPackageType(package.PackageId);
        switch (packageType)
        {
            case PackageTypes.Command:
                CommandPackage command = JsonSerializer.Deserialize<CommandPackage>(json);
                Context.Commands.RunCommand(command.CommandId, command.CommandArgs);
                break;
        }
    }

    internal void UpdateLoop()
    {
        bool ServerReady = false;
        while (true)
        {
            if (pipeClient.TryGetMessage(out string message))
            {
                if (string.Equals(message, "Server:READY"))
                {
                    ServerReady = true;
                }

                if (ServerReady)
                {
                    if (string.Equals(message, "activate", StringComparison.OrdinalIgnoreCase))
                    {
                        Activate(Context);
                    }
                }
            }
            else if (pipeClient.TryGetPackage(out Package package))
            {

            }
        }
    }

    public void Activate()
    {
        pipeClient.Initialize();
        Activate(Context);
    }
    public void Stop()
    {
        pipeClient.Stop(0);
        Deactivate();
    }
    public abstract void Activate(ExtensionContext context);
    public abstract void Deactivate();
}