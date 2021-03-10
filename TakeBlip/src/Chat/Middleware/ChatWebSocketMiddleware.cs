using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Chat.Middleware
{
    public class ChatWebSocketMiddleware
    {
        private static ConcurrentDictionary<string, WebSocket> _sockets = new ConcurrentDictionary<string, WebSocket>();

        private readonly RequestDelegate _next;

        public ChatWebSocketMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await _next.Invoke(context);
                return;
            }

            var notifyCancellationToken = context.RequestAborted;

            var currentSocket = await context.WebSockets.AcceptWebSocketAsync();

            var socketIdentity = context.Request.Query["identity"].FirstOrDefault();

            if(_sockets.Any(x => x.Key.Contains(socketIdentity)))
            {
                await currentSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Já existe alguém com esse nickname", notifyCancellationToken);
                currentSocket.Dispose();
                return;
            }

            _sockets.TryAdd(socketIdentity, currentSocket);

            foreach (var socket in _sockets)
            {
                if (socket.Value.State != WebSocketState.Open)
                    continue;

                await EnviarMensagem(socket.Value, $"{socketIdentity} : Está conectado(a)!", notifyCancellationToken);
            }

            while (true)
            {
                if (notifyCancellationToken.IsCancellationRequested)
                    break;

                var response = await ReceberMensagem(currentSocket, notifyCancellationToken);

                if (string.IsNullOrEmpty(response))
                {
                    if (currentSocket.State != WebSocketState.Open)
                        break;

                    continue;
                }

                var dadosEnviados = response.Split("|");

                var origem = dadosEnviados.FirstOrDefault();

                var complemento = string.Empty;

                if (dadosEnviados.ElementAt(1).Contains(" "))
                    complemento = response.Substring(response.IndexOf(" "), response.Length - response.IndexOf(" ")).TrimStart();
                else
                    complemento = dadosEnviados.ElementAt(1);

                var mensagemTratada = $"{origem} : {complemento}";

                if (dadosEnviados.Length > 1 && dadosEnviados.ElementAt(1).ToArray().First() == '@')
                {
                    var destino = dadosEnviados.ElementAt(1).Substring(dadosEnviados.ElementAt(1).IndexOf("@") + 1, 
                        dadosEnviados.ElementAt(1).IndexOf(" ") - 1).TrimEnd();

                    var destinatario = _sockets.Where(x => x.Key == destino).Select(x => x.Value).FirstOrDefault();

                    await EnviarMensagem(destinatario, mensagemTratada, notifyCancellationToken);
                }
                else
                {
                    foreach (var socket in _sockets)
                    {
                        if (socket.Value.State != WebSocketState.Open)
                            continue;

                        await EnviarMensagem(socket.Value, mensagemTratada, notifyCancellationToken);
                    }
                }
            }

            WebSocket dummy;

            _sockets.TryRemove(socketIdentity, out dummy);

            await currentSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fechando", notifyCancellationToken);
            currentSocket.Dispose();

        }

        private static Task EnviarMensagem(WebSocket socket, string mensagem, CancellationToken notifyCancellationToken = default(CancellationToken))
        {
            var buffer = Encoding.UTF8.GetBytes(mensagem);
            var segment = new ArraySegment<byte>(buffer);

            return socket.SendAsync(segment, WebSocketMessageType.Text, true, notifyCancellationToken);
        }

        private static async Task<string> ReceberMensagem(WebSocket socket, CancellationToken notifyCancellationToken = default(CancellationToken))
        {
            var buffer = new ArraySegment<byte>(new byte[8192]);

            var ms = new MemoryStream();

            WebSocketReceiveResult result;

            do
            {
                notifyCancellationToken.ThrowIfCancellationRequested();

                result = await socket.ReceiveAsync(buffer, notifyCancellationToken);

                ms.Write(buffer.Array, buffer.Offset, result.Count);
            }
            while (!result.EndOfMessage);

            ms.Seek(0, SeekOrigin.Begin);

            if (result.MessageType != WebSocketMessageType.Text)
                return null;

            var reader = new StreamReader(ms, Encoding.UTF8);

            return await reader.ReadToEndAsync();

        }
    }
}
