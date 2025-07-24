namespace KaspaBot.Infrastructure.Options
{
    public class MexcOptions
    {
        public const string SectionName = "Mexc";

        public required string ApiKey { get; set; }
        public required string ApiSecret { get; set; }
    }
}