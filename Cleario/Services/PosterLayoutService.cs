namespace Cleario.Services
{
    public readonly struct PosterLayoutMetrics
    {
        public PosterLayoutMetrics(
            double browseWidth,
            double browseHeight,
            double homeWidth,
            double homeHeight,
            double detailsWidth,
            double detailsMaxHeight)
        {
            BrowseWidth = browseWidth;
            BrowseHeight = browseHeight;
            HomeWidth = homeWidth;
            HomeHeight = homeHeight;
            DetailsWidth = detailsWidth;
            DetailsMaxHeight = detailsMaxHeight;
        }

        public double BrowseWidth { get; }
        public double BrowseHeight { get; }
        public double HomeWidth { get; }
        public double HomeHeight { get; }
        public double DetailsWidth { get; }
        public double DetailsMaxHeight { get; }
    }

    public static class PosterLayoutService
    {
        public static PosterLayoutMetrics GetCurrent()
        {
            return SettingsManager.PosterSize switch
            {
                PosterSizeMode.Compact => new PosterLayoutMetrics(166, 244, 200, 294, 300, 192),
                PosterSizeMode.Large => new PosterLayoutMetrics(202, 297, 244, 359, 345, 221),
                PosterSizeMode.ExtraLarge => new PosterLayoutMetrics(220, 323, 266, 391, 370, 237),
                _ => new PosterLayoutMetrics(184, 270, 222, 326, 325, 208)
            };
        }
    }
}
