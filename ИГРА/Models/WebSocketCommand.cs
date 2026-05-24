namespace MemoryGame.Models
{
    public class WebSocketCommand
    {
        public string Type { get; set; } //тип команды
        public string ConnectionId { get; set; } //id подключения клиента
        public string RoomCode { get; set; } //код комнаты
        public string PlayerName { get; set; } //имя игрока
        public int BoardSize { get; set; } //размер игрового поля
        public int CardIndex { get; set; } //индекс карточки
        public int Score { get; set; } //счет игрока
    }
}