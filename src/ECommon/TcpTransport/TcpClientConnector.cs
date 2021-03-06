using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ECommon.Configurations;
using ECommon.Utilities;

namespace ECommon.TcpTransport
{
    public class TcpClientConnector
    {
        private const int CheckPeriodMs = 200;
        private TcpConfiguration _tcpConfiguration;
        private readonly SocketArgsPool _connectSocketArgsPool;
        private readonly ConcurrentDictionary<Guid, PendingConnection> _pendingConections;
        private readonly Timer _timer;

        public TcpClientConnector()
        {
            _tcpConfiguration = Configuration.Instance.Setting.TcpConfiguration;
            _connectSocketArgsPool = new SocketArgsPool("TcpClientConnector._connectSocketArgsPool", _tcpConfiguration.ConnectPoolSize, CreateConnectSocketArgs);
            _pendingConections = new ConcurrentDictionary<Guid, PendingConnection>();
            _timer = new Timer(TimerCallback, null, CheckPeriodMs, Timeout.Infinite);
        }

        private SocketAsyncEventArgs CreateConnectSocketArgs()
        {
            var socketArgs = new SocketAsyncEventArgs();
            socketArgs.Completed += ConnectCompleted;
            socketArgs.UserToken = new CallbacksStateToken();
            return socketArgs;
        }

        public ITcpConnection ConnectTo(Guid connectionId,
                                        IPEndPoint localEndPoint,
                                        IPEndPoint remoteEndPoint,
                                        Action<ITcpConnection> onConnectionEstablished = null,
                                        Action<ITcpConnection, SocketError> onConnectionFailed = null)
        {
            Ensure.NotNull(remoteEndPoint, "remoteEndPoint");
            return TcpConnection.CreateConnectingTcpConnection(connectionId, localEndPoint, remoteEndPoint, this, onConnectionEstablished, onConnectionFailed);
        }

        internal void InitConnect(IPEndPoint localEndPoint,
                                  IPEndPoint serverEndPoint,
                                  Action<IPEndPoint, Socket> onConnectionEstablished,
                                  Action<IPEndPoint, SocketError> onConnectionFailed,
                                  ITcpConnection connection)
        {
            if (serverEndPoint == null)
                throw new ArgumentNullException("serverEndPoint");
            if (onConnectionEstablished == null)
                throw new ArgumentNullException("onConnectionEstablished");
            if (onConnectionFailed == null)
                throw new ArgumentNullException("onConnectionFailed");

            var socketArgs = _connectSocketArgsPool.Get();
            var connectingSocket = new Socket(serverEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socketArgs.RemoteEndPoint = serverEndPoint;
            socketArgs.AcceptSocket = connectingSocket;
            var callbacks = (CallbacksStateToken)socketArgs.UserToken;
            callbacks.OnConnectionEstablished = onConnectionEstablished;
            callbacks.OnConnectionFailed = onConnectionFailed;
            callbacks.PendingConnection = new PendingConnection(connection, DateTime.UtcNow.Add(_tcpConfiguration.ConnectionTimeout));

            AddToConnecting(callbacks.PendingConnection);

            try
            {
                if (localEndPoint != null)
                {
                    connectingSocket.Bind(localEndPoint);
                }
                var firedAsync = connectingSocket.ConnectAsync(socketArgs);
                if (!firedAsync)
                    ProcessConnect(socketArgs);
            }
            catch (ObjectDisposedException)
            {
                HandleBadConnect(socketArgs);
            }
        }

        private void ConnectCompleted(object sender, SocketAsyncEventArgs e)
        {
            ProcessConnect(e);
        }

        private void ProcessConnect(SocketAsyncEventArgs e)
        {
            if (e.SocketError != SocketError.Success)
                HandleBadConnect(e);
            else
                OnSocketConnected(e);
        }

        private void HandleBadConnect(SocketAsyncEventArgs socketArgs)
        {
            var serverEndPoint = socketArgs.RemoteEndPoint;
            var socketError = socketArgs.SocketError;
            var callbacks = (CallbacksStateToken)socketArgs.UserToken;
            var onConnectionFailed = callbacks.OnConnectionFailed;
            var pendingConnection = callbacks.PendingConnection;

            Helper.EatException(() => socketArgs.AcceptSocket.Close(_tcpConfiguration.SocketCloseTimeoutMs));
            socketArgs.AcceptSocket = null;
            callbacks.Reset();
            _connectSocketArgsPool.Return(socketArgs);

            if (RemoveFromConnecting(pendingConnection))
                onConnectionFailed((IPEndPoint)serverEndPoint, socketError);
        }

        private void OnSocketConnected(SocketAsyncEventArgs socketArgs)
        {
            var remoteEndPoint = (IPEndPoint)socketArgs.RemoteEndPoint;
            var socket = socketArgs.AcceptSocket;
            var callbacks = (CallbacksStateToken)socketArgs.UserToken;
            var onConnectionEstablished = callbacks.OnConnectionEstablished;
            var pendingConnection = callbacks.PendingConnection;

            socketArgs.AcceptSocket = null;
            callbacks.Reset();
            _connectSocketArgsPool.Return(socketArgs);

            if (RemoveFromConnecting(pendingConnection))
                onConnectionEstablished(remoteEndPoint, socket);
        }

        private void TimerCallback(object state)
        {
            foreach (var pendingConnection in _pendingConections.Values)
            {
                if (DateTime.UtcNow >= pendingConnection.WhenToKill && RemoveFromConnecting(pendingConnection))
                {
                    Helper.EatException(() => pendingConnection.Connection.Close(SocketError.TimedOut, "Connection establishment timeout."));
                }
            }
            _timer.Change(CheckPeriodMs, Timeout.Infinite);
        }

        private void AddToConnecting(PendingConnection pendingConnection)
        {
            _pendingConections.TryAdd(pendingConnection.Connection.ConnectionId, pendingConnection);
        }

        private bool RemoveFromConnecting(PendingConnection pendingConnection)
        {
            PendingConnection conn;
            return _pendingConections.TryRemove(pendingConnection.Connection.ConnectionId, out conn)
                   && Interlocked.CompareExchange(ref conn.Done, 1, 0) == 0;
        }

        private class CallbacksStateToken
        {
            public Action<IPEndPoint, Socket> OnConnectionEstablished;
            public Action<IPEndPoint, SocketError> OnConnectionFailed;
            public PendingConnection PendingConnection;

            public void Reset()
            {
                OnConnectionEstablished = null;
                OnConnectionFailed = null;
                PendingConnection = null;
            }
        }

        private class PendingConnection
        {
            public readonly ITcpConnection Connection;
            public readonly DateTime WhenToKill;
            public int Done;

            public PendingConnection(ITcpConnection connection, DateTime whenToKill)
            {
                Connection = connection;
                WhenToKill = whenToKill;
            }
        }
    }
}