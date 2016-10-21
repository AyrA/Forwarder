#define CONSUME_REQUEST
using System;
using System.Collections.Generic;
using System.Globalization;
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

    public enum MessageType : int
    {
        Debug = ConsoleColor.Blue,
        Info = ConsoleColor.Green,
        Warn = ConsoleColor.Yellow,
        Error = ConsoleColor.Red
    }

    public delegate void ForwarderEventHandler(object sender, ForwarderEventType Action);
    public delegate void DataTransmitHandler(object sender, ulong Amount);
    public delegate void ForwarderMessageHandler(object sender, string Message, MessageType Type);

    public class Forwarder : IDisposable
    {
        public bool Dump
        { get; set; } = false;

        private FileStream FS = null;

        private const int CLOSE_TIMEOUT = 3000;
        private const int BUFFER = 1024 * 1024;
        private const long MAX_HEADER_LENGTH = 1000000; //1MB
        private const long MAX_CONTENT_LENGTH = MAX_HEADER_LENGTH * 100; //10MB


        #region HTTP_ANSWERS

        private const string LENGTH_EXCEEDED = @"HTTP/1.1 413 Payload Too Large
Connection: Close
Content-Type: text/html;charset=utf-8
Content-Length: 240
X-Engine: Forwarder

<h1>413 - Payload Too Large</h1>
Your request was larger than the server was willing to process.<br />
<br />
If you were uploading a file, please try to reduce the file size,
e.g. by scaling down images.<br />
<hr />
<i>Forwarder</i>";
        private const string LENGTH_EXPECTED = @"HTTP/1.1 411 Length Required
Connection: Close
Content-Type: text/html;charset=utf-8
X-Engine: Forwarder

<h1>411 - Length Required</h1>
Your browser did not set the <tt>Content-Length</tt> header (<a href='https://www.ietf.org/rfc/rfc2616.txt'>rfc2616</a>).<br />
If you see this message, please update your browser to the newest version and try again.<br />
<hr />
<i>Forwarder</i>";
        private const string UNSUPPORTED_METHOD = @"HTTP/1.1 405 Method Not Allowed
Connection: Close
Content-Type: text/html;charset=utf-8
X-Engine: Forwarder
Allow: GET, HEAD, POST, OPTIONS

<h1>405 - Method not allowed</h1>
Your browser used an unsupported method of accessing a resource.<br />
If you see this message, please update your browser to the newest version and try again.<br />
<hr />
<i>Forwarder</i>";
        private const string HEADERS_TOO_LARGE = @"HTTP/1.1 400 Bad Request
Connection: Close
Content-Type: text/html;charset=utf-8
X-Engine: Forwarder

<h1>400 - Bad Request<h1>
Your browser sent a request whose headers are too large.<br />
Try reopening your browser. If the error persists, try deleting your cookies.<br />
<hr />
<i>Forwarder</i>";
        private const string CONTENT_LENGTH_MISMATCH = @"HTTP/1.1 400 Bad Request
Connection: Close
Content-Type: text/html;charset=utf-8
X-Engine: Forwarder

<h1>400 - Bad Request<h1>
Your browser sent a request whose <tt>Content-Length</tt> header doesn't matches the actual request length.<br />
Please update your browser to the newest version and try again.<br />
<hr />
<i>Forwarder</i>";
        private const string SERVER_UNAVAILABLE = @"HTTP/1.1 502 Bad Gateway
Connection: Close
Content-Type: text/html;charset=utf-8
X-Engine: Forwarder

<h1>502 - Bad Gateway<h1>
We can't reach our backend server at the moment. Please try again later<br />
<hr />
<i>Forwarder</i>";

        #endregion

        #region Classes

        private class Connection : IDisposable
        {
            public ConnectionMode Mode;
            public byte[] Data;
            public NetworkStream NS;
            public IAsyncResult Handle;
            public Connection Other;
            public IPEndPoint EP;
            public Socket Source;
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

        private struct HEADER
        {
            public const string METHOD = "_METHOD";
            public const string PATH = "_PATH";
            public const string PROTOCOL = "_PROTOCOL";
            public const string CONTENT_LENGTH = "Content-Length";
        }

        private class Logger
        {
            public static class Modes
            {
#if DEBUG
                public static bool DEBUG = true;
#else
                public static bool DEBUG = false;
#endif
                public static bool INFO = true;
                public static bool WARN = true;
                public static bool ERROR = true;
            }
            public event ForwarderMessageHandler MSG;

            private string M(string S, MessageType T)
            {
                if ((T == MessageType.Debug && Modes.DEBUG) ||
                    (T == MessageType.Info && Modes.INFO) ||
                    (T == MessageType.Warn && Modes.WARN) ||
                    (T == MessageType.Error && Modes.ERROR))
                {
                    MSG?.Invoke(null, S, T);
                }
                return S;
            }

            public string E(string S, params object[] P)
            {
                return M(string.Format(S, P), MessageType.Error);
            }

            public string W(string S, params object[] P)
            {
                return M(string.Format(S, P), MessageType.Warn);
            }

            public string I(string S, params object[] P)
            {
                return M(string.Format(S, P), MessageType.Info);
            }

            public string D(string S, params object[] P)
            {
                return M(string.Format(S, P), MessageType.Debug);
            }

            public string E(string S)
            {
                return M(S, MessageType.Error);
            }

            public string W(string S)
            {
                return M(S, MessageType.Warn);
            }

            public string I(string S)
            {
                return M(S, MessageType.Info);
            }

            public string D(string S)
            {
                return M(S, MessageType.Debug);
            }
        }

        #endregion

        private int Timeout;
        private Connection Source, Destination;
        private IPEndPoint Dest;
        private Logger Log;

        public event ForwarderEventHandler ForwarderEvent;
        public event DataTransmitHandler DataTransmitted;
        public event ForwarderMessageHandler ForwarderMessage;

        public ulong BytesTransmitted
        {
            get
            {
                return Source == null || Destination == null ? 0 : Source.BytesReceived + Destination.BytesReceived;
            }
        }

        public Forwarder(Socket Source, IPEndPoint Destination)
        {
            Log = new Logger();
            Log.MSG += delegate (object sender, string Message, MessageType Type)
              {
                  ForwarderMessage?.Invoke(this, Message, Type);
              };

            this.Source = new Connection()
            {
                NS = new NetworkStream(Source, true),
                Data = new byte[BUFFER],
                EP = (IPEndPoint)Source.RemoteEndPoint,
                Source = Source
            };
            Dest = Destination;
            Log.D("Forwarder initialized");
        }

        public void Start()
        {
            Start(0);
        }

        public void Start(int Timeout)
        {
            if (Destination == null)
            {
                this.Timeout = Timeout;
                Log.I("{0}: Forwarder started", Source.EP);
                if (Timeout < 0)
                {
                    throw new ArgumentOutOfRangeException(Log.E("Timeout must be 0 or bigger"));
                }
                Destination = new Connection();
                Destination.Other = Source;
                Source.Other = Destination;
                Destination.Data = new byte[BUFFER];

                Source.Mode = Connection.ConnectionMode.Client;
                Destination.Mode = Connection.ConnectionMode.Server;

                new Thread(delegate ()
                {
                    /*
                    Socket Temp = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    Destination.Source = Temp;
                    try
                    {
                        Log.D("Connecting to server");
                        Temp.Connect(Dest);
                        Destination.NS = new NetworkStream(Temp, true);
                        Destination.EP = Dest;

                        byte[] DummyReq = Encoding.UTF8.GetBytes(@"GET / HTTP/1.1
Host: 127.0.0.1
Connection: keep-alive
Accept: text/html
User -Agent: Forwarder

");
                        Destination.NS.Write(DummyReq, 0, DummyReq.Length);
                        Destination.NS.Flush();
                        var tmpHeader = ReadHeaders(Destination);
                        ulong DiscardLength = 0UL;
                        if (tmpHeader.Header.ContainsKey("Content-Length") && ulong.TryParse(tmpHeader.Header["Content-Length"], out DiscardLength))
                        {
                            if (DiscardLength > 0)
                            {
                                if (!Discard(Destination.NS, DiscardLength))
                                {
                                    Dispose();
                                    return;
                                }
                            }
                            else
                            {
                                AnswerAndClose(UNSUPPORTED_METHOD, string.Format("{0}: Invalid Content-Length", Source.EP));
                            }
                        }
                        else if (tmpHeader.Header.ContainsKey("Transfer-Encoding") && tmpHeader.Header["Transfer-Encoding"].ToLower() == "chunked")
                        {
                            DiscardChunks(Destination.NS);
                        }

                        ForwarderEvent?.Invoke(this, ForwarderEventType.Established);
                    }
                    catch (Exception ex)
                    {
                        Log.E("Can't connect to server. {0}", ex.Message);
                        Source.NS.Close();
                        Source.NS.Dispose();
                        Source = null;
                        ForwarderEvent?.Invoke(this, ForwarderEventType.ServerUnavailable);
                        return;
                    }
                    //*/
                    if (Destination != null)
                    {
                        Log.D("{0}: Setting timeout to {1}", Source.EP, Timeout);
                        Source.NS.ReadTimeout = (Timeout == 0 ? System.Threading.Timeout.Infinite : 0);
                        //Destination.NS.ReadTimeout = (Timeout == 0 ? System.Threading.Timeout.Infinite : 0);
                        Source.Source.ReceiveTimeout = (Timeout == 0 ? System.Threading.Timeout.Infinite : 0);
                        //Destination.Source.ReceiveTimeout = (Timeout == 0 ? System.Threading.Timeout.Infinite : 0);

                        //Never wait for Server Response, just send to client.
                        //Log.D("{0}: Begin reading from Server", Source.EP);
                        //Destination.Handle = Destination.NS.BeginRead(Destination.Data, 0, BUFFER, DataIn, Destination);

                        //make sure we only operate on the connection,
                        //if there is actually one
                        while (Source != null &&
                            Source.NS != null &&
                            Destination != null)
                        {
                            ulong Length = 0;

                            //Read headers
                            var H = ReadHeaders(Source);

                            //If this is null it is probably not an HTTP connection
                            if (H == null)
                            {
                                //ReadHeaders can get rid of Source already
                                if (Source != null)
                                {
                                    Log.W("{0}: Invalid content. Not HTTP", Source.EP);
                                    Dispose();
                                }
                                return;
                            }
                            //Client gone
                            if (Source == null)
                            {
                                return;
                            }
                            Log.D("{0}: {1} {2}", Source.EP, H.Header[HEADER.METHOD], H.Header[HEADER.PATH]);
                            //Lenght based Request. Read entire body
                            if (H.Header.ContainsKey(HEADER.CONTENT_LENGTH) && ulong.TryParse(H.Header[HEADER.CONTENT_LENGTH], out Length) && Length > 0)
                            {
                                Log.D("{0}: Processing request body", Source.EP);
                                //Don't even start reading the content if it is too long.
                                if (Length > MAX_CONTENT_LENGTH)
                                {
#if CONSUME_REQUEST
                                    Log.D("{0}: Content Length too large", Source.EP);
                                    Discard(Source.NS, Length);
#endif
                                    DataTransmitted(this, Length);
                                    AnswerAndClose(LENGTH_EXCEEDED, "Client attempted to send too much content");
                                    return;
                                }
                                Length += (ulong)H.Raw.Length;
                                byte[] buffer = new byte[1024];
                                using (var MS = new MemoryStream())
                                {
                                    MS.Write(H.Raw, 0, H.Raw.Length);
                                    while ((ulong)MS.Position < Length)
                                    {
                                        int readed = Source.NS.Read(buffer, 0, Length - (ulong)MS.Position > (ulong)buffer.Length ? buffer.Length : (int)(Length - (ulong)MS.Position));
                                        if (readed > 0)
                                        {
                                            MS.Write(buffer, 0, readed);
                                        }
                                        else
                                        {
                                            AnswerAndClose(CONTENT_LENGTH_MISMATCH, "Not enough data in request");
                                            return;
                                        }
                                    }
                                    Log.D("{0}: Request fully received. Sending...", Source.EP);
                                    MS.Seek(0, SeekOrigin.Begin);
                                    buffer = MS.ToArray();
                                    if (/*SendToServer(H.Raw, 0, H.Raw.Length) &&*/
                                        SendToServer(buffer, 0, buffer.Length))
                                    {
                                        DataTransmitted?.Invoke(this, (ulong)(/*H.Raw.LongLength +*/ buffer.LongLength));
                                        Log.D("{0}: Request sent to server", Source.EP);
                                    }
                                    else
                                    {
                                        AnswerAndClose(SERVER_UNAVAILABLE, "Server Gone");
                                        return;
                                    }
                                }
                            }
                            //For GET requests, just write "AS-IS"
                            else if (
                                H.Header[HEADER.METHOD] == "GET" ||
                                H.Header[HEADER.METHOD] == "HEAD" ||
                                H.Header[HEADER.METHOD] == "OPTIONS")
                            {
                                Log.D("{0}: No reason to buffer request.", Source.EP);
                                if (SendToServer(H.Raw, 0, H.Raw.Length))
                                {
                                    DataTransmitted?.Invoke(this, (ulong)H.Raw.LongLength);
                                }
                                else
                                {
                                    AnswerAndClose(SERVER_UNAVAILABLE, "Server Gone");
                                    return;
                                }
                            }
                            //POST request without length
                            else if (H.Header[HEADER.METHOD] == "POST")
                            {
                                AnswerAndClose(LENGTH_EXPECTED, "POST request without content length");
                                return;
                            }
                            //Unsupported method
                            else
                            {
                                AnswerAndClose(UNSUPPORTED_METHOD, string.Format("{0}: Unsupported Header: {1}", Source.EP, H.Header[HEADER.METHOD]));
                            }
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = string.Format("Forwarder from {0}", Source.EP)
                }.Start();
            }
            else
            {
                throw new InvalidOperationException(Log.E("{0}: Forwarder already running", Source.EP));
            }
        }

        private bool SendToServer(byte[] Data, int Index, int Length)
        {
            if (Destination == null)
            {
                Log.W("{0}: Server gone?", Source.EP);
                Source.Other = Destination = new Connection()
                {
                    BytesReceived = 0,
                    Data = new byte[BUFFER],
                    Mode = Connection.ConnectionMode.Server,
                    EP = Dest,
                    Other = Source
                };
            }
            if (Destination.NS == null)
            {
                Destination.Source = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    Log.I("{0}: Connecting to server", Source.EP);
                    Destination.Source.Connect(Dest);
                    Destination.NS = new NetworkStream(Destination.Source, true);
                    Destination.EP = Dest;
                    Destination.NS.ReadTimeout = (Timeout == 0 ? System.Threading.Timeout.Infinite : 0);
                    Destination.Source.ReceiveTimeout = (Timeout == 0 ? System.Threading.Timeout.Infinite : 0);
                    Log.D("{0}: Begin reading from Server", Source.EP);
                    Destination.Handle = Destination.NS.BeginRead(Destination.Data, 0, BUFFER, DataIn, Destination);

                }
                catch (Exception ex)
                {
                    Log.E("Can't connect to server. {0}", ex.Message);
                    Source.NS.Close();
                    Source.NS.Dispose();
                    Source = null;
                    ForwarderEvent?.Invoke(this, ForwarderEventType.ServerUnavailable);
                    return false;
                }
            }
            try
            {
                Destination.NS.Write(Data, Index, Length);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Stop()
        {
            lock (this)
            {
                if (Destination == null || Source == null)
                {
                    return;
                }
                Log.I("{0}: Closing Forwarder", Source.EP);
                var Temp1 = Source;
                var Temp2 = Destination;
                Source = null;
                Destination = null;
                if (Temp1 != null && Temp1.NS != null)
                {
                    Log.D("{0}: Closing source connection", Temp1.EP);
                    Temp1.NS.Flush();
                    Temp1.NS.Close(1000);
                    Temp1.NS.Dispose();
                }
                if (Temp2 != null && Temp2.NS != null)
                {
                    Log.D("{0}: Closing server connection", Temp2.EP);
                    Temp2.NS.Flush();
                    Temp2.NS.Close(1000);
                    Temp2.NS.Dispose();
                }
                if (FS != null)
                {
                    Log.D("{0}: Closing file dump", Temp1.EP);
                    FS.Close();
                    FS.Dispose();
                    FS = null;
                }
                ForwarderEvent?.Invoke(this, ForwarderEventType.UserClosed);
                Log.D("{0}: Forwarder closed", Temp1.EP);
            }
        }

        public void Dispose()
        {
            if (Destination != null && Source != null)
            {
                Log.D("{0}: Disposing", Source != null ? Source.EP.ToString() : "?");
                Stop();
            }
        }

        private void AnswerAndClose(string Message, string Reason)
        {
            var Data = Encoding.UTF8.GetBytes(Message);
            Source.NS.Write(Data, 0, Data.Length);
            Log.W(Reason);
            Dispose();
        }

        private Headers ReadHeaders(Connection Source)
        {
            var H = new Headers();
            H.Header = new Dictionary<string, string>();
            byte[] b = new byte[1];
            using (MemoryStream MS = new MemoryStream())
            {
                while (MS.Position < MAX_HEADER_LENGTH && !IsDone(MS))
                {
                    try
                    {
                        if (Source.NS.Read(b, 0, b.Length) == 0)
                        {
                            Log.W("{0}: Unexpected end of stream when reading headers.", Source.EP);
                            return null;
                        }
                        //b = Source.NS.ReadByte();
                    }
                    catch (Exception ex)
                    {
                        Log.W("{0}: Can't read headers from source. {1}", Source.EP, ex.Message);
                        return null;
                    }
                    MS.WriteByte(b[0]);
                }
                //too many headers
                if (MS.Position >= MAX_HEADER_LENGTH)
                {
                    AnswerAndClose(HEADERS_TOO_LARGE, string.Format("{0}: Headers are too large", Source.EP));
                    return null;
                }
                Log.D("{0}: Adding 'X-Forwarded-For' header", Source.EP);
                MS.Seek(-2, SeekOrigin.End);
                var Data = Encoding.ASCII.GetBytes(string.Format("X-Forwarded-For: {0}\r\n\r\n", Source.EP.Address));
                MS.Write(Data, 0, Data.Length);
                MS.Seek(0, SeekOrigin.Begin);
                H.Raw = MS.ToArray();
                using (StreamReader S = new StreamReader(MS, Encoding.UTF8, false, 1, true))
                {
                    string Line = S.ReadLine();
                    //last line is empty
                    while (Line.Length > 0)
                    {
                        //first line is request type
                        if (H.Header.Count == 0)
                        {
                            Log.D("{0}: Processing first header line", Source.EP);
                            string[] Parts = Line.Split(' ');
                            if (Parts.Length > 2)
                            {
                                H.Header[HEADER.METHOD] = Parts[0].ToUpper();
                                H.Header[HEADER.PATH] = string.Join(" ", Parts, 1, Parts.Length - 2);
                                H.Header[HEADER.PROTOCOL] = Parts[Parts.Length - 1];
                            }
                            else
                            {
                                Log.W("{0}: Invalid first line of HTTP request", Source.EP);
                                //Invalid first header
                                return null;
                            }
                        }
                        //HTTP header. We ignore those with _ at the start
                        else if (Line.Contains(":") && !Line.StartsWith("_") && Line.IndexOf(':') < Line.Length - 2)
                        {
                            var Header = Line.Substring(0, Line.IndexOf(':'));
                            var Value = Line.Substring(Line.IndexOf(':') + 2);
                            if (Line.ToUpper().StartsWith("X-"))
                            {
                                Log.D("{0}: Ignoring custom header", Source.EP, Header);
                            }
                            else
                            {
                                Log.D("{0}: Got Header '{1}'", Source.EP, Header);
                                if (H.Header.ContainsKey(Header))
                                {
                                    Log.D("{0}: Appending to existing header", Source.EP);
                                    H.Header[Header] += "; " + Value;
                                }
                                else
                                {
                                    H.Header[Header] = Value;
                                }
                            }
                        }
                        Line = S.ReadLine();
                    }
                    //Make sure to overwrite this header in case the client wanted to specify it.
                    H.Header["X-Forwarded-For"] = Source.EP.Address.ToString();
                }
            }
            Log.D("{0}: Header processing done", Source.EP);
            return H;
        }

        /// <summary>
        /// Checks if all headers have been received
        /// </summary>
        /// <param name="S">Stream to read from</param>
        /// <returns>true, if all headers are received</returns>
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
                        FS = File.Create(string.Format(@"C:\Temp\dump_{0}.bin", DateTime.UtcNow.Ticks));
                    }
                    FS.Write(From.Data, 0, Readed);
                }
                From.BytesReceived += (ulong)Readed;
                DataTransmitted?.Invoke(this, (ulong)Readed);
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
                Log.I("{0}: {1} closed the connection", Source.EP, From.Mode);
                //ForwarderEvent?.Invoke(this, From == Source ? ForwarderEventType.ClientGone : ForwarderEventType.ServerGone);
                Destination.NS.Close();
                Destination.NS.Dispose();
                Destination = null;
            }
        }

        /*
        private string ReadLine(Stream S)
        {
            List<byte> b = new List<byte>();
            while (true)
            {
                int temp = 0;
                temp = S.ReadByte();
                if (temp < 0)
                {
                    return Encoding.UTF8.GetString(b.ToArray());
                }
                else
                {
                    b.Add((byte)temp);
                    if (b.Count > 1 && b[b.Count - 1] == 10 && b[b.Count - 2] == 13)
                    {
                        b.RemoveAt(b.Count - 1);
                        b.RemoveAt(b.Count - 1);
                        return Encoding.UTF8.GetString(b.ToArray());
                    }
                }
            }
        }

        private bool DiscardChunks(Stream NS)
        {
            Log.D("{0}: Discarding chunked response", Source.EP);
            string Line;
            int Length;
            while (true)
            {
                try
                {
                    Line = ReadLine(NS);
                }
                catch
                {
                    return false;
                }
                if (int.TryParse(Line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out Length))
                {
                    if (Length == 0)
                    {
                        Log.D("{0}: Discarding chunked response [DONE]", Source.EP);
                        return Discard(NS, 2UL);
                    }
                    if (!Discard(NS, (ulong)(Length + 2)))
                    {
                        return false;
                    }
                }
            }
        }
        //*/

        private bool Discard(Stream S, ulong count)
        {
            Log.D("{0}: Discarding {1} bytes", Source.EP, count);
            ulong readed = 0UL;
            byte[] buffer = new byte[(count > MAX_HEADER_LENGTH ? MAX_HEADER_LENGTH : (int)count)];
            while (readed < count)
            {
                try
                {
                    readed += (ulong)S.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    Log.W("{0}: Unexpected end of stream while discarding", Source.EP);
                    return false;
                }
            }
            Log.D("{0}: Discarding [DONE]", Source.EP);
            return true;
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

        public override string ToString()
        {
            if (Source != null)
            {
                return string.Format("Forwarder: {0} --> {1}", Source.EP, Destination != null ? Destination.EP.ToString() : "<none>");
            }
            return base.ToString();
        }
    }
}
