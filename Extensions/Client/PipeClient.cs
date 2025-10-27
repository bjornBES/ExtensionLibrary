
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GlobalLibrary;
using lib.debug;

public class PipeClient
{
    internal string PipeName { get; set; }
    internal string ClientId { get; set; }

    public NamedPipeClientStream Stream;
    public DebugStreamReader Reader;
    public DebugStreamWriter Writer;

    private readonly ConcurrentQueue<string> receivedMessages = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<Package> receivedPackages = new ConcurrentQueue<Package>();
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim receiveLock = new SemaphoreSlim(1, 1);

    private volatile bool IsRunning = false;

    public delegate void OnExit(int exitCode);
    public event OnExit OnClose;

    string outMessage;
    Thread thread;

    public bool IsConnected
    {
        get { return IsRunning && Writer != null && Reader != null && Stream != null; }
    }

    public PipeClient(string pipeName, string clientId)
    {
        PipeName = pipeName;
        ClientId = clientId;
        DebugWriter.AddModulesToLog("Client", clientId);
    }

    public void Initialize()
    {
        if (IsRunning)
        {
            return;
        }

        Stream = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        DebugWriter.WriteLine(ClientId, $"Connecting to server...");
        Stream.ConnectAsync().GetAwaiter().GetResult();

        Reader = new DebugStreamReader(Stream, leaveOpen: true);
        Writer = new DebugStreamWriter(Stream, leaveOpen: true) { AutoFlush = true };
        Reader.SetFile($"{ClientId}" ?? "unknown");
        Writer.SetFile($"{ClientId}" ?? "unknown");

        Thread.Sleep(600);

        SendString(ClientId);

        IsRunning = true;

        thread = new Thread(new ThreadStart(ListenLoop));
        thread.Start();

        GetString(out string outMessage);
        if (!string.Equals(outMessage, "Server:READY", StringComparison.OrdinalIgnoreCase))
        {
            DebugWriter.WriteLine(ClientId, "Servers not ready");
            Stop(-1);
        }
    }

    public string GetLastMessage() => outMessage;

    internal void SendString(string message)
    {
        sendLock.Wait();
        Writer.WriteLineAsync(message).GetAwaiter().GetResult();
        sendLock.Release();
    }

    internal void SendDataPackage(DataPackage package)
    {
        sendLock.Wait();
        string json = JsonSerializer.Serialize(package);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        Stream.Write(bytes, 0 , bytes.Length);
        sendLock.Release();
    }

    public bool SendPackage<T>(PackageTypes packageType, T obj)
    {
        if (!IsConnected)
        {
            outMessage = "Not connected";
            return false;
        }

        string json = JsonSerializer.Serialize(obj);

        return SendPackage(PackageId.PackageTypeToId(packageType), json);
    }

    internal bool SendPackage(string packageId, string message)
    {
        if (!IsConnected)
        {
            outMessage = "Not connected";
            return false;
        }

        byte[] data = Encoding.UTF8.GetBytes(message);

        try
        {
            DataPackage dataPackage = new DataPackage()
            {
                PackageId = packageId,
                ClientId = ClientId,
                PackageSize = data.Length,
                PackageData = data,
            };
            string json = JsonSerializer.Serialize(dataPackage);
            SendString($"Package {ClientId},{packageId},{json.Length}");

            GetString(out string ack);

            if (!string.Equals(ack, "ack", StringComparison.OrdinalIgnoreCase))
            {
                outMessage = ack ?? "No ACK";
                return false;
            }

            SendDataPackage(dataPackage);

            GetString(out string respont);
            if (!string.Equals(respont, "Received package"))
            {
                outMessage = ack ?? "No Received package";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            DebugWriter.WriteLine(ClientId, $"Send error: {ex.Message}");
            Stop(1);
            return false;
        }
    }

    internal void GetString(out string message)
    {
        receiveLock.Wait();
        while (receivedMessages.Count() == 0)
        {

        }
        if (!receivedMessages.TryDequeue(out message))
        {
            receiveLock.Release();
            DebugWriter.WriteLine(ClientId, $"ack message not received");
        }
        receiveLock.Release();
    }

    public bool TryGetPackage(out Package package) =>
        receivedPackages.TryDequeue(out package!);
    public bool TryGetMessage(out string message) =>
        receivedMessages.TryDequeue(out message!);


    private void ListenLoop()
    {
        string line;
        try
        {
            while (IsRunning && Reader != null)
            {
                line = Reader.ReadLine();
                if (line == null)
                    break;

                if (line.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    IsRunning = false;
                    Stop(0);

                    DebugWriter.WriteLine(ClientId, $"Received exit command. Closing...");
                    break;
                }
                else if (line.StartsWith("package", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2)
                    {
                        DebugWriter.WriteLine(ClientId, "Usage: package clientid,packageId,size");
                        continue;
                    }

                    string[] packageParts = parts.Last().Split(',', 3, StringSplitOptions.RemoveEmptyEntries);

                    Package package = ReceivePackage(packageParts);

                    receivedPackages.Enqueue(package);

                    continue;
                }
                else if (line.Equals("exit -", StringComparison.OrdinalIgnoreCase))
                {
                    IsRunning = false;
                    DebugWriter.WriteLine(ClientId, $"Received exit command. Closing...");
                    break;
                }

                DebugWriter.WriteLine(ClientId, $"Received {line}");

                receivedMessages.Enqueue(line);
            }
        }
        catch (Exception ex)
        {
            DebugWriter.WriteLine(ClientId, $"Listen loop error: {ex.Message}");
            DebugWriter.WriteLine(ClientId, ex.ToString());
        }
        // finally
        // {
        //     Stop(1);
        // }
    }

    internal byte[] ReceiveBytes(int size, out int bytesReaded, int timeOut = 1000)
    {
        DateTime dateTime = DateTime.UtcNow;
        byte[] buffer = new byte[size];
        int totalRead = 0;
        bytesReaded = 0;

        while (totalRead < size)
        {
            if (dateTime <= DateTime.UtcNow.AddMilliseconds(timeOut))
            {
                return buffer;
            }
            if (totalRead == size)
            {
                break;
            }

            if (Stream == null)
            {
                bytesReaded = 0;
                return null;
            }

            int bytesRead = Stream.Read(buffer, totalRead, size - totalRead);
            if (bytesRead <= 0)
            {
                bytesReaded = 0;
                return null;
            }

            totalRead += bytesRead;
            bytesReaded += bytesRead;
            if (totalRead == size)
            {
                break;
            }
        }
        return buffer;
    }
    internal Package ReceivePackage(string[] parts)
    {
        if (parts.Length != 3)
        {
            DebugWriter.WriteLine(ClientId, "Usage: package clientid,packageId,size");
            return null;
        }

        string clientId = parts[0];
        string packageType = parts[1];
        if (!int.TryParse(parts[2], out int packageSize) || packageSize <= 0)
        {
            SendString("error: invalid size");
            DebugWriter.WriteLine(ClientId, "Package is invakud size"); // i think its invalid or invakud
            return null;
        }

        // Server Ident
        if (clientId != "AS:SERVER")
        {
            DebugWriter.WriteLine(ClientId, "Package is not send from server");
            return null;
        }

        // Acknowledgment
        SendString("ACK");

        // Send data
        SendString("Send Data");

        byte[] bytes = ReceiveBytes(packageSize, out int bytesRead);

        if (bytesRead == packageSize)
        {
            DebugWriter.WriteLine(ClientId, $"Sending Received package for package {packageType}");
            SendString("Received package");
        }
        else
        {
            DebugWriter.WriteLine(ClientId, $"Sending error for package {packageType}");
            SendString("error: incomplete data");
            return null;
        }

        DataPackage dataPackage = JsonSerializer.Deserialize<DataPackage>(bytes);

        Package package = new Package()
        {
            ClientId = dataPackage.ClientId,
            PackageId = dataPackage.PackageId,
            PackageSize = dataPackage.PackageSize,
            PackageData = dataPackage.PackageData,
        };

        return package;
    }

    public void Stop(int exitCode)
    {
        if (!IsRunning) return;

        IsRunning = false;

        SendString($"STOP: {ClientId} {exitCode}");

        try { Stream?.Dispose(); } catch { }
        Stream = null;
        Reader = null;
        Writer = null;

        OnClose?.Invoke(exitCode);

        thread.Join();

        DebugWriter.WriteLine(ClientId, $"Disconnected.");
    }
}