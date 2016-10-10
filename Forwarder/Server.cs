using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace Forwarder
{
    public delegate void ConnectionHandler(object Sender, Socket Connection);

    public class Server : IDisposable
    {
        /// <summary>
        /// Called, upon connections. If not bound, the connections will be terminated immediately.
        /// </summary>
        public event ConnectionHandler Connection;

        /// <summary>
        /// If true, the event will be called in an asynchronous matter.
        /// This can be dangerous if connections arrive faster then you can handle it.
        /// </summary>
        public bool UseAsync { get; set; } = false;

        private IPEndPoint LocalEP;
        private TcpListener Listener;

        public Server(int LocalPort)
        {
            if (!IsValidPort(LocalPort))
            {
                throw new ArgumentOutOfRangeException();
            }
            LocalEP = new IPEndPoint(IPAddress.Any, LocalPort);
        }

        public Server(IPEndPoint LocalLocation)
        {
            if (LocalLocation == null)
            {
                throw new ArgumentNullException();
            }
            LocalEP = LocalLocation;
        }

        public Server(string LocalIP, int LocalPort)
        {
            if (!IsValidAddress(LocalIP))
            {
                throw new ArgumentException("IP address not valid");
            }
            if (!IsValidPort(LocalPort))
            {
                throw new ArgumentOutOfRangeException();
            }
            LocalEP = new IPEndPoint(IPAddress.Parse(LocalIP), LocalPort);
        }

        public void Start()
        {
            if (Listener != null)
            {
                throw new InvalidOperationException("Server already running");
            }
            Listener = new TcpListener(LocalEP);
            Listener.Start();
            Listener.BeginAcceptSocket(con, null);
        }

        public void Stop()
        {
            if (Listener == null)
            {
                throw new InvalidOperationException("Server already stopped");
            }
            //upon calling "Stop", the awaiting async operation will complete.
            //To prevent it from crashing we set listener to null first.
            var Temp = Listener;
            Listener = null;
            Temp.Stop();
            Temp = null;
        }

        private void con(IAsyncResult ar)
        {
            //server closed if listener is null
            if (Listener == null)
            {
                return;
            }
            Socket S = null;
            try
            {
                S = Listener.EndAcceptSocket(ar);
            }
            catch
            {
                //This can happen if a connection was closed before we could accept it.
            }
            if (S != null)
            {
                //if no event is present, just close the connection again.
                if (Connection == null)
                {
                    S.Close();
                }
                else
                {
                    if (UseAsync)
                    {
                        //invoke the event asynchronously
                        new Thread(delegate ()
                        {
                            Connection?.Invoke(this, S);
                        }).Start();
                    }
                    else
                    {
                        Connection?.Invoke(this, S);
                    }
                }
            }
            Listener.BeginAcceptSocket(con, null);
        }

        public void Dispose()
        {
            if (Listener != null)
            {
                Stop();
            }
        }

        public static bool IsValidAddress(string IP)
        {
            IPAddress A = IPAddress.Any;
            return IPAddress.TryParse(IP, out A);
        }

        public static bool IsValidPort(int Port)
        {
            return Port >= IPEndPoint.MinPort && Port <= IPEndPoint.MaxPort;
        }

        public static IPAddress[] GetLocalAddresses()
        {
            List<IPAddress> Addresses = new List<IPAddress>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                {
                    Addresses.Add(ip.Address);
                }
            }
            return Addresses.ToArray();
        }
    }
}
