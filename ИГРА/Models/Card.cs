namespace MemoryGame.Models
{
    public class Card
    {
        public int Id { get; set; } //уникальный id
        public int PairId { get; set; } //парный id
        public bool IsFlipped { get; set; } //открыта ли карточка
        public bool IsMatched { get; set; } //уже найдена пара
        public string ImagePath { get; set; } //путь к картинке

    }
}
