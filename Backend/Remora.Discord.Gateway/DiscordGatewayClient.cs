//
//  DiscordGatewayClient.cs
//
//  Author:
//       Jarl Gullberg <jarl.gullberg@gmail.com>
//
//  Copyright (c) 2017 Jarl Gullberg
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Remora.Discord.API.Abstractions;
using Remora.Discord.API.Abstractions.Bidirectional;
using Remora.Discord.API.Abstractions.Commands;
using Remora.Discord.API.Abstractions.Events;
using Remora.Discord.API.API;
using Remora.Discord.API.API.Bidirectional;
using Remora.Discord.API.API.Commands;
using Remora.Discord.Core;
using Remora.Discord.Gateway.Responders;
using Remora.Discord.Gateway.Results;

namespace Remora.Discord.Gateway
{
    /// <summary>
    /// Represents a Discord Gateway client.
    /// </summary>
    public class DiscordGatewayClient
    {
        private readonly ILogger<DiscordGatewayClient> _log;

        private readonly IDiscordRestGatewayAPI _gatewayAPI;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ITokenStore _tokenStore;
        private readonly ClientWebSocket _clientWebSocket;
        private readonly Random _random;

        /// <summary>
        /// Holds payloads that have been submitted by the application, but have not yet been sent to the gateway.
        /// </summary>
        private readonly ConcurrentQueue<IPayload> _payloadsToSend;

        /// <summary>
        /// Holds payloads that have been received by the gateway, but not yet distributed to the application.
        /// </summary>
        private readonly ConcurrentQueue<IPayload> _receivedPayloads;

        /// <summary>
        /// Holds the various responders currently subscribed to the gateway.
        /// </summary>
        private readonly ConcurrentDictionary<IResponder, int> _responders;

        /// <summary>
        /// Holds the currently running responders.
        /// </summary>
        private readonly ConcurrentQueue<Task<EventResponseResult>> _runningResponders;

        /// <summary>
        /// Holds the connection status.
        /// </summary>
        private GatewayConnectionStatus _connectionStatus;

        /// <summary>
        /// Holds the last sequence number received by the gateway client.
        /// </summary>
        private int _lastSequenceNumber;

        /// <summary>
        /// Holds the time when the last heartbeat acknowledgement was received, using
        /// <see cref="DateTime.ToBinary()"/>.
        /// </summary>
        private long _lastReceivedHeartbeatAck;

        /// <summary>
        /// Holds the session ID.
        /// </summary>
        private string? _sessionID;

        /// <summary>
        /// Holds the cancellation token source for internal operations.
        /// </summary>
        private CancellationTokenSource _tokenSource;

        /// <summary>
        /// Holds the task responsible for sending payloads to the gateway.
        /// </summary>
        private Task<GatewaySenderResult> _sendTask;

        /// <summary>
        /// Holds the task responsible for receiving payloads from the gateway.
        /// </summary>
        private Task<GatewayReceiverResult> _receiveTask;

        /// <summary>
        /// Holds a value indicating that the client should reconnect and resume at its earliest convenience.
        /// </summary>
        private bool _shouldReconnectAndResume;

        /// <summary>
        /// Initializes a new instance of the <see cref="DiscordGatewayClient"/> class.
        /// </summary>
        /// <param name="gatewayAPI">The gateway API.</param>
        /// <param name="jsonOptions">The JSON options.</param>
        /// <param name="tokenStore">The token store.</param>
        /// <param name="random">An entropy source.</param>
        /// <param name="log">The logging instance.</param>
        public DiscordGatewayClient
        (
            IDiscordRestGatewayAPI gatewayAPI,
            JsonSerializerOptions jsonOptions,
            ITokenStore tokenStore,
            Random random,
            ILogger<DiscordGatewayClient> log
        )
        {
            _gatewayAPI = gatewayAPI;
            _jsonOptions = jsonOptions;
            _tokenStore = tokenStore;
            _random = random;
            _log = log;
            _clientWebSocket = new ClientWebSocket();
            _responders = new ConcurrentDictionary<IResponder, int>();
            _runningResponders = new ConcurrentQueue<Task<EventResponseResult>>();

            _payloadsToSend = new ConcurrentQueue<IPayload>();
            _receivedPayloads = new ConcurrentQueue<IPayload>();

            _connectionStatus = GatewayConnectionStatus.Offline;

            _tokenSource = new CancellationTokenSource();
            _sendTask = Task.FromResult(GatewaySenderResult.FromSuccess());
            _receiveTask = Task.FromResult(GatewayReceiverResult.FromSuccess());
        }

        /// <summary>
        /// Subscribes the given responder to events from the gateway.
        /// </summary>
        /// <param name="responder">The responder to subscribe.</param>
        public void SubscribeResponder(IResponder responder)
        {
            if (_responders.ContainsKey(responder))
            {
                return;
            }

            _responders.AddOrUpdate(responder, r => 0, (r, i) => 0);
        }

        /// <summary>
        /// Unsubscribes the given responder from events from the gateway.
        /// </summary>
        /// <param name="responder">The responder to unsubscribe.</param>
        public void UnsubscribeResponder(IResponder responder)
        {
            _responders.TryRemove(responder, out _);
        }

        /// <summary>
        /// Starts and connects the gateway client. This task will not complete until cancelled (or faulted),
        /// maintaining the connection for the duration of it.
        ///
        /// If the gateway client encounters a fatal problem during the execution of this task, it will return with a
        /// failed result. If a shutdown is requested, it will gracefully terminate the connection and return a
        /// successful result.
        /// </summary>
        /// <param name="ct">The cancellation token for this operation.</param>
        /// <returns>A gateway connection result which may or may not have succeeded.</returns>
        public async Task<GatewayConnectionResult> RunAsync(CancellationToken ct)
        {
            try
            {
                if (_connectionStatus != GatewayConnectionStatus.Offline)
                {
                    return GatewayConnectionResult.FromError("Already connected.");
                }

                // Until cancellation has been requested or we hit a fatal error, reconnections should be attempted.
                _tokenSource = new CancellationTokenSource();

                while (!ct.IsCancellationRequested)
                {
                    var iterationResult = await RunConnectionIterationAsync(ct);
                    if (iterationResult.IsSuccess)
                    {
                        continue;
                    }

                    // Something has gone wrong. Close the socket, and handle it
                    // Terminate the send and receive tasks
                    _tokenSource.Cancel();

                    // The results of the send and receive tasks are discarded here, because the iteration result will
                    // contain whichever of them failed if any of them did
                    _ = await _sendTask;
                    _ = await _receiveTask;

                    if (_clientWebSocket.State == WebSocketState.Open)
                    {
                        await _clientWebSocket.CloseAsync
                        (
                            WebSocketCloseStatus.NormalClosure,
                            "Terminating connection by user request.",
                            ct
                        );
                    }

                    // Finish up the responders
                    foreach (var runningResponder in _runningResponders)
                    {
                        await FinalizeResponderAsync(runningResponder);
                    }

                    switch (iterationResult.GatewayCloseStatus)
                    {
                        case GatewayCloseStatus.SessionTimedOut:
                        case GatewayCloseStatus.RateLimited:
                        case GatewayCloseStatus.InvalidSequence:
                        case GatewayCloseStatus.UnknownError:
                        {
                            // Reconnection is allowed, using a completely new session
                            _sessionID = null;
                            _connectionStatus = GatewayConnectionStatus.Disconnected;

                            continue;
                        }
                    }

                    switch (iterationResult.WebSocketCloseStatus)
                    {
                        case WebSocketCloseStatus.InternalServerError:
                        case WebSocketCloseStatus.EndpointUnavailable:
                        {
                            // Reconnection is allowed, using a completely new session
                            _sessionID = null;
                            _connectionStatus = GatewayConnectionStatus.Disconnected;

                            continue;
                        }
                    }

                    // Reconnection is not allowed.
                    return iterationResult;
                }
            }
            catch (Exception e)
            {
                return GatewayConnectionResult.FromError(e);
            }

            _connectionStatus = GatewayConnectionStatus.Offline;

            return GatewayConnectionResult.FromSuccess();
        }

        /// <summary>
        /// Runs a single iteration of the connection loop.
        /// </summary>
        /// <param name="ct">The cancellation token for this operation.</param>
        /// <returns>A connection result, based on the results of the iteration.</returns>
        private async Task<GatewayConnectionResult> RunConnectionIterationAsync(CancellationToken ct = default)
        {
            switch (_connectionStatus)
            {
                case GatewayConnectionStatus.Offline:
                case GatewayConnectionStatus.Disconnected:
                {
                    // Start connecting
                    var getGatewayEndpoint = await _gatewayAPI.GetGatewayBotAsync(ct);
                    if (!getGatewayEndpoint.IsSuccess)
                    {
                        return GatewayConnectionResult.FromError(getGatewayEndpoint);
                    }

                    var gatewayEndpoint = $"{getGatewayEndpoint.Entity.Url}?v=6&encoding=json";
                    if (!Uri.TryCreate(gatewayEndpoint, UriKind.Absolute, out var gatewayUri))
                    {
                        return GatewayConnectionResult.FromError
                        (
                            "Failed to parse the received gateway endpoint."
                        );
                    }

                    await _clientWebSocket.ConnectAsync(gatewayUri, ct);
                    switch (_clientWebSocket.State)
                    {
                        case WebSocketState.Open:
                        case WebSocketState.Connecting:
                        {
                            break;
                        }
                        default:
                        {
                            return GatewayConnectionResult.FromError("Failed to connect to the endpoint.");
                        }
                    }

                    var receiveHello = await ReceivePayloadAsync(ct);
                    if (!receiveHello.IsSuccess)
                    {
                        return GatewayConnectionResult.FromError(receiveHello);
                    }

                    if (!(receiveHello.Entity is Payload<IHello> hello))
                    {
                        // Not receiving a hello is a non-recoverable error
                        return GatewayConnectionResult.FromError
                        (
                            "The first payload from the gateway was not a hello. Rude!"
                        );
                    }

                    // Set up the send task
                    var heartbeatInterval = TimeSpan.FromMilliseconds(hello.Data.HeartbeatInterval);

                    _sendTask = Task.Factory.StartNew
                    (
                        () => GatewaySenderAsync(heartbeatInterval, _tokenSource.Token),
                        TaskCreationOptions.LongRunning
                    ).Unwrap();

                    // Attempt to connect or resume
                    var connectResult = await AttemptConnectionAsync(ct);
                    if (!connectResult.IsSuccess)
                    {
                        return connectResult;
                    }

                    // Now, set up the receive task and start receiving events normally
                    _receiveTask = Task.Factory.StartNew
                    (
                        () => GatewayReceiverAsync(_tokenSource.Token),
                        TaskCreationOptions.LongRunning
                    ).Unwrap();

                    _connectionStatus = GatewayConnectionStatus.Connected;
                    break;
                }
                case GatewayConnectionStatus.Connected:
                {
                    // Process received events and dispatch them to the application
                    if (_receivedPayloads.TryDequeue(out var payload))
                    {
                        UnwrapAndDispatchEvent(payload, _tokenSource.Token);
                    }

                    // Unpack one of the running responders, if any are pending
                    if (_runningResponders.TryDequeue(out var runningResponder))
                    {
                        if (runningResponder.IsCompleted)
                        {
                            await FinalizeResponderAsync(runningResponder);
                        }
                        else
                        {
                            _runningResponders.Enqueue(runningResponder);
                        }
                    }

                    // Check the send and receive tasks for errors
                    if (_sendTask.IsCompleted)
                    {
                        var sendResult = await _sendTask;
                        if (!sendResult.IsSuccess)
                        {
                            return GatewayConnectionResult.FromError(sendResult);
                        }
                    }

                    if (_receiveTask.IsCompleted)
                    {
                        var receiveResult = await _receiveTask;
                        if (!receiveResult.IsSuccess)
                        {
                            return GatewayConnectionResult.FromError(receiveResult);
                        }
                    }

                    break;
                }
            }

            if (!_shouldReconnectAndResume)
            {
                return GatewayConnectionResult.FromSuccess();
            }

            // Terminate the send and receive tasks
            _tokenSource.Cancel();

            // The results of the send and receive tasks are discarded here, because we know that it's going to be a
            // cancellation
            _ = await _sendTask;
            _ = await _receiveTask;

            if (_clientWebSocket.State == WebSocketState.Open)
            {
                await _clientWebSocket.CloseAsync
                (
                    WebSocketCloseStatus.NormalClosure,
                    "Terminating connection by user request.",
                    ct
                );
            }

            // Set up the state for the new connection
            _tokenSource = new CancellationTokenSource();
            _connectionStatus = GatewayConnectionStatus.Disconnected;

            _shouldReconnectAndResume = false;

            return GatewayConnectionResult.FromSuccess();
        }

        /// <summary>
        /// Finalizes the given running responder, awaiting it and logging its results.
        /// </summary>
        /// <param name="runningResponder">The running responder.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        private async Task FinalizeResponderAsync(Task<EventResponseResult> runningResponder)
        {
            try
            {
                var responderResult = await runningResponder;
                if (!responderResult.IsSuccess)
                {
                    if (responderResult.Exception is null)
                    {
                        _log.LogWarning
                        (
                            "Error in gateway event responder.",
                            responderResult.ErrorReason
                        );
                    }
                    else
                    {
                        _log.LogWarning
                        (
                            "Error in gateway event responder.",
                            responderResult.Exception
                        );
                    }
                }
            }
            catch (Exception e)
            {
                _log.LogWarning("Error in gateway event responder.", e);
            }
        }

        /// <summary>
        /// Unwraps the given payload into its typed representation, dispatching all events for it.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <param name="ct">The cancellation token for the dispatched event.</param>
        private void UnwrapAndDispatchEvent(IPayload payload, CancellationToken ct = default)
        {
            var dispatchMethod = GetType().GetMethod
            (
                nameof(DispatchEvent),
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            if (dispatchMethod is null)
            {
                throw new MissingMethodException(nameof(DiscordGatewayClient), nameof(DispatchEvent));
            }

            var payloadType = payload.GetType();
            if (!payloadType.IsGenericType || payloadType.GetGenericTypeDefinition() != typeof(Payload<>))
            {
                throw new ArgumentException("The given payload was not compatible with the event dispatcher.");
            }

            var boundDispatchMethod = dispatchMethod.MakeGenericMethod(payloadType.GetGenericArguments());
            boundDispatchMethod.Invoke(this, new object?[] { payload, ct });
        }

        /// <summary>
        /// Dispatches the given event to all relevant gateway event responders.
        /// </summary>
        /// <param name="gatewayEvent">The event to dispatch.</param>
        /// <param name="ct">The cancellation token to use.</param>
        /// <typeparam name="TGatewayEvent">The gateway event.</typeparam>
        private void DispatchEvent<TGatewayEvent>(Payload<TGatewayEvent> gatewayEvent, CancellationToken ct = default)
            where TGatewayEvent : IGatewayEvent
        {
            var relevantResponders = _responders.Keys
                .Where(r => r is IResponder<TGatewayEvent>)
                .Cast<IResponder<TGatewayEvent>>();

            foreach (var relevantResponder in relevantResponders)
            {
                _runningResponders.Enqueue(Task.Run(() => relevantResponder.RespondAsync(gatewayEvent.Data, ct), ct));
            }
        }

        /// <summary>
        /// Attempts to identify or resume the gateway connection.
        /// </summary>
        /// <param name="ct">The cancellation token for this operation.</param>
        /// <returns>A connection result which may or may not have succeeded.</returns>
        private Task<GatewayConnectionResult> AttemptConnectionAsync(CancellationToken ct = default)
        {
            if (_sessionID is null)
            {
                // We've never connected before
                return CreateNewSessionAsync(ct);
            }

            return ResumeExistingSessionAsync(ct);
        }

        /// <summary>
        /// Creates a new session with the gateway, identifying the client.
        /// </summary>
        /// <param name="ct">The cancellation token for this operation.</param>
        /// <returns>A connection result which may or may not have succeeded.</returns>
        private async Task<GatewayConnectionResult> CreateNewSessionAsync(CancellationToken ct = default)
        {
            var identifyPayload = new Payload<Identify>
            (
                new Identify
                (
                    _tokenStore.Token,
                    new ConnectionProperties("Remora.Discord"),
                    intents: GatewayIntents.DirectMessages,
                    compress: false
                )
            );

            _payloadsToSend.Enqueue(identifyPayload);

            var receiveReady = await ReceivePayloadAsync(ct);
            if (!receiveReady.IsSuccess)
            {
                return GatewayConnectionResult.FromError(receiveReady);
            }

            if (!(receiveReady.Entity is Payload<IReady> ready))
            {
                return GatewayConnectionResult.FromError
                (
                    "The payload after identification was not a Ready payload."
                );
            }

            _sessionID = ready.Data.SessionID;
            return GatewayConnectionResult.FromSuccess();
        }

        /// <summary>
        /// Resumes an existing session with the gateway, replaying missed events.
        /// </summary>
        /// <param name="ct">The cancellation token for this operation.</param>
        /// <returns>A connection result which may or may not have succeeded.</returns>
        private async Task<GatewayConnectionResult> ResumeExistingSessionAsync(CancellationToken ct = default)
        {
            if (_sessionID is null)
            {
                return GatewayConnectionResult.FromError("There's no previous session to resume.");
            }

            var resumePayload = new Payload<Resume>
            (
                new Resume
                (
                    _tokenStore.Token,
                    _sessionID,
                    _lastSequenceNumber
                )
            );

            _payloadsToSend.Enqueue(resumePayload);

            var receiveFirstEvent = await ReceivePayloadAsync(ct);
            if (!receiveFirstEvent.IsSuccess)
            {
                return GatewayConnectionResult.FromError(receiveFirstEvent);
            }

            if (receiveFirstEvent.Entity is Payload<IInvalidSession>)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(1000, 5000)), ct);
                return await CreateNewSessionAsync(ct);
            }

            // Push resumed events onto the queue
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    return GatewayConnectionResult.FromError("Operation was cancelled.");
                }

                var receiveEvent = await ReceivePayloadAsync(ct);
                if (!receiveEvent.IsSuccess)
                {
                    return GatewayConnectionResult.FromError(receiveEvent);
                }

                if (receiveEvent.Entity is Payload<IResumed>)
                {
                    return GatewayConnectionResult.FromSuccess();
                }

                _receivedPayloads.Enqueue(receiveEvent.Entity);
            }
        }

        /// <summary>
        /// This method acts as the main entrypoint for the gateway sender task. It processes payloads that are
        /// submitted by the application to the gateway, sending them to it.
        /// </summary>
        /// <param name="heartbeatInterval">The interval at which heartbeats should be sent.</param>
        /// <param name="ct">The cancellation token for this operation.</param>
        /// <returns>A sender result which may or may not have been successful. A failed result indicates that something
        /// has gone wrong when sending a payload, and that the connection has been deemed nonviable. A nonviable
        /// connection should be either terminated, reestablished, or resumed as appropriate.</returns>
        private async Task<GatewaySenderResult> GatewaySenderAsync
        (
            TimeSpan heartbeatInterval,
            CancellationToken ct = default
        )
        {
            DateTime? lastHeartbeat = null;
            while (!ct.IsCancellationRequested)
            {
                var lastHeartbeatAck = DateTime.FromBinary(Interlocked.Read(ref _lastReceivedHeartbeatAck));

                // Heartbeat, if required
                var now = DateTime.UtcNow;
                if (lastHeartbeat is null || now - lastHeartbeat >= heartbeatInterval - TimeSpan.FromMilliseconds(100))
                {
                    if (lastHeartbeatAck < lastHeartbeat)
                    {
                        return GatewaySenderResult.FromError
                        (
                            "The server did not respond in time with a heartbeat acknowledgement.",
                            GatewayCloseStatus.SessionTimedOut
                        );
                    }

                    // 32-bit reads are atomic, so this is fine
                    var lastSequenceNumber = _lastSequenceNumber;

                    var heartbeatPayload = new Payload<IHeartbeat>
                    (
                        new Heartbeat
                        (
                            lastSequenceNumber == 0 ? (long?)null : lastSequenceNumber
                        )
                    );

                    var sendHeartbeat = await SendPayloadAsync(heartbeatPayload, ct);

                    if (!sendHeartbeat.IsSuccess)
                    {
                        return GatewaySenderResult.FromError(sendHeartbeat);
                    }

                    lastHeartbeat = DateTime.UtcNow;
                }

                // Check if there are any user-submitted payloads to send
                if (!_payloadsToSend.TryDequeue(out var payload))
                {
                    // Let's sleep for a little while
                    var maxSleepTime = (lastHeartbeat.Value + heartbeatInterval) - now;
                    var sleepTime = TimeSpan.FromMilliseconds(Math.Clamp(100, 0, maxSleepTime.TotalMilliseconds));

                    await Task.Delay(sleepTime, ct);
                    continue;
                }

                var sendResult = await SendPayloadAsync(payload, ct);
                if (!sendResult.IsSuccess)
                {
                    return GatewaySenderResult.FromError(sendResult);
                }
            }

            return GatewaySenderResult.FromSuccess();
        }

        /// <summary>
        /// This method acts as the main entrypoint for the gateway receiver task. It processes payloads that are
        /// sent from the gateway to the application, submitting them to it.
        /// </summary>
        /// <param name="ct">The cancellation token for this operation.</param>
        /// <returns>A receiver result which may or may not have been successful. A failed result indicates that
        /// something has gone wrong when receiving a payload, and that the connection has been deemed nonviable. A
        /// nonviable connection should be either terminated, reestablished, or resumed as appropriate.</returns>
        private async Task<GatewayReceiverResult> GatewayReceiverAsync(CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested)
            {
                var receivedPayload = await ReceivePayloadAsync(ct);
                if (!receivedPayload.IsSuccess)
                {
                    return GatewayReceiverResult.FromError(receivedPayload);
                }

                // Update the sequence number
                if (receivedPayload.Entity is IEventPayload eventPayload)
                {
                    Interlocked.Exchange(ref _lastSequenceNumber, eventPayload.SequenceNumber);
                }

                // Update the ack timestamp
                if (receivedPayload.Entity is Payload<IHeartbeatAcknowledge>)
                {
                    Interlocked.Exchange(ref _lastReceivedHeartbeatAck, DateTime.UtcNow.ToBinary());
                }

                // Signal the governor task that a reconnection is requested, if necessary.
                if (receivedPayload.Entity is Payload<IReconnect>)
                {
                    _shouldReconnectAndResume = true;
                }

                _receivedPayloads.Enqueue(receivedPayload.Entity);
            }

            return GatewayReceiverResult.FromSuccess();
        }

        /// <summary>
        /// Asynchronously sends a payload to the websocket.
        /// </summary>
        /// <param name="payload">The payload.</param>
        /// <param name="ct">The cancellation token for this operation.</param>
        /// <returns>A send result which may or may not have succeeded.</returns>
        private async Task<SendPayloadResult> SendPayloadAsync(IPayload payload, CancellationToken ct = default)
        {
            if (_clientWebSocket.State != WebSocketState.Open)
            {
                return SendPayloadResult.FromError("The socket was not open.");
            }

            await using var memoryStream = new MemoryStream();

            byte[]? buffer = null;
            try
            {
                await JsonSerializer.SerializeAsync(memoryStream, payload, _jsonOptions, ct);

                if (memoryStream.Length > 4096)
                {
                    return SendPayloadResult.FromError
                    (
                        "The payload was too large to be accepted by the gateway."
                    );
                }

                buffer = ArrayPool<byte>.Shared.Rent((int)memoryStream.Length);
                memoryStream.Seek(0, SeekOrigin.Begin);

                // Copy the data
                var bufferSegment = new ArraySegment<byte>(buffer, 0, (int)memoryStream.Length);
                await memoryStream.ReadAsync(bufferSegment, ct);

                // Send the whole payload as one chunk
                await _clientWebSocket.SendAsync(bufferSegment, WebSocketMessageType.Text, true, ct);

                if (_clientWebSocket.CloseStatus.HasValue)
                {
                    if (Enum.IsDefined(typeof(GatewayCloseStatus), (int)_clientWebSocket.CloseStatus))
                    {
                        return SendPayloadResult.FromError
                        (
                            "The gateway closed the connection.",
                            (GatewayCloseStatus)_clientWebSocket.CloseStatus
                        );
                    }

                    return SendPayloadResult.FromError
                    (
                        _clientWebSocket.CloseStatusDescription,
                        _clientWebSocket.CloseStatus.Value
                    );
                }
            }
            catch (Exception e)
            {
                return SendPayloadResult.FromError(e);
            }
            finally
            {
                if (!(buffer is null))
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            return SendPayloadResult.FromSuccess();
        }

        /// <summary>
        /// Asynchronously receives a payload from the websocket.
        /// </summary>
        /// <param name="ct">The cancellation token for this operation.</param>
        /// <returns>A receive result which may or may not have succeeded.</returns>
        private async Task<ReceivePayloadResult<IPayload>> ReceivePayloadAsync(CancellationToken ct = default)
        {
            if (_clientWebSocket.State != WebSocketState.Open)
            {
                return ReceivePayloadResult<IPayload>.FromError("The socket was not open.");
            }

            await using var memoryStream = new MemoryStream();

            var buffer = ArrayPool<byte>.Shared.Rent(4096);

            try
            {
                WebSocketReceiveResult result;

                do
                {
                    result = await _clientWebSocket.ReceiveAsync(buffer, ct);

                    if (result.CloseStatus.HasValue)
                    {
                        if (Enum.IsDefined(typeof(GatewayCloseStatus), (int)result.CloseStatus))
                        {
                            return ReceivePayloadResult<IPayload>.FromError
                            (
                                "The gateway closed the connection.",
                                (GatewayCloseStatus)result.CloseStatus
                            );
                        }

                        return ReceivePayloadResult<IPayload>.FromError
                        (
                            result.CloseStatusDescription,
                            result.CloseStatus.Value
                        );
                    }

                    await memoryStream.WriteAsync(buffer, 0, result.Count, ct);
                }
                while (!result.EndOfMessage);

                memoryStream.Seek(0, SeekOrigin.Begin);

                var payload = await JsonSerializer.DeserializeAsync<IPayload>(memoryStream, _jsonOptions, ct);
                return ReceivePayloadResult<IPayload>.FromSuccess(payload);
            }
            catch (Exception ex)
            {
                return ReceivePayloadResult<IPayload>.FromError("Failed to receive a payload.", ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
