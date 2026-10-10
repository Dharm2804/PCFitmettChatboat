using System.ComponentModel.DataAnnotations;

namespace PCFitmettChatboat.Models
{
    public class JsonRecord
    {
        public int Id { get; set; }

        [Url]
        public string CurrentUrl { get; set; } = string.Empty;

        public string PreviousUrl { get; set; } = string.Empty;

        public bool Flagupdate { get; set; }

        public bool Delete { get; set; }
    }
}
