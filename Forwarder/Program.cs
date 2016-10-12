using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Forwarder
{
    static class Program
    {
        public const int INFITITE = 0;
        private struct Arguments
        {
            public IPEndPoint Listener;
            public IPEndPoint Destination;
            public int Timeout;
            public bool Valid;
            public bool Help;
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static int Main(string[] Args)
        {
            ulong BytesTransmitted = 0;
            List<Forwarder> Connections = new List<Forwarder>();
#if DEBUG
            Args = new string[]
            {
                "127.0.0.1:8080",
                "46.140.111.85:80",
                "5000"
            };
#endif
            var A = ParseArgs(Args);
            if (A.Help || !A.Valid)
            {
                Console.Error.WriteLine(@"Forwarder <source> <destination> [timeout]
Forwards TCP connections from source to destination.

source       - Source to listen on.
               Either in the format IP:PORT or as PORT only.
               Using only the port number listens on all interfaces.
destination  - Destination to forward to.
               In the format IP:PORT
timeout      - How long a connection can sit inactive until it is closed.
               Defaults to {0}, which indicates infinite.
               Time is in milliseconds.
", INFITITE);
                Console.Error.WriteLine("List of local addresses:\r\n{0}",
                    //Note. This cast is required to prevent CS0121
                    string.Join("\r\n", (IEnumerable<IPAddress>)Server.GetLocalAddresses())
                    );
#if DEBUG
                End();
#endif
                return 1;
            }
            var Listener = new Server(A.Listener);
            Listener.Connection += delegate (object sender, Socket Connection)
            {
                Console.Error.WriteLine("Got connection");
                Forwarder F = new Forwarder(Connection, A.Destination);
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
                F.Start(A.Timeout);
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

            Listener.Start();
            Console.WriteLine("Forwarding from {0} to {1} | Press [ESC] to stop", A.Listener, A.Destination);
            while (Console.ReadKey(true).Key != ConsoleKey.Escape) ;
            Listener.Stop();
#if DEBUG
            End();
#endif
            return 0;
        }
#if DEBUG
        private static void End()
        {
            Console.WriteLine("#END");
            while (Console.KeyAvailable)
            {
                Console.ReadKey(true);
            }
            Console.ReadKey(true);
        }
#endif

        /// <summary>
        /// Parses command line arguments
        /// </summary>
        /// <param name="Args">Arguments</param>
        /// <returns>Parsed Arguments</returns>
        private static Arguments ParseArgs(string[] Args)
        {
            List<string> A = new List<string>(Args);
            Arguments Result = new Arguments()
            {
                Destination = null,
                Listener = null,
                Timeout = INFITITE,
                Valid = false,
                Help = false
            };

            while (A.Count > 0)
            {
                var Current = A[0];
                A.RemoveAt(0);
                //Stop parsing on help request
                if (Current.ToLower() == "/h" || Current == "/?")
                {
                    Result.Help = Result.Valid = true;
                    return Result;
                }
                else
                {
                    if (Result.Listener == null)
                    {
                        try
                        {
                            Result.Listener = ParseEndpoint(Current);
                            if (!IsValidBindIP(Result.Listener.Address))
                            {
                                Console.Error.WriteLine("{0} is not a valid local listener IP", Result.Listener.Address);
                                Result.Valid = false;
                                return Result;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(ex.Message);
                            Result.Valid = false;
                            return Result;
                        }
                    }
                    else if (Result.Destination == null)
                    {
                        try
                        {
                            Result.Destination = ParseEndpoint(Current);
                            //We might check here eventually if the Destination IP is actually routable.
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine(ex.Message);
                            Result.Valid = false;
                            return Result;
                        }
                    }
                    else
                    {
                        if (int.TryParse(Current, out Result.Timeout))
                        {
                            if (Result.Timeout < 0)
                            {
                                Console.Error.WriteLine("Invalid Timeout value (must be 0 or bigger)");
                                Result.Valid = false;
                                return Result;
                            }
                        }
                        else
                        {
                            Console.Error.WriteLine("Invalid Timeout value (not a number)");
                            Result.Valid = false;
                            return Result;
                        }
                    }
                }
            }
            Result.Valid = (Result.Listener != null && Result.Destination != null && Result.Timeout >= 0);
            return Result;
        }

        /// <summary>
        /// This is the most primitive IPEndPoint Parser you will ever see.
        /// </summary>
        /// <param name="S">String, either as IP:PORT or [IP]:PORT</param>
        /// <remarks>
        /// Because the user must supply a port we could avoid the brackets
        /// and always assume the last segment is the port. However this can
        /// lead to errors more easily so we aren't doing it.
        /// </remarks>
        /// <returns>Parset IPEndpoint</returns>
        private static IPEndPoint ParseEndpoint(string S)
        {
            if (string.IsNullOrEmpty(S))
            {
                throw new ArgumentException("Argument is required and must not be empty or null");
            }
            IPAddress Addr = IPAddress.Any;
            int Port = 0;
            //extract IPv6 Address
            if (S.Contains("[") || S.Contains("]")) //probably IPv6, but this test would work for IPv4 too
            {
                if (
                    //make sure there is a closing bracket after the opening bracket
                    S.IndexOf(']') > S.IndexOf('[') &&
                    //make sure the IP starts with the opening bracket
                    S.StartsWith("[") &&
                    //make sure the is only one of each bracket type
                    S.IndexOf('[') == S.LastIndexOf('[') && S.IndexOf(']') == S.LastIndexOf(']'))
                {
                    if (!IPAddress.TryParse(S.Substring(1, S.IndexOf(']') - 1), out Addr))
                    {
                        throw new ArgumentException("Invalid IPv6 Address");
                    }
                    else
                    {
                        if (!int.TryParse(S.Substring(S.IndexOf(']')).Split(':').Last(), out Port))
                        {
                            throw new ArgumentException("Invalid port number");
                        }
                    }
                }
                else
                {
                    throw new ArgumentException("Invalid IPv6 Address (check brackets)");
                }
            }
            else if (S.Contains(":")) //IPv4 Address (no brackets)
            {
                if (!IPAddress.TryParse(S.Substring(0, S.LastIndexOf(':')), out Addr))
                {
                    //maybe the user meant to use IPv6 but did it wrong
                    if (S.LastIndexOf(':') == S.IndexOf(':'))
                    {
                        throw new ArgumentException("Invalid IPv4 Address");
                    }
                    else
                    {
                        throw new ArgumentException("Invalid IPv4 Address. Use square brackets for IPv6. Example: [::1]:8080");
                    }
                }
                else
                {
                    if (!int.TryParse(S.Split(':').Last(), out Port))
                    {
                        throw new ArgumentException("Invalid port number");
                    }
                }
            }
            else //Port only
            {
                if (!IsPort(S))
                {
                    throw new ArgumentException("Invalid port number");
                }
                Port = int.Parse(S);
            }
            //IP and port are set properly by now or an exception was thrown
            return new IPEndPoint(Addr, Port);

        }

        /// <summary>
        /// Tests, if the given IP can be used for local binding.
        /// </summary>
        /// <param name="Addr">IP Address</param>
        /// <returns>True, if valid source IP</returns>
        private static bool IsValidBindIP(IPAddress Addr)
        {
            return IPAddress.IsLoopback(Addr) ||
                IPAddress.Any == Addr ||
                Server.GetLocalAddresses().Contains(Addr);
        }

        /// <summary>
        /// Tests if the given string is a valid IP address
        /// </summary>
        /// <remarks>This is a pure "TryParse" test.</remarks>
        /// <param name="Result">IP Address</param>
        /// <returns>True if valid</returns>
        private static bool IsValidIP(string Result)
        {
            IPAddress A = IPAddress.Any;
            return IPAddress.TryParse(Result, out A);
        }

        /// <summary>
        /// Tests if the given number is a valid port.
        /// </summary>
        /// <remarks>This will not test if the port is in use or not.</remarks>
        /// <param name="Result">Port number to check</param>
        /// <returns>True, if valid port number</returns>
        private static bool IsPort(string Result)
        {
            int Port = 0;
            return int.TryParse(Result, out Port) && Server.IsValidPort(Port);
        }

        /// <summary>
        /// Formats a size in bytes 
        /// </summary>
        /// <param name="Size">Size in bytes</param>
        /// <returns>Size string in human readable format</returns>
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
            //no decimal place if result is still in bytes
            return string.Format((index == 0 ? "{0}" : "{0:0.0}") + " {1}", Size, Sizes[index]);
        }
    }
}
