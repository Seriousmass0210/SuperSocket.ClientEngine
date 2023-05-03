using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace SuperSocket.ClientEngine
{
    public class AsyncTcpSession : TcpClientSession
    {
        private SocketAsyncEventArgs m_recvSocketEventArgs;
        private SocketAsyncEventArgs m_sendSocketEventArgs;
        private SpinLock m_closeLocker;
        private bool m_IsClosing;

        public AsyncTcpSession()
            : base()
        {

        }

        protected override void SocketEventArgsCompleted(object sender, SocketAsyncEventArgs e)
        {
            if (e.LastOperation == SocketAsyncOperation.Connect)
            {
                ProcessConnect(sender as Socket, null, e, null);
                return;
            }

            ProcessReceive(e);
        }

        protected override void SetBuffer(ArraySegment<byte> bufferSegment)
        {
            base.SetBuffer(bufferSegment);

            m_recvSocketEventArgs?.SetBuffer(bufferSegment.Array, bufferSegment.Offset, bufferSegment.Count);
        }

        protected override void OnGetSocket(SocketAsyncEventArgs e)
        {
            if (Buffer.Array == null)
            {
                var receiveBufferSize = ReceiveBufferSize;

                if (receiveBufferSize <= 0)
                    receiveBufferSize = DefaultReceiveBufferSize;

                ReceiveBufferSize = receiveBufferSize;

                Buffer = new ArraySegment<byte>(new byte[receiveBufferSize]);
            }

            e.SetBuffer(Buffer.Array, Buffer.Offset, Buffer.Count);

            m_recvSocketEventArgs = e;

            OnConnected();
            StartReceive();
        }

        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            SocketAsyncEventArgs args = e;
            if (args == null)
                return;

            if (args.SocketError != SocketError.Success)
            {
                if (EnsureSocketClosed())
                    OnClosed();
                if (!IsIgnorableSocketError((int)args.SocketError))
                    OnError(new SocketException((int)args.SocketError));
                return;
            }

            if (args.BytesTransferred == 0)
            {
                if (EnsureSocketClosed())
                    OnClosed();
                return;
            }

            OnDataReceived(args.Buffer, args.Offset, args.BytesTransferred);
            StartReceive();
        }

        void StartReceive()
        {
            SocketAsyncEventArgs args = m_recvSocketEventArgs;
            if (args == null)
                return;

            bool raiseEvent;

            var client = Client;

            if (client == null)
                return;

            try
            {
                raiseEvent = client.ReceiveAsync(args);
            }
            catch (SocketException ex)
            {
                int errorCode;

#if !NETFX_CORE
                errorCode = ex.ErrorCode;
#else
                errorCode = (int)ex.SocketErrorCode;
#endif

                if (!IsIgnorableSocketError(errorCode))
                    OnError(ex);

                if (EnsureSocketClosed(client))
                    OnClosed();

                return;
            }
            catch (Exception e)
            {
                if (!IsIgnorableException(e))
                    OnError(e);

                if (EnsureSocketClosed(client))
                    OnClosed();

                return;
            }

            if (!raiseEvent)
                ProcessReceive(args);
        }

        protected override void SendInternal(PosList<ArraySegment<byte>> items)
        {
            bool lockTaken = false;

            try
            {
                m_closeLocker.Enter(ref lockTaken);
                if (!m_IsClosing && m_sendSocketEventArgs == null)
                {
                    m_sendSocketEventArgs = new SocketAsyncEventArgs();
                    m_sendSocketEventArgs.Completed += new EventHandler<SocketAsyncEventArgs>(Sending_Completed);
                }
            }
            finally
            {
                if (lockTaken)
                    m_closeLocker.Exit();
            }

            SocketAsyncEventArgs args = m_sendSocketEventArgs;
            if (args == null)
                return;

            bool raiseEvent;

            try
            {
                if (items.Count > 1)
                {
                    if (args.Buffer != null)
                        args.SetBuffer(null, 0, 0);

                    args.BufferList = items;
                }
                else
                {
                    var currentItem = items[0];

                    try
                    {
                        if (args.BufferList != null)
                            args.BufferList = null;
                    }
                    catch//a strange NullReference exception
                    {
                    }

                    args.SetBuffer(currentItem.Array, currentItem.Offset, currentItem.Count);
                }


                raiseEvent = Client.SendAsync(args);
            }
            catch (SocketException ex)
            {
                int errorCode;

#if !NETFX_CORE
                errorCode = ex.ErrorCode;
#else
                errorCode = (int)ex.SocketErrorCode;
#endif

                if (EnsureSocketClosed() && !IsIgnorableSocketError(errorCode))
                    OnError(ex);

                return;
            }
            catch (Exception ex)
            {
                if (EnsureSocketClosed() && IsIgnorableException(ex))
                    OnError(ex);
                return;
            }

            if (!raiseEvent)
                Sending_Completed(Client, args);
        }

        void Sending_Completed(object sender, SocketAsyncEventArgs e)
        {
            SocketAsyncEventArgs args = e;
            if (args == null)
                return;

            if (args.SocketError != SocketError.Success || args.BytesTransferred == 0)
            {
                if (EnsureSocketClosed())
                    OnClosed();

                if (args.SocketError != SocketError.Success && !IsIgnorableSocketError((int)args.SocketError))
                    OnError(new SocketException((int)args.SocketError));

                return;
            }

            OnSendingCompleted();
        }

        protected override void OnClosed()
        {
            bool lockTaken = false;

            try
            {
                m_closeLocker.Enter(ref lockTaken);

                m_IsClosing = true;

                m_sendSocketEventArgs?.Dispose();
                m_sendSocketEventArgs = null;

                m_recvSocketEventArgs?.Dispose();
                m_recvSocketEventArgs = null;

                base.OnClosed();
            }
            finally
            {
                if (lockTaken)
                    m_closeLocker.Exit();
            }
        }
    }
}
