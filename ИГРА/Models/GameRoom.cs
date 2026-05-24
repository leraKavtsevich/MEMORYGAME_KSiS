namespace MemoryGame.Models
{
    public class GameRoom
    {
        public string RoomCode { get; set; } //код команаты
        public List<Player> Players { get; set; } = new(); //список игроков
        public List<Card> Board {  get; set; } = new(); //поле карточек
        public int CurrentPlayerIndex { get; set; } //индекс игрока, чей ход
        public bool GameStarted { get; set; } //начало игры
        public bool GameOver { get; set; } //конец игры
        public string WinnerId { get; set; } //id победителя 
        public int? PendingFirstCardIndex { get; set; } //индекс первой открытой карты
        public int BoardSize { get; set; } = 4; //размер поля
        public bool IsProcessing { get; set; } //обработка пары карт
    }
}
