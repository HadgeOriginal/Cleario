using Cleario.Models;

namespace Cleario.Models
{
    public sealed class DetailsNavigationRequest
    {
        public MetaItem Item { get; set; } = new();
        public int? SeasonNumber { get; set; }
        public string VideoId { get; set; } = string.Empty;
    }
}
