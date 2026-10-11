namespace PCFitmettChatboat.Models
{
    public class PdfRecord
    {
        public int Id { get; set; }

        public string FileName { get; set; } = string.Empty;

        public string PreviousFileName { get; set; } = string.Empty;

        public bool Flagupdate { get; set; }

        public bool Delete { get; set; }
    }
}
