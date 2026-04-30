namespace Cleario.Models
{
    public sealed class DiscoverNavigationRequest
    {
        public string Type { get; set; } = string.Empty;
        public string CatalogId { get; set; } = string.Empty;
        public string SourceBaseUrl { get; set; } = string.Empty;
    }
}
