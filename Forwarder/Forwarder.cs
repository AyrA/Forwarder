using System;
using System.Collections.Generic;
using System.IO;
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
        public bool Dump
        { get; set; } = false;

        private FileStream FS = null;

        private const int BUFFER = 1024 * 1024;

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

        private class Headers
        {
            public Dictionary<string, string> Header;
            public byte[] Raw;
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
            Start(0);
        }

        public void Start(int Timeout)
        {
            if (Destination == null)
            {
                if (Timeout < 0)
                {
                    throw new ArgumentOutOfRangeException("Timeout must be 0 or bigger");
                }
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
                        if (Timeout > 0)
                        {
                            Source.NS.ReadTimeout = Timeout;
                            Destination.NS.ReadTimeout = Timeout;
                        }

                        var H = ReadHeaders(Source);

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
            if (FS != null)
            {
                FS.Close();
                FS.Dispose();
                FS = null;
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

        private Headers ReadHeaders(Connection Source)
        {
            var H = new Headers();
            H.Header = new Dictionary<string, string>();
            using (MemoryStream MS = new MemoryStream())
            {
                while (MS.Length < 100000/*100 KB*/ && !IsDone(MS))
                {
                    int b=Source.NS.ReadByte();
                    if (b < 0)
                    {
                        return null;
                    }
                    MS.WriteByte((byte)b);
                }
                MS.Seek(0, SeekOrigin.Begin);
                H.Raw = MS.ToArray();
                using (StreamReader S = new StreamReader(MS, Encoding.UTF8, false, 0, true))
                {
                    string Line = S.ReadLine();
                    //last line is empty
                    if (Line.Length > 0)
                    {
                        //first line is request type
                        if (H.Header.Count == 0)
                        {
                            string[] Parts = Line.Split(' ');
                            if (Parts.Length > 2)
                            {
                                H.Header["_METHOD"] = Parts[0].ToUpper();
                                H.Header["_PATH"] = string.Join(" ", Parts, 1, Parts.Length - 2);
                                H.Header["_PROTOCOL"] = Parts[Parts.Length - 1];
                            }
                            else
                            {
                                //Invalid first header
                                return null;
                            }
                        }
                        //HTTP header. We ignore those with _ at the start
                        else if (Line.Contains(":") && !Line.StartsWith("_") && Line.IndexOf(':') < Line.Length - 2)
                        {
                            var Header = Line.Substring(0, Line.IndexOf(':'));
                            var Value = Line.Substring(Line.IndexOf(':') + 2);
                            if (H.Header.ContainsKey(Header))
                            {
                                H.Header[Header] += "; " + Value;
                            }
                            else
                            {
                                H.Header[Header] = Value;
                            }
                        }
                    }
                }
            }
            return H;
        }

        private bool IsDone(Stream S)
        {
            if (S.Length < 4)
            {
                return false;
            }
            long Pos = S.Position;
            S.Seek(-4, SeekOrigin.End);
            byte[] Data = new byte[4];
            S.Read(Data, 0, Data.Length);
            S.Seek(Pos, SeekOrigin.Begin);
            return
                Data[0] == 13 &&
                Data[1] == 10 &&
                Data[2] == 13 &&
                Data[3] == 10; /*\r\n\r\n*/
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
                if (Dump)
                {
                    if (FS == null)
                    {
                        FS = File.Create(string.Format(@"R:\dump_{0}.bin", DateTime.UtcNow.Ticks));
                    }
                    FS.Write(From.Data, 0, Readed);
                }
                From.BytesReceived += (ulong)Readed;
                DataTransmitted?.Invoke(this, Readed);
                /*Only enable if the connections behave weirdly. Causes massive performance issues.
#if DEBUG
                lock ("console")
                {
                    Console.ForegroundColor = From == Source ? ConsoleColor.Green : ConsoleColor.Yellow;
                    Console.Error.WriteLine(b2s(From.Data, Readed));
                    Console.ResetColor();
                }
#endif
                //*/
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

        /// <summary>
        /// Writes the first line of string data to the console.
        /// Truncates it, if it exceeds the console size.
        /// Replaces some chars with '.'
        /// </summary>
        /// <param name="Data">Byte data</param>
        /// <param name="Count">Number of bytes to process</param>
        /// <returns>ASCII String</returns>
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
