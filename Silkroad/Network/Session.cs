﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Silkroad.Network.Messaging;
using Silkroad.Network.Messaging.Handshake;
using Silkroad.Network.Messaging.Protocol;
using Silkroad.Security;

namespace Silkroad.Network {
    /// <summary>
    ///     Implements a Silkroad session interface.
    /// </summary>
    public class Session : IDisposable {
        public delegate Task MessageHandler(Session s, Message m);

        /// <summary>
        ///     A list of registered handlers.
        /// </summary>
        private readonly List<MessageHandler> _handlers = new List<MessageHandler>();

        /// <summary>
        ///     The underlying socket.
        /// </summary>
        private readonly Socket _socket;

        /// <summary>
        ///     The Silkroad message protocol.
        /// </summary>
        internal readonly MessageProtocol Protocol;

        /// <summary>
        ///     Initializes a new session, this should be used by clients, as it will setup the client protocol.
        /// </summary>
        public Session() {
            this._socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this.Protocol = new ClientMessageProtocol();
            this.RegisterService<ClientHandshakeService>();
        }

        /// <summary>
        ///     Initializes a new session with a giving <see cref="Socket" />, this should be only
        ///     used in servers, as it will setup the server protocol.
        /// </summary>
        /// <param name="socket"></param>
        public Session(Socket socket) {
            this._socket = socket;
            this.Protocol = new ServerMessageProtocol();
            this.RegisterService<ServerHandshakeService>();
        }

        /// <summary>
        ///     Indicates if the session is connected and not closed.
        /// </summary>
        public bool Connected => this._socket.Connected;

        /// <summary>
        ///     Indicates if the Handshake process is done, and the session is ready to use.
        /// </summary>
        public bool Ready => this.Protocol.Ready;

        public void Dispose() {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~Session() {
            this.Dispose(false);
        }

        /// <summary>
        ///     Registers a service to be invoked when <see cref="RespondAsync" /> is called.
        ///     The same service type cannot be registered twice.
        /// </summary>
        /// <param name="service">The service to be registered.</param>
        /// <typeparam name="T">The service type.</typeparam>
        public void RegisterService<T>(T service) where T : class {
            if (this.FindService<T>() == null) {
                foreach (var m in from method in service.GetType()
                        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    where method.GetCustomAttribute<MessageServiceAttribute>() != null
                    select method) {
                    this._handlers.Add((MessageHandler) Delegate.CreateDelegate(typeof(MessageHandler), service, m));
                }
            }
        }

        /// <summary>
        ///     Registers a service with parameter-less constructor to be invoked
        ///     when <see cref="RespondAsync" /> is called. The same service type cannot be registered twice.
        /// </summary>
        /// <typeparam name="T">The service type.</typeparam>
        public void RegisterService<T>() where T : class {
            this.RegisterService(Activator.CreateInstance<T>());
        }

        /// <summary>
        ///     Finds a service in the registered services.
        /// </summary>
        /// <typeparam name="T">The service type.</typeparam>
        /// <returns>The requested service, or <c>null</c> if it's registered.</returns>
        public T FindService<T>() where T : class {
            return (T) this._handlers.FirstOrDefault(h => h.Target?.GetType() == typeof(T))?.Target;
        }

        /// <summary>
        ///     Registers a method handler to be invoked when <see cref="RespondAsync" /> is called. The same handler type cannot
        ///     be registered twice.
        /// </summary>
        /// <param name="handler">The handler to register.</param>
        public void RegisterHandler(MessageHandler handler) {
            if (this._handlers.FirstOrDefault(h => h == handler) == null) {
                this._handlers.Add(handler);
            }
        }

        /// <summary>
        ///     Establishes a connection to a remote host.
        /// </summary>
        /// <param name="ip">The host's IP Address.</param>
        /// <param name="port">The host's Port.</param>
        /// <returns></returns>
        public Task ConnectAsync(string ip, int port) {
            // When attempting to reconnect we have to reset the protocol
            // or we will put it in a invalid status.
            this.Protocol.Option = MessageProtocolOption.None;
            this.Protocol.State = MessageProtocolState.WaitSetup;

            return this._socket.ConnectAsync(ip, port);
        }

        /// <summary>
        ///     Sends a <see cref="Message" /> to a connected session.
        /// </summary>
        /// <param name="msg">The message to be sent.</param>
        /// <returns></returns>
        public async Task SendAsync(Message msg) {
            if (msg.Massive) {
                var size = msg.Size;
                var chunks = (ushort) Math.Ceiling(size / (float) Message.BufferSize);

                var header = new Message(Opcodes.MASSIVE, 5);
                header.Write(true);
                header.Write(chunks);
                header.Write(msg.ID.Value);
                await this._socket.SendAsync(this.Protocol.Encode(header), SocketFlags.None).ConfigureAwait(false);

                for (var i = 0; i < chunks; i++) {
                    var len = Math.Min(Message.BufferSize, size);

                    var chunk = new Message(Opcodes.MASSIVE, (ushort) (len + 1));
                    chunk.Write(false);
                    chunk.Write<byte>(msg.AsDataSpan().Slice(i * Message.BufferSize, len));
                    await this._socket.SendAsync(this.Protocol.Encode(header), SocketFlags.None).ConfigureAwait(false);
                }
            } else {
                await this._socket.SendAsync(this.Protocol.Encode(msg), SocketFlags.None).ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     Closes the session connection and allow reuse of the underlying socket.
        ///     If the session is already closed, this method would do nothing.
        /// </summary>
        /// <returns></returns>
        public Task DisconnectAsync() {
            return !this._socket.Connected
                ? Task.CompletedTask
                : Task.Factory.FromAsync(this._socket.BeginDisconnect, this._socket.EndDisconnect, true, null);
        }

        /// <summary>
        ///     Receives a complete <see cref="Message" /> from a connected session. This method would
        ///     close the session if a protocol error acquired returning <c>null</c>.
        /// </summary>
        /// <returns>
        ///     The received <see cref="Message" />, could be <c>null</c> if the session
        ///     was closed by the connected pair, or if any protocol error acquired.
        /// </returns>
        public async Task<Message> ReceiveAsync() {
            Message massiveMsg = null;
            ushort massiveCnt = 0;

            while (true) {
                // for receiving a complete MASSIVE message.
                var sizeBuffer = new byte[2]; // 2 = Unsafe.SizeOf<MessageSize>()
                await this.ReceiveExactAsync(sizeBuffer.AsMemory(), SocketFlags.None).ConfigureAwait(false);
                if (!this._socket.Connected) {
                    return null;
                }

                var size = MemoryMarshal.Read<MessageSize>(sizeBuffer);
                var remaining = size.Encrypted && this.Protocol.Option.HasFlag(MessageProtocolOption.Encryption)
                    ? Blowfish.GetOutputLength(Message.EncryptSize + size.DataSize)
                    : Message.EncryptSize + size.DataSize;

                var buffer = new byte[remaining];
                await this.ReceiveExactAsync(buffer.AsMemory(), SocketFlags.None).ConfigureAwait(false);
                if (!this._socket.Connected) {
                    return null;
                }

                try {
                    var msg = this.Protocol.Decode(size, buffer.AsSpan());

                    if (msg.ID.Value == Opcodes.MASSIVE) {
                        var isHeader = msg.Read<bool>();
                        if (isHeader && massiveMsg == null && massiveCnt == 0) {
                            massiveCnt = msg.Read<ushort>();
                            massiveMsg = new Message(msg.Read<ushort>(), false, true);
                        } else if (!isHeader && massiveMsg != null && massiveCnt != 0) {
                            massiveCnt--;
                            massiveMsg.Write<byte>(msg.AsDataSpan().Slice(1));
                        } else {
                            // "corrupted massive message"
                            await this.DisconnectAsync().ConfigureAwait(false);
                            return null;
                        }

                        if (massiveCnt == 0) {
                            return massiveMsg;
                        }
                    } else {
                        return msg;
                    }
                } catch {
                    // "received message cannot be decoded."
                    await this.DisconnectAsync().ConfigureAwait(false);
                    return null;
                }
            }
        }

        /// <summary>
        ///     Receives from a connected session, until buffer is completely filled.
        /// </summary>
        /// <param name="buffer">The buffer to receive into.</param>
        /// <param name="flags">The socket receiving flag</param>
        /// <returns></returns>
        private async Task ReceiveExactAsync(Memory<byte> buffer, SocketFlags flags) {
            var received = 0;
            var remaining = buffer.Length;
            while (received < remaining) {
                // System.Net.Sockets.SocketException (10057)
                var receivedChunk = await this._socket.ReceiveAsync(buffer.Slice(received), flags)
                    .ConfigureAwait(false);

                if (receivedChunk <= 0) {
                    // "you have been disconnected"
                    await this.DisconnectAsync().ConfigureAwait(false);
                    return;
                }

                received += receivedChunk;
            }
        }

        /// <summary>
        ///     Responds to a <see cref="Message" /> by invoking all the registered services.
        ///     This method would close the session if any service throw an exception.
        ///     This method would return without doing anything if the passed message is <c>null</c>.
        /// </summary>
        /// <param name="msg">The message to respond to.</param>
        /// <returns></returns>
        public async Task RespondAsync(Message msg) {
            if (msg == null) {
                return;
            }

            foreach (var handler in from handler in this._handlers
                where handler.GetMethodInfo()?.GetCustomAttribute<MessageServiceAttribute>()?.Opcode == msg.ID.Value
                select handler) {
                await handler.Invoke(this, msg);
            }
        }

        /// <summary>
        ///     Completes the Handshake process, after calling this method <see cref="Ready" /> should
        ///     return trues, if the session is not closed somehow.
        /// </summary>
        /// <returns></returns>
        public async Task HandshakeAsync() {
            var server = this.FindService<ServerHandshakeService>();
            if (server != null) {
                await server.Begin(this).ConfigureAwait(false);
            }

            // Duplicated in RunAsync()
            while (!this.Ready) {
                var msg = await this.ReceiveAsync().ConfigureAwait(false);
                // We can't pass a null to RespondAsync() as it would return.
                // and this would keep the loop running even when the session is closed.
                // which is not what we want of course.
                if (msg == null) {
                    break;
                }

                await this.RespondAsync(msg).ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     Runs the session until it's closed somehow. This behavior is accomplished by continue
        ///     receiving via <see cref="ReceiveAsync" /> and responding with <see cref="RespondAsync" />.
        /// </summary>
        /// <returns></returns>
        public async Task RunAsync() {
            // Just want to ensure that the handshake is done.
            await this.HandshakeAsync();

            while (true) {
                var msg = await this.ReceiveAsync().ConfigureAwait(false);
                // We can't pass a null to RespondAsync() as it would return.
                // and this would keep the loop running even when the session is closed.
                // which is not what we want of course.
                if (msg == null) {
                    break;
                }

                await this.RespondAsync(msg).ConfigureAwait(false);
            }
        }

        private void ReleaseUnmanagedResources() {
            this._handlers.Clear();
        }

        private void Dispose(bool disposing) {
            this.ReleaseUnmanagedResources();
            if (disposing) {
                this._socket?.Dispose();
            }
        }
    }
}