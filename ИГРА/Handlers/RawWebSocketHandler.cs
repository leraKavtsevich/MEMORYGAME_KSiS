using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using MemoryGame.Services;
using MemoryGame.Models;

namespace MemoryGame.Handlers
{
    public class RawWebSocketHandler
    {
        private readonly GameRoomManager _roomManager;

        private static readonly ConcurrentDictionary<string, TcpClient> _clients = new();
        private static readonly ConcurrentDictionary<string, string> _connectionToRoom = new();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _clientLocks = new();

        public RawWebSocketHandler(GameRoomManager roomManager)
        {
            _roomManager = roomManager;
        }

        // Запускает TCP сервер на указанном порту и принимает входящие соединения
        public async Task StartServer(int port)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Console.WriteLine($"[RawWebSocket] Сервер запущен на порту {port}");

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClient(client));
            }
        }

        // Обрабатывает одного клиента: WebSocket handshake, регистрация, цикл сообщений
        private async Task HandleClient(TcpClient tcpClient)
        {
            var stream = tcpClient.GetStream();
            string connectionId = null;

            try
            {
                // WebSocket Handshake
                byte[] buffer = new byte[4096];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                string httpRequest = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                string wsKey = ExtractWebSocketKey(httpRequest);
                if (wsKey == null)
                {
                    await SendHttpResponse(stream, 400, "Bad Request");
                    return;
                }

                string acceptKey = ComputeWebSocketAccept(wsKey);

                string handshakeResponse =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(handshakeResponse));
                Console.WriteLine("[RawWebSocket] Handshake успешен!");

                // Регистрация клиента
                byte[] frameData = await ReadWebSocketFrame(stream);
                if (frameData == null) return;

                string firstMessage = Encoding.UTF8.GetString(frameData);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var tempCommand = JsonSerializer.Deserialize<WebSocketCommand>(firstMessage, options);

                if (tempCommand?.ConnectionId != null)
                {
                    connectionId = tempCommand.ConnectionId;

                    // При переподключении закрываем старое соединение
                    if (_clients.TryRemove(connectionId, out var oldClient))
                    {
                        Console.WriteLine($"[RawWebSocket] Переподключение клиента: {connectionId}, закрываем старое соединение");
                        try { oldClient.Close(); } catch { }
                    }

                    _clients[connectionId] = tcpClient;
                    _clientLocks[connectionId] = new SemaphoreSlim(1, 1);

                    Console.WriteLine($"[RawWebSocket] Клиент зарегистрирован: {connectionId}");

                    await SendWebSocketFrame(stream, JsonSerializer.Serialize(new
                    {
                        type = "connected",
                        connectionId = connectionId
                    }));

                    // Обрабатываем первое сообщение если это не ping
                    if (tempCommand.Type != null && tempCommand.Type != "ping")
                    {
                        await ProcessMessage(connectionId, firstMessage);
                    }
                }
                else
                {
                    Console.WriteLine($"[RawWebSocket] Клиент не прислал ConnectionId, отключаем");
                    return;
                }

                // Основной цикл обработки сообщений
                while (tcpClient.Connected)
                {
                    byte[] data = await ReadWebSocketFrame(stream);
                    if (data == null) break;

                    string message = Encoding.UTF8.GetString(data);
                    Console.WriteLine($"[RawWebSocket] Получено от {connectionId}: {message}");

                    await ProcessMessage(connectionId, message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RawWebSocket] Ошибка клиента {connectionId ?? "unknown"}: {ex.Message}");
            }
            finally
            {
                if (connectionId != null)
                {
                    await HandleDisconnect(connectionId);
                    _clients.TryRemove(connectionId, out _);
                    if (_clientLocks.TryRemove(connectionId, out var sem))
                        sem.Dispose();
                }
                try { tcpClient.Close(); } catch { }
            }
        }

        // Читает WebSocket фрейм согласно RFC 6455
        private async Task<byte[]> ReadWebSocketFrame(NetworkStream stream)
        {
            byte[] header = new byte[2];
            int read = await ReadExact(stream, header, 2);
            if (read < 2) return null;

            int opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            int payloadLen = header[1] & 0x7F;

            if (payloadLen == 126)
            {
                byte[] ext = new byte[2];
                await ReadExact(stream, ext, 2);
                payloadLen = (ext[0] << 8) | ext[1];
            }
            else if (payloadLen == 127)
            {
                byte[] ext = new byte[8];
                await ReadExact(stream, ext, 8);
                payloadLen = (int)(((long)ext[0] << 56) | ((long)ext[1] << 48) |
                                   ((long)ext[2] << 40) | ((long)ext[3] << 32) |
                                   ((long)ext[4] << 24) | ((long)ext[5] << 16) |
                                   ((long)ext[6] << 8) | ext[7]);
            }

            byte[] mask = new byte[4];
            if (masked)
                await ReadExact(stream, mask, 4);

            byte[] payload = new byte[payloadLen];
            await ReadExact(stream, payload, payloadLen);

            // Размаскировка XOR
            if (masked)
            {
                for (int i = 0; i < payloadLen; i++)
                    payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            }

            if (opcode == 8) return null; // Close frame
            if (opcode == 9) return new byte[0]; // Ping frame

            return payload;
        }

        // Отправляет WebSocket фрейм клиенту
        private async Task SendWebSocketFrame(NetworkStream stream, string message)
        {
            byte[] data = Encoding.UTF8.GetBytes(message);

            using var ms = new MemoryStream();
            ms.WriteByte(0x81); // FIN=1, Opcode=1 (text)

            if (data.Length <= 125)
            {
                ms.WriteByte((byte)data.Length);
            }
            else if (data.Length <= 65535)
            {
                ms.WriteByte(126);
                ms.WriteByte((byte)(data.Length >> 8));
                ms.WriteByte((byte)(data.Length & 0xFF));
            }
            else
            {
                ms.WriteByte(127);
                long len = data.Length;
                for (int i = 7; i >= 0; i--)
                    ms.WriteByte((byte)((len >> (i * 8)) & 0xFF));
            }

            ms.Write(data, 0, data.Length);

            byte[] frame = ms.ToArray();
            await stream.WriteAsync(frame, 0, frame.Length);
        }

        // Читает точное количество байт из потока
        private async Task<int> ReadExact(NetworkStream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset);
                if (read == 0) return offset;
                offset += read;
            }
            return offset;
        }

        // Извлекает WebSocket ключ из HTTP запроса handshake
        private string ExtractWebSocketKey(string httpRequest)
        {
            string search = "Sec-WebSocket-Key: ";
            int start = httpRequest.IndexOf(search);
            if (start == -1) return null;
            start += search.Length;
            int end = httpRequest.IndexOf("\r\n", start);
            return httpRequest.Substring(start, end - start).Trim();
        }

        // Вычисляет Accept ключ для WebSocket handshake
        private string ComputeWebSocketAccept(string key)
        {
            const string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            using var sha1 = SHA1.Create();
            byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(key + magic));
            return Convert.ToBase64String(hash);
        }

        // Отправляет HTTP ответ с указанным кодом
        private async Task SendHttpResponse(NetworkStream stream, int code, string message)
        {
            string response = $"HTTP/1.1 {code} {message}\r\nContent-Length: 0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        }

        // Обрабатывает входящее сообщение, маршрутизирует по типу команды
        private async Task ProcessMessage(string connectionId, string message)
        {
            if (string.IsNullOrWhiteSpace(message) || message.Length == 0) return;

            try
            {
                if (message.Contains("\"type\":\"ping\""))
                {
                    await SendToClient(connectionId, new { type = "pong" });
                    return;
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var command = JsonSerializer.Deserialize<WebSocketCommand>(message, options);
                if (command == null) return;

                Console.WriteLine($"[RawWebSocket] Команда от {connectionId}: {command.Type}");

                switch (command.Type)
                {
                    case "register": break;
                    case "create_room": await CreateRoom(connectionId, command); break;
                    case "restart_game": await RestartGame(connectionId, command); break;
                    case "join_room": await JoinRoom(connectionId, command); break;
                    case "start_game": await StartGame(connectionId, command); break;
                    case "flip_card": await FlipCard(connectionId, command); break;
                    case "next_turn": await NextTurn(connectionId, command); break;
                    case "force_update": await ForceUpdate(connectionId, command); break;
                    default:
                        Console.WriteLine($"[RawWebSocket] Неизвестная команда: {command.Type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RawWebSocket] Ошибка обработки сообщения: {ex.Message}\nСообщение: {message}");
                try { await SendToClient(connectionId, new { type = "error", message = ex.Message }); } catch { }
            }
        }

        // Создает новую игровую комнату
        private async Task CreateRoom(string connectionId, WebSocketCommand command)
        {
            int boardSize = command.BoardSize;
            if (boardSize != 4 && boardSize != 6 && boardSize != 8) boardSize = 4;

            var room = _roomManager.CreateRoom(command.PlayerName, connectionId, boardSize);

            _connectionToRoom[connectionId] = room.RoomCode;

            await SendToClient(connectionId, new
            {
                type = "room_created",
                roomCode = room.RoomCode,
                boardSize = room.BoardSize
            });

            await UpdatePlayersList(room.RoomCode);
        }

        // Перезапускает игру в комнате (только для создателя)
        private async Task RestartGame(string connectionId, WebSocketCommand command)
        {
            var room = _roomManager.GetRoom(command.RoomCode);
            if (room == null) return;

            var creator = room.Players.FirstOrDefault();
            if (creator?.ConnectionId != connectionId) return;

            _roomManager.RestartGame(room);

            await SendToRoom(command.RoomCode, new
            {
                type = "game_restarted",
                boardSize = room.BoardSize
            });

            await UpdatePlayersList(command.RoomCode);
        }

        // Добавляет игрока в существующую комнату
        private async Task JoinRoom(string connectionId, WebSocketCommand command)
        {
            var room = _roomManager.GetRoom(command.RoomCode);
            if (room == null)
            {
                await SendToClient(connectionId, new { type = "error", message = "Комната не найдена" });
                return;
            }

            if (room.GameStarted)
            {
                await SendToClient(connectionId, new { type = "error", message = "Игра уже началась" });
                return;
            }

            if (room.Players.Count >= 4)
            {
                await SendToClient(connectionId, new { type = "error", message = "Комната заполнена" });
                return;
            }

            // Проверяем, не присоединился ли уже этот connectionId
            if (room.Players.Any(p => p.ConnectionId == connectionId))
            {
                Console.WriteLine($"[RawWebSocket] Клиент {connectionId} уже в комнате {command.RoomCode}");
                await SendToClient(connectionId, new { type = "board_size_info", boardSize = room.BoardSize });
                await UpdatePlayersList(command.RoomCode);
                return;
            }

            var result = _roomManager.JoinRoom(command.RoomCode, command.PlayerName, connectionId);
            if (result == null)
            {
                await SendToClient(connectionId, new { type = "error", message = "Не удалось присоединиться" });
                return;
            }

            _connectionToRoom[connectionId] = command.RoomCode;

            Console.WriteLine($"[RawWebSocket] Игрок {command.PlayerName} ({connectionId}) присоединился к комнате {command.RoomCode}");

            await SendToClient(connectionId, new { type = "board_size_info", boardSize = room.BoardSize });
            await UpdatePlayersList(command.RoomCode);

            if (room.Players.Count >= 2 && !room.GameStarted)
            {
                await SendToRoom(command.RoomCode, new { type = "ready_to_start" });
            }
        }

        // Запускает игру (только для создателя комнаты)
        private async Task StartGame(string connectionId, WebSocketCommand command)
        {
            var room = _roomManager.GetRoom(command.RoomCode);
            if (room == null || room.GameStarted) return;

            var creator = room.Players.FirstOrDefault();
            if (creator?.ConnectionId != connectionId) return;

            _roomManager.InitializeBoard(room);
            room.GameStarted = true;
            room.CurrentPlayerIndex = 0;
            room.PendingFirstCardIndex = null;
            room.IsProcessing = false;

            var boardData = room.Board.Select(c => new
            {
                c.Id,
                c.PairId,
                c.IsFlipped,
                c.IsMatched,
                c.ImagePath
            }).ToList();

            var currentPlayer = _roomManager.GetCurrentPlayer(room);

            await SendToRoom(command.RoomCode, new
            {
                type = "game_started",
                boardData = boardData,
                firstPlayerId = currentPlayer?.ConnectionId,
                boardSize = room.BoardSize
            });

            await UpdatePlayersList(command.RoomCode);
        }

        // Обрабатывает переворот карточки игроком
        private async Task FlipCard(string connectionId, WebSocketCommand command)
        {
            var room = _roomManager.GetRoom(command.RoomCode);
            if (room == null || !room.GameStarted || room.GameOver || room.IsProcessing) return;

            var currentPlayer = _roomManager.GetCurrentPlayer(room);
            if (currentPlayer?.ConnectionId != connectionId) return;
            if (command.CardIndex < 0 || command.CardIndex >= room.Board.Count) return;

            var card = room.Board[command.CardIndex];
            if (card.IsFlipped || card.IsMatched) return;

            if (room.PendingFirstCardIndex == null)
            {
                // Первая карта в ходе
                card.IsFlipped = true;
                room.PendingFirstCardIndex = command.CardIndex;

                await SendToRoom(command.RoomCode, new
                {
                    type = "card_flipped",
                    cardIndex = command.CardIndex,
                    imagePath = card.ImagePath
                });
            }
            else
            {
                // Вторая карта в ходе - проверяем совпадение
                int firstIndex = room.PendingFirstCardIndex.Value;
                var firstCard = room.Board[firstIndex];
                card.IsFlipped = true;

                await SendToRoom(command.RoomCode, new
                {
                    type = "card_flipped",
                    cardIndex = command.CardIndex,
                    imagePath = card.ImagePath
                });

                bool isMatch = (firstCard.PairId == card.PairId);
                room.PendingFirstCardIndex = null;

                if (isMatch)
                {
                    firstCard.IsMatched = true;
                    card.IsMatched = true;
                    currentPlayer.Score++;

                    await SendToRoom(command.RoomCode, new
                    {
                        type = "match_found",
                        firstCardIndex = firstIndex,
                        secondCardIndex = command.CardIndex,
                        playerName = currentPlayer.Name,
                        score = currentPlayer.Score
                    });

                    await UpdatePlayersList(command.RoomCode);

                    if (_roomManager.CheckGameOver(room))
                    {
                        await SendToRoom(command.RoomCode, new { type = "game_over", winnerId = room.WinnerId });
                    }
                    else
                    {
                        await SendToRoom(command.RoomCode, new { type = "turn_changed", nextPlayerId = currentPlayer.ConnectionId });
                    }
                }
                else
                {
                    room.IsProcessing = true;
                    await SendToRoom(command.RoomCode, new
                    {
                        type = "no_match",
                        firstCardIndex = firstIndex,
                        secondCardIndex = command.CardIndex
                    });
                }
            }
        }

        // Переключает ход на следующего игрока
        private async Task NextTurn(string connectionId, WebSocketCommand command)
        {
            var room = _roomManager.GetRoom(command.RoomCode);
            if (room == null) return;

            var currentPlayer = _roomManager.GetCurrentPlayer(room);
            if (currentPlayer?.ConnectionId != connectionId) return;
            if (room.GameOver) return;
            if (!room.IsProcessing) return;

            // Закрываем неподошедшие карты
            var flippedCards = room.Board
                .Select((card, idx) => new { card, idx })
                .Where(x => x.card.IsFlipped && !x.card.IsMatched)
                .ToList();

            foreach (var item in flippedCards) item.card.IsFlipped = false;

            room.PendingFirstCardIndex = null;
            _roomManager.NextPlayer(room);
            room.IsProcessing = false;

            var nextPlayer = _roomManager.GetCurrentPlayer(room);
            await UpdatePlayersList(command.RoomCode);

            await SendToRoom(command.RoomCode, new { type = "turn_changed", nextPlayerId = nextPlayer?.ConnectionId });
        }

        // Принудительно обновляет состояние клиента
        private async Task ForceUpdate(string connectionId, WebSocketCommand command)
        {
            await UpdatePlayersList(command.RoomCode);

            var room = _roomManager.GetRoom(command.RoomCode);
            if (room != null)
            {
                await SendToClient(connectionId, new { type = "board_size_info", boardSize = room.BoardSize });

                if (room.GameStarted && !room.GameOver)
                {
                    var boardData = room.Board.Select(c => new
                    {
                        c.Id,
                        c.PairId,
                        c.IsFlipped,
                        c.IsMatched,
                        c.ImagePath
                    }).ToList();

                    await SendToClient(connectionId, new
                    {
                        type = "game_state",
                        boardData = boardData,
                        currentPlayerId = _roomManager.GetCurrentPlayer(room)?.ConnectionId,
                        boardSize = room.BoardSize
                    });
                }
            }
        }

        // Обновляет список игроков для всех в комнате
        private async Task UpdatePlayersList(string roomCode)
        {
            var room = _roomManager.GetRoom(roomCode);
            if (room == null) return;

            var currentPlayerId = room.CurrentPlayerIndex < room.Players.Count
                ? room.Players[room.CurrentPlayerIndex].ConnectionId
                : null;

            var playersInfo = room.Players.Select(p => new
            {
                connectionId = p.ConnectionId,
                name = p.Name,
                score = p.Score,
                isCurrent = p.ConnectionId == currentPlayerId,
                isOwner = p.IsOwner
            }).ToList();

            await SendToRoom(roomCode, new { type = "players_updated", players = playersInfo });
        }

        // Обрабатывает отключение клиента: удаляет из комнаты, переключает ход
        private async Task HandleDisconnect(string connectionId)
        {
            Console.WriteLine($"[RawWebSocket] Клиент отключен: {connectionId}");

            _connectionToRoom.TryRemove(connectionId, out string roomCode);

            if (roomCode != null)
            {
                var room = _roomManager.GetRoom(roomCode);
                if (room != null)
                {
                    var player = room.Players.FirstOrDefault(p => p.ConnectionId == connectionId);
                    if (player != null)
                    {
                        string playerName = player.Name;

                        bool wasCurrentPlayer = _roomManager.RemovePlayer(room, connectionId);

                        await SendToRoom(roomCode, new { type = "player_left", playerName = playerName });
                        await UpdatePlayersList(roomCode);

                        if (wasCurrentPlayer && room.GameStarted && !room.GameOver && room.Players.Count > 0)
                        {
                            var nextPlayer = _roomManager.GetCurrentPlayer(room);
                            await SendToRoom(roomCode, new
                            {
                                type = "turn_changed",
                                nextPlayerId = nextPlayer?.ConnectionId
                            });
                        }
                    }

                    if (room.Players.Count == 0)
                        _roomManager.RemoveRoom(roomCode);
                }
            }
        }

        // Отправляет сообщение конкретному клиенту (потокобезопасно)
        private async Task SendToClient(string connectionId, object data)
        {
            if (!_clients.TryGetValue(connectionId, out var client)) return;
            if (!_clientLocks.TryGetValue(connectionId, out var semaphore)) return;
            if (!client.Connected) return;

            await semaphore.WaitAsync();
            try
            {
                var stream = client.GetStream();
                var json = JsonSerializer.Serialize(data);
                await SendWebSocketFrame(stream, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RawWebSocket] Ошибка отправки клиенту {connectionId}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        }

        // Отправляет сообщение всем игрокам в комнате
        private async Task SendToRoom(string roomCode, object data)
        {
            var room = _roomManager.GetRoom(roomCode);
            if (room == null) return;

            // Создаем копию списка игроков для безопасной итерации
            List<string> playerIds;
            lock (room)
            {
                playerIds = room.Players.Select(p => p.ConnectionId).ToList();
            }

            var tasks = playerIds.Select(id => SendToClient(id, data));
            await Task.WhenAll(tasks);
        }
    }
}