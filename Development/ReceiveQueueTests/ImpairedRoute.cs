using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

internal sealed class ImpairedRoute : IDisposable
{
    private readonly Socket[] routes;
    private readonly IPEndPoint server;
    private readonly Thread worker;
    private long pauseUntil;
    private volatile bool stop;
    private volatile bool suppressServerPongs;
    private int suppressedPongs;
    private int delayed;
    private int dropped;
    private Exception? failure;

    internal ImpairedRoute(int serverPort, int peers)
    {
        server = new IPEndPoint(IPAddress.Loopback, serverPort);
        routes = new Socket[peers];
        for (int index = 0; index < routes.Length; index++)
        {
            routes[index] = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            routes[index].Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }
        worker = new Thread(Run) { IsBackground = true };
        worker.Start();
    }

    internal int Port(int index) => ((IPEndPoint)routes[index].LocalEndPoint!).Port;

    internal void Pause(TimeSpan duration) =>
        Interlocked.Exchange(ref pauseUntil, Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency));

    internal void SuppressServerPongs() => suppressServerPongs = true;
    internal int SuppressedPongs => Volatile.Read(ref suppressedPongs);

    internal void Verify()
    {
        if (failure != null) throw new InvalidOperationException("UDP relay failed", failure);
        if (dropped < 50 || delayed < 1000) throw new InvalidOperationException("Insufficient impaired traffic");
        Console.WriteLine($"PASS: 170–250 ms simulated round-trip delay, 1% random loss and a 3s outage. Relayed {delayed:N0} datagrams, dropped {dropped:N0}.");
    }

    private void Run()
    {
        try
        {
            var random = new Random(257);
            var clients = new IPEndPoint?[routes.Length];
            var waiting = new PriorityQueue<(int Route, EndPoint Destination, byte[] Data), long>();
            var buffer = new byte[65536];
            while (!stop)
            {
                long now = Stopwatch.GetTimestamp();
                bool outage = now < Interlocked.Read(ref pauseUntil);
                while (waiting.TryPeek(out var datagram, out long due) && due <= now)
                {
                    waiting.Dequeue();
                    if (outage) dropped++;
                    else routes[datagram.Route].SendTo(datagram.Data, datagram.Destination);
                }
                for (int index = 0; index < routes.Length; index++)
                {
                    var socket = routes[index];
                    for (int batch = 0; batch < 32 && socket.Poll(0, SelectMode.SelectRead); batch++)
                    {
                        EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                        int size = socket.ReceiveFrom(buffer, ref source);
                        bool fromServer = server.Equals(source);
                        if (!fromServer) clients[index] ??= (IPEndPoint)source;
                        if (!fromServer && !source.Equals(clients[index])) continue;
                        var destination = fromServer ? clients[index] : server;
                        if (destination == null) continue;
                        if (fromServer && suppressServerPongs && size > 0 && buffer[0] == 130)
                        {
                            Interlocked.Increment(ref suppressedPongs);
                            continue;
                        }
                        if (outage || random.Next(100) == 0)
                        {
                            dropped++;
                            continue;
                        }
                        if (waiting.Count >= 16384) throw new InvalidOperationException("UDP relay exceeded fixture capacity");
                        long delay = random.Next(85, 126) * Stopwatch.Frequency / 1000;
                        waiting.Enqueue((index, destination, buffer.AsSpan(0, size).ToArray()), now + delay);
                        delayed++;
                    }
                }
                Thread.Sleep(1);
            }
        }
        catch (Exception error) { failure = error; }
    }

    public void Dispose()
    {
        stop = true;
        worker.Join();
        foreach (var route in routes) route.Dispose();
    }
}
