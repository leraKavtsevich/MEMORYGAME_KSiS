namespace MemoryGame.Models
{
    public class Player
    {
        public string ConnectionId { get; set; } //уникальный Id подключения
        public string Name { get; set; } //имя
        public int Score { get; set; } //счет
        public bool IsOwner { get; set; } //создатель комнаты 
    }
}
