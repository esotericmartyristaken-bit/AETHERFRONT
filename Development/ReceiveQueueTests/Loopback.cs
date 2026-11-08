using System.Diagnostics;
using System.Net;
using System.Reflection;

internal static class Loopback
{
    internal static void RunReceiveOnly(Assembly assembly, bool reproduce)
    {
        Type Type(string name) => assembly.GetType("Lidgren.Network." + name, true)!;
        dynamic unreliable = Enum.Parse(Type("NetDeliveryMethod"), "Unreliable");
        dynamic Configure(float timeout)
        {
            dynamic config = Activator.CreateInstance(Type("NetPeerConfiguration"), "aetherfront-inactivity-test")!;
            config.LocalAddress = IPAddress.Loopback;
            config.PingInterval = 1f;
            config.ConnectionTimeout = timeout;
            return config;
        }

        dynamic server = Activator.CreateInstance(Type("NetServer"), (object)Configure(15f))!;
        dynamic client = Activator.CreateInstance(Type("NetClient"), (object)Configure(4f))!;
        server.Start();
        using var proxy = new ImpairedRoute((int)server.Port, 1);
        string reason = "";
        int received = 0;
        try
        {
            client.Start();
            client.Connect("127.0.0.1", proxy.Port(0));
            WaitUntil(() =>
            {
                Pump();
                return server.ConnectionsCount == 1 && client.ConnectionStatus.ToString() == "Connected";
            }, TimeSpan.FromSeconds(15), "Receive-only connection");
            proxy.SuppressServerPongs();
            var started = Stopwatch.StartNew();
            while (started.Elapsed < TimeSpan.FromSeconds(10) && reason.Length == 0)
            {
                if ((int)server.ConnectionsCount == 1)
                {
                    dynamic message = server.CreateMessage();
                    message.Write(received);
                    server.SendMessage(message, server.Connections[0], unreliable, 0);
                }
                Thread.Sleep(85);
                Pump();
            }
            if (received < 15 || proxy.SuppressedPongs < 2)
                throw new InvalidOperationException("Fixture did not exchange enough one-way application data or lose enough Pongs");
            if (reproduce)
            {
                if (!reason.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Original transport did not reproduce a false timeout: " + reason);
                Console.WriteLine($"Reproduced: the original transport timed out after receiving {received} live messages.");
                return;
            }
            if (reason.Length > 0) throw new InvalidOperationException("Receiving real data still timed out: " + reason);
            proxy.Pause(TimeSpan.FromSeconds(12));
            WaitUntil(() => { Pump(); return reason.Length > 0; }, TimeSpan.FromSeconds(9), "Actual inactivity timeout");
            if (!reason.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected outage disconnect: " + reason);
            Console.WriteLine($"PASS: {received} live receive-only messages kept the connection alive without Pongs. A fully silent link still timed out.");
        }
        finally
        {
            client.Shutdown("Test complete");
            server.Shutdown("Test complete");
        }

        void Pump()
        {
            while (server.ReadMessage() is object incoming) server.Recycle((dynamic)incoming);
            while (client.ReadMessage() is object incoming)
            {
                dynamic message = incoming;
                if (message.MessageType.ToString() == "Data") received++;
                if (message.MessageType.ToString() == "StatusChanged" && message.ReadByte() == 7)
                    reason = message.ReadString();
                client.Recycle(message);
            }
        }
    }

    internal static void Run(Assembly assembly, bool impaired)
    {
        const int peerCount = 24;
        int checkpoints = 300;
        if (Environment.GetEnvironmentVariable("APF_UDP_ROUNDS") is string configured &&
            (!int.TryParse(configured, out checkpoints) || checkpoints < 300 || checkpoints > 20000))
            throw new ArgumentException("APF_UDP_ROUNDS must be between 300 and 20000");
        Type Type(string name) => assembly.GetType("Lidgren.Network." + name, true)!;
        dynamic reliable = Enum.Parse(Type("NetDeliveryMethod"), "ReliableOrdered");
        dynamic unreliable = Enum.Parse(Type("NetDeliveryMethod"), "Unreliable");
        dynamic Configure()
        {
            dynamic config = Activator.CreateInstance(Type("NetPeerConfiguration"), "aetherfront-receive-test")!;
            config.LocalAddress = IPAddress.Loopback;
            config.Port = 0;
            config.MaximumConnections = peerCount + 1;
            config.PingInterval = 1f;
            config.ConnectionTimeout = 15f;
            return config;
        }

        dynamic server = Activator.CreateInstance(Type("NetServer"), (object)Configure())!;
        var peers = new dynamic[peerCount];
        var nextReceived = new int[peerCount];
        var nextEchoed = new int[peerCount];
        server.Start();
        using var proxy = impaired ? new ImpairedRoute((int)server.Port, peerCount) : null;
        try
        {
            for (int id = 0; id < peerCount; id++)
            {
                dynamic peer = Activator.CreateInstance(Type("NetClient"), (object)Configure())!;
                peers[id] = peer;
                peer.Start();
                peer.Connect("127.0.0.1", proxy?.Port(id) ?? (int)server.Port);
            }
            WaitUntil(() =>
            {
                Pump();
                return server.ConnectionsCount == peerCount && peers.All(peer => peer.ConnectionStatus.ToString() == "Connected");
            }, TimeSpan.FromSeconds(15), "Connection burst");

            var elapsed = Stopwatch.StartNew();
            for (int sequence = 0; sequence < checkpoints; sequence++)
            {
                if (sequence % 300 == 110) proxy?.Pause(TimeSpan.FromSeconds(3));
                for (int id = 0; id < peerCount; id++)
                {
                    dynamic sample = peers[id].CreateMessage();
                    sample.Write(id);
                    sample.Write(sequence);
                    peers[id].SendMessage(sample, reliable, 0);
                }
                // Reproduce a temporarily stalled consumer with a burst of position
                // traffic from one client. Other clients keep their reliable messages.
                if (sequence % 300 is 60 or 180)
                {
                    for (int burst = 0; burst < 160; burst++)
                    {
                        for (int item = 0; item < 32; item++)
                        {
                            dynamic update = peers[0].CreateMessage();
                            update.Write(-1);
                            peers[0].SendMessage(update, unreliable, 0);
                        }
                        Thread.Sleep(5);
                    }
                }
                Thread.Sleep(65);
                Pump();
            }
            WaitUntil(() =>
            {
                Pump();
                return nextEchoed.All(count => count == checkpoints);
            }, TimeSpan.FromSeconds(12), "Reliable delivery");
            if ((int)server.ConnectionsCount != peerCount)
                throw new InvalidOperationException("A UDP peer disconnected during the load run");
            Console.WriteLine($"PASS: {peerCount} concurrent real UDP peers, {peerCount * checkpoints:N0} reliable round trips in {elapsed.Elapsed.TotalSeconds:F1}s, with recurring stalled-consumer bursts.");
            proxy?.Verify();
        }
        finally
        {
            foreach (dynamic peer in peers)
                if (peer != null) peer.Shutdown("Test complete");
            server.Shutdown("Test complete");
        }

        void Pump()
        {
            while (server.ReadMessage() is object received)
            {
                dynamic message = received;
                try
                {
                    if (message.MessageType.ToString() != "Data") continue;
                    int id = message.ReadInt32();
                    if (id == -1) continue;
                    int sequence = message.ReadInt32();
                    if (sequence != nextReceived[id]++) throw new InvalidOperationException($"Peer {id} request order changed at {sequence}");
                    dynamic reply = server.CreateMessage();
                    reply.Write(sequence);
                    server.SendMessage(reply, message.SenderConnection, reliable, 0);
                }
                finally { server.Recycle(message); }
            }
            for (int id = 0; id < peerCount; id++)
                while (peers[id].ReadMessage() is object received)
                {
                    dynamic message = received;
                    try
                    {
                        if (message.MessageType.ToString() != "Data") continue;
                        int sequence = message.ReadInt32();
                        if (sequence != nextEchoed[id]++) throw new InvalidOperationException($"Peer {id} reply order changed at {sequence}");
                    }
                    finally { peers[id].Recycle(message); }
                }
        }
    }

    private static void WaitUntil(Func<bool> check, TimeSpan timeout, string label)
    {
        var elapsed = Stopwatch.StartNew();
        while (!check())
        {
            if (elapsed.Elapsed >= timeout) throw new TimeoutException(label);
            Thread.Sleep(5);
        }
    }
}
