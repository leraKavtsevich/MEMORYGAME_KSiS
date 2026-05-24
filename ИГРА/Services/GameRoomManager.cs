using System.Collections.Concurrent;
using MemoryGame.Models;

namespace MemoryGame.Services
{
    public class GameRoomManager
    {
        private readonly ConcurrentDictionary<string, GameRoom> _rooms = new();
        private static readonly Random _random = new();
        private const string RoomCodeChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        private readonly string[] _imagePaths;

        public GameRoomManager()
        {
            _imagePaths = new string[32];
            for (int i = 0; i < 32; i++)
            {
                _imagePaths[i] = $"/images/card{i + 1}.jpg";
            }
        }

        // Создает новую игровую комнату с уникальным кодом
        public GameRoom CreateRoom(string playerName, string connectionId, int boardSize = 4)
        {
            if (boardSize != 4 && boardSize != 6 && boardSize != 8)
            {
                boardSize = 4;
            }

            string roomCode;
            do
            {
                roomCode = GenerateRoomCode();
            } while (_rooms.ContainsKey(roomCode));

            var room = new GameRoom
            {
                RoomCode = roomCode,
                GameStarted = false,
                GameOver = false,
                CurrentPlayerIndex = 0,
                PendingFirstCardIndex = null,
                BoardSize = boardSize
            };

            var player = new Player
            {
                ConnectionId = connectionId,
                Name = playerName,
                Score = 0,
                IsOwner = true
            };

            room.Players.Add(player);
            _rooms.TryAdd(roomCode, room);

            return room;
        }

        // Перезапускает игру: сбрасывает состояние и обнуляет очки
        public void RestartGame(GameRoom room)
        {
            room.GameStarted = false;
            room.GameOver = false;
            room.WinnerId = null;
            room.CurrentPlayerIndex = 0;
            room.PendingFirstCardIndex = null;
            room.IsProcessing = false;

            foreach (var player in room.Players)
            {
                player.Score = 0;
            }

            InitializeBoard(room);
        }

        // Добавляет игрока в существующую комнату (максимум 4 игрока)
        public GameRoom JoinRoom(string roomCode, string playerName, string connectionId)
        {
            if (!_rooms.TryGetValue(roomCode.ToUpper(), out var room))
                return null;

            if (room.GameStarted)
                return null;

            if (room.Players.Count >= 4)
                return null;

            var player = new Player
            {
                ConnectionId = connectionId,
                Name = playerName,
                Score = 0,
                IsOwner = false
            };

            room.Players.Add(player);
            return room;
        }

        // Возвращает комнату по коду
        public GameRoom GetRoom(string roomCode)
        {
            _rooms.TryGetValue(roomCode.ToUpper(), out var room);
            return room;
        }

        // Удаляет комнату из хранилища
        public bool RemoveRoom(string roomCode)
        {
            return _rooms.TryRemove(roomCode.ToUpper(), out _);
        }

        // Возвращает все активные комнаты
        public IEnumerable<GameRoom> GetAllRooms()
        {
            return _rooms.Values;
        }

        // Генерирует случайный 4-символьный код комнаты
        private string GenerateRoomCode()
        {
            var code = new char[4];
            for (int i = 0; i < 4; i++)
            {
                code[i] = RoomCodeChars[_random.Next(RoomCodeChars.Length)];
            }
            return new string(code);
        }

        // Создает и перемешивает колоду карт для указанной комнаты
        public void InitializeBoard(GameRoom room)
        {
            var cards = new List<Card>();
            int totalCards = room.BoardSize * room.BoardSize;
            int pairCount = totalCards / 2;

            for (int pairId = 0; pairId < pairCount; pairId++)
            {
                string imagePath = _imagePaths[pairId % _imagePaths.Length];

                cards.Add(new Card
                {
                    Id = pairId * 2,
                    PairId = pairId,
                    IsFlipped = false,
                    IsMatched = false,
                    ImagePath = imagePath
                });

                cards.Add(new Card
                {
                    Id = pairId * 2 + 1,
                    PairId = pairId,
                    IsFlipped = false,
                    IsMatched = false,
                    ImagePath = imagePath
                });
            }

            // Алгоритм Фишера-Йетса для перемешивания
            for (int i = cards.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (cards[i], cards[j]) = (cards[j], cards[i]);
            }

            room.Board = cards;
        }

        // Возвращает текущего игрока в комнате
        public Player GetCurrentPlayer(GameRoom room)
        {
            if (room.Players.Count == 0) return null;
            return room.Players[room.CurrentPlayerIndex];
        }

        // Переключает ход на следующего игрока
        public void NextPlayer(GameRoom room)
        {
            if (room.Players.Count == 0) return;
            room.CurrentPlayerIndex = (room.CurrentPlayerIndex + 1) % room.Players.Count;
        }

        // Проверяет, закончена ли игра, и определяет победителя
        public bool CheckGameOver(GameRoom room)
        {
            bool allMatched = room.Board.All(c => c.IsMatched);
            if (allMatched)
            {
                room.GameOver = true;

                int maxScore = room.Players.Max(p => p.Score);
                var winners = room.Players.Where(p => p.Score == maxScore).ToList();

                if (winners.Count > 1)
                {
                    room.WinnerId = "DRAW";
                }
                else
                {
                    room.WinnerId = winners.First().ConnectionId;
                }
                return true;
            }
            return false;
        }

        // Удаляет игрока из комнаты, корректирует ход при необходимости
        public bool RemovePlayer(GameRoom room, string connectionId)
        {
            var playerIndex = room.Players.FindIndex(p => p.ConnectionId == connectionId);
            if (playerIndex == -1) return false;

            bool wasCurrentPlayer = (playerIndex == room.CurrentPlayerIndex);
            room.Players.RemoveAt(playerIndex);

            if (room.GameStarted && !room.GameOver && room.Players.Count > 0)
            {
                if (wasCurrentPlayer)
                {
                    if (playerIndex <= room.CurrentPlayerIndex && room.CurrentPlayerIndex > 0)
                    {
                        room.CurrentPlayerIndex--;
                    }
                    if (room.CurrentPlayerIndex >= room.Players.Count)
                    {
                        room.CurrentPlayerIndex = 0;
                    }

                    // Сбрасываем незавершенный ход при удалении текущего игрока
                    if (room.IsProcessing)
                    {
                        var flippedCards = room.Board
                            .Where(c => c.IsFlipped && !c.IsMatched)
                            .ToList();
                        foreach (var card in flippedCards)
                        {
                            card.IsFlipped = false;
                        }
                        room.PendingFirstCardIndex = null;
                        room.IsProcessing = false;
                    }
                }
                else
                {
                    if (playerIndex < room.CurrentPlayerIndex)
                    {
                        room.CurrentPlayerIndex--;
                    }
                }
            }

            return wasCurrentPlayer;
        }
    }
}