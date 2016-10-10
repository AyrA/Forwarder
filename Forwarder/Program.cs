using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Forwarder
{
    static class Program
    {
        struct Arguments
        {
            public IPEndPoint Listener;
            public IPEndPoint Destination;
            public int Timeout;
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main(string[] Args)
        {
            ulong BytesTransmitted = 0;
            List<Forwarder> Connections = new List<Forwarder>();
#if DEBUG
            var Server = new Server("127.0.0.1", 8080);
            var Dest = new IPEndPoint(IPAddress.Parse("46.140.111.85"), 80);
#else
#endif
            Server.Connection += delegate (object sender, Socket Connection)
            {
                Console.Error.WriteLine("Got connection");
                Forwarder F = new Forwarder(Connection, Dest);
                Connections.Add(F);

                F.DataTransmitted += delegate (object source, int Count)
                {
                    BytesTransmitted += (ulong)Count;
                };

                F.ForwarderEvent += delegate (object source, ForwarderEventType Action)
                {
                    Forwarder Fwd = (Forwarder)source;
                    switch (Action)
                    {
                        case ForwarderEventType.Established:
                            Console.Error.WriteLine("Server connected.");
                            break;
                        case ForwarderEventType.ClientGone:
                        case ForwarderEventType.ServerGone:
                        case ForwarderEventType.ServerUnavailable:
                        case ForwarderEventType.UserClosed:
                            Console.Error.WriteLine("Connection lost");
                            Fwd.Dispose();
                            Connections.Remove(Fwd);
                            break;
                    }
                };
                F.Start();
            };

            (new Thread(delegate ()
            {
                while (true)
                {
                    Console.Title = string.Format("TCP Forwarder | Transmitted: {0}", FormatSize(BytesTransmitted));
                    Thread.Sleep(1000);
                }
            })
            { IsBackground = true, Name = "Title updater" }).Start();

            Server.Start();
            Console.WriteLine("Press [ESC] to stop");
            while (Console.ReadKey(true).Key != ConsoleKey.Escape) ;
            Server.Stop();
        }

        private static string FormatSize(double Size)
        {
            const double FACTOR = 1024.0;
            int index = 0;
            var Sizes = "Bytes|KB|MB|GB|TB|EB|ZB|YB".Split('|');
            while (Size >= FACTOR && index < Sizes.Length - 1)
            {
                Size /= FACTOR;
                ++index;
            }
            return string.Format("{0:0.0} {1}", Size, Sizes[index]);
        }
    }
}
