using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Forwarder
{
    public enum ForwarderEventType
    {
        /// <summary>
        /// The connection has been fully established and data is forwarded between the two
        /// </summary>
        Established,
        /// <summary>
        /// The client disconnected
        /// </summary>
        ClientGone,
        /// <summary>
        /// The server disconnected
        /// </summary>
        ServerGone,
        /// <summary>
        /// A connection to the server could not be established
        /// </summary>
        ServerUnavailable,
        /// <summary>
        /// The connnection was closed by the Stop() call
        /// </summary>
        UserClosed
    }

    public delegate void ForwarderEventHandler(object sender, ForwarderEventType Action);
    public delegate void DataTransmitHandler(object sender, int Amount);
    public class Forwarder : IDisposable
    {
        private const int BUFFER = 1024 * 1024;
        private const int TIMEOUT = 30 * 1000;

        private class Connection : IDisposable
        {
            public ConnectionMode Mode;
            public byte[] Data;
            public NetworkStream NS;
            public IAsyncResult Handle;
            public Connection Other;
            public ulong BytesReceived;

            public void Dispose()
            {
                if (NS != null)
                {
                    var Temp = NS;
                    NS = null;
                    Temp.Close();
                    Temp.Dispose();
                    Temp = null;
                }
            }

            public enum ConnectionMode
            {
                Client,
                Server
            }
        }

        private Connection Source, Destination;
        private IPEndPoint Dest;

        public event ForwarderEventHandler ForwarderEvent;
        public event DataTransmitHandler DataTransmitted;

        public ulong BytesTransmitted
        {
            get
            {
                return Source == null || Destination == null ? 0 : Source.BytesReceived + Destination.BytesReceived;
            }
        }

        public Forwarder(Socket Source, IPEndPoint Destination)
        {
            this.Source = new Connection()
            {
                NS = new NetworkStream(Source, true),
                Data = new byte[BUFFER]
            };
            Dest = Destination;
        }

        public void Start()
        {
            if (Destination == null)
            {
                Destination = new Connection();
                Destination.Other = Source;
                Source.Other = Destination;
                Destination.Data = new byte[BUFFER];

                Source.Mode = Connection.ConnectionMode.Client;
                Destination.Mode = Connection.ConnectionMode.Server;

                new Thread(delegate ()
                {
                    Socket Temp = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        Temp.Connect(Dest);
                        Destination.NS = new NetworkStream(Temp, true);
                        ForwarderEvent?.Invoke(this, ForwarderEventType.Established);
                    }
                    catch
                    {
                        Source.NS.Close();
                        Source.NS.Dispose();
                        Source = null;
                        ForwarderEvent?.Invoke(this, ForwarderEventType.ServerUnavailable);
                    }
                    if (Destination != null)
                    {
                        Source.NS.ReadTimeout = TIMEOUT;
                        Destination.NS.ReadTimeout = TIMEOUT;
                        Source.Handle = Source.NS.BeginRead(Source.Data, 0, BUFFER, DataIn, Source);
                        Destination.Handle = Destination.NS.BeginRead(Destination.Data, 0, BUFFER, DataIn, Destination);
                    }

                }).Start();
            }
            else
            {
                throw new InvalidOperationException("Forwarder already running");
            }
        }

        public void Stop()
        {
            if (Destination == null)
            {
                throw new InvalidOperationException("Forwarder has not yet been started");
            }
            var Temp1 = Source;
            var Temp2 = Destination;
            Source = null;
            Destination = null;
            if (Temp1 != null && Temp1.NS != null)
            {
                Temp1.NS.Close();
                Temp1.NS.Dispose();
            }
            if (Temp2 != null && Temp2.NS != null)
            {
                Temp2.NS.Close();
                Temp2.NS.Dispose();
            }
            ForwarderEvent?.Invoke(this, ForwarderEventType.UserClosed);
        }

        public void Dispose()
        {
            if (Destination != null)
            {
                Stop();
            }
        }

        private void DataIn(IAsyncResult ar)
        {
            if (Source == null || Destination == null)
            {
                return;
            }
            int Readed = 0;

            Connection From = (Connection)ar.AsyncState;
            Connection To = From.Other;

            try
            {
                Readed = From.NS.EndRead(ar);
            }
            catch
            {
                ForwarderEvent?.Invoke(this, From == Source ? ForwarderEventType.ClientGone : ForwarderEventType.ServerGone);
            }
            if (Readed > 0)
            {
                From.BytesReceived += (ulong)Readed;
                DataTransmitted?.Invoke(this, Readed);
#if DEBUG
                lock ("console")
                {
                    Console.ForegroundColor = From == Source ? ConsoleColor.Green : ConsoleColor.Yellow;
                    Console.Error.WriteLine(b2s(From.Data, Readed));
                    Console.ResetColor();
                }
#endif
                try
                {
                    To.NS.Write(From.Data, 0, Readed);
                    From.Handle = From.NS.BeginRead(From.Data, 0, BUFFER, DataIn, From);
                }
                catch
                {
                    ForwarderEvent?.Invoke(this, To == Source ? ForwarderEventType.ClientGone : ForwarderEventType.ServerGone);
                }
            }
            else
            {
                ForwarderEvent?.Invoke(this, From == Source ? ForwarderEventType.ClientGone : ForwarderEventType.ServerGone);
            }
        }

        private string b2s(byte[] Data, int Count)
        {
            int MAXCOUNT = Console.BufferWidth - 1;
            byte[] Mod = new byte[Count > MAXCOUNT ? MAXCOUNT : Count];
            int LF = 0;
            for (int x = 0; x < Mod.Length && LF == 0; x++)
            {
                if (LF == 0 && Data[x] == '\n')
                {
                    LF = x;
                }
                if (Data[x] < 31)
                {
                    Mod[x] = (byte)'.';
                }
                else
                {
                    Mod[x] = Data[x];
                }
            }
            return Encoding.ASCII.GetString(Mod, 0, LF == 0 ? Mod.Length : LF).Trim();
        }
    }
}
